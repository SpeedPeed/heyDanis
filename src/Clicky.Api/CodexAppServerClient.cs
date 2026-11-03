using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Clicky.Capture;

namespace Clicky.Api;

/// <summary>
/// Streams responses through the local Codex app-server. Auth is owned by Codex,
/// so this can use Sign in with ChatGPT / Codex OAuth instead of an OpenAI API key.
/// </summary>
public sealed class CodexAppServerClient : ILlmClient, IAsyncDisposable, IDisposable
{
    private readonly string _model;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<int, PendingRequest> _pending = new();
    private readonly object _writeLock = new();
    private readonly Task _readerTask;
    private readonly Process _process;
    private int _nextId;
    private string? _threadId;
    private bool _disposed;

    public CodexAppServerClient(string model = "gpt-5.5", string? workingDirectory = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-5.5" : model;
        _workingDirectory = workingDirectory
            ?? AppContext.BaseDirectory
            ?? Environment.CurrentDirectory;

        _process = StartCodexAppServer(_workingDirectory);
        _readerTask = Task.Run(ReadLoopAsync);

        InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public async IAsyncEnumerable<string> SendAsync(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string systemPrompt,
        string userText,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _turnLock.WaitAsync(ct).ConfigureAwait(false);

        var tempFiles = new List<string>();
        var channel = Channel.CreateUnbounded<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnNotification(JsonElement message)
        {
            if (!message.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var method = methodElement.GetString();
            if (method == "item/agentMessage/delta" &&
                message.TryGetProperty("params", out var deltaParams))
            {
                var delta = ExtractAgentMessageDelta(deltaParams);
                if (!string.IsNullOrEmpty(delta))
                    channel.Writer.TryWrite(delta);
            }
            else if (method == "turn/completed")
            {
                channel.Writer.TryComplete();
                completed.TrySetResult();
            }
        }

        NotificationReceived += OnNotification;

        try
        {
            if (_threadId is null)
            {
                var threadResult = await RequestAsync("thread/start", new
                {
                    model = _model,
                    cwd = _workingDirectory,
                    approvalPolicy = "never",
                    sandbox = "workspace-write",
                    serviceName = "clicky_windows"
                }, ct).ConfigureAwait(false);

                _threadId = threadResult
                    .GetProperty("thread")
                    .GetProperty("id")
                    .GetString();
            }

            var input = BuildTurnInput(history, screens, systemPrompt, userText, tempFiles);
            _ = RequestAsync("turn/start", new
            {
                threadId = _threadId,
                input
            }, ct).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    channel.Writer.TryComplete(task.Exception?.GetBaseException());
                    completed.TrySetException(task.Exception?.GetBaseException() ?? task.Exception!);
                }
            }, CancellationToken.None);

            await foreach (var delta in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return delta;
            }

            await completed.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            NotificationReceived -= OnNotification;
            foreach (var file in tempFiles)
            {
                try { File.Delete(file); } catch { }
            }
            _turnLock.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await RequestAsync("initialize", new
        {
            clientInfo = new
            {
                name = "clicky_windows",
                title = "Clicky Windows",
                version = "0.1.0"
            },
            capabilities = new { experimentalApi = true }
        }, ct).ConfigureAwait(false);

        Notify("initialized", new { });

        var account = await RequestAsync("account/read", new { refreshToken = false }, ct).ConfigureAwait(false);
        if (!account.TryGetProperty("account", out var accountElement) ||
            accountElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                "Codex is not signed in. Run `codex` or open the root Codex OAuth test page and sign in with ChatGPT.");
        }
    }

    private static List<object> BuildTurnInput(
        IReadOnlyList<Message> history,
        IReadOnlyList<CapturedScreen> screens,
        string systemPrompt,
        string userText,
        List<string> tempFiles)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("follow these instructions for this response:");
        prompt.AppendLine(systemPrompt);

        if (history.Count > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("recent conversation:");
            foreach (var turn in history.TakeLast(10))
            {
                prompt.AppendLine($"user: {turn.UserText}");
                prompt.AppendLine($"assistant: {turn.AssistantText}");
            }
        }

        prompt.AppendLine();
        prompt.AppendLine("current user request:");
        prompt.AppendLine(userText);

        var input = new List<object>
        {
            new { type = "text", text = prompt.ToString() }
        };

        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            var path = SaveTempImage(screen.ImageBytes, i + 1);
            tempFiles.Add(path);
            input.Add(new { type = "text", text = screen.Label });
            input.Add(new { type = "localImage", path });
        }

        return input;
    }

    private static string SaveTempImage(byte[] imageBytes, int index)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Clicky", "codex-screens");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"screen-{index}-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, imageBytes);
        return path;
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        ThrowIfDisposed();

        var id = Interlocked.Increment(ref _nextId);
        var pending = new PendingRequest();
        _pending[id] = pending;

        Send(new { method, id, @params = parameters });

        await using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var request))
                request.Completion.TrySetCanceled(ct);
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var timeoutRegistration = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var request))
                request.Completion.TrySetException(new TimeoutException($"{method} timed out"));
        });

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private void Notify(string method, object parameters)
        => Send(new { method, @params = parameters });

    private void Send(object message)
    {
        var json = JsonSerializer.Serialize(message);
        lock (_writeLock)
        {
            _process.StandardInput.WriteLine(json);
            _process.StandardInput.Flush();
        }
    }

    private event Action<JsonElement>? NotificationReceived;

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_process.HasExited)
            {
                var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement.Clone();

                if (root.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind == JsonValueKind.Number &&
                    idElement.TryGetInt32(out var id) &&
                    _pending.TryRemove(id, out var pending))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        var message = error.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString()
                            : error.ToString();
                        pending.Completion.TrySetException(new InvalidOperationException(message));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        pending.Completion.TrySetResult(result.Clone());
                    }
                    else
                    {
                        pending.Completion.TrySetResult(default);
                    }
                }
                else
                {
                    NotificationReceived?.Invoke(root);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var entry in _pending)
            {
                if (_pending.TryRemove(entry.Key, out var pending))
                    pending.Completion.TrySetException(ex);
            }
        }
    }

    internal static string? ExtractAgentMessageDelta(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.String)
            return parameters.GetString();

        if (parameters.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var key in new[] { "delta", "textDelta", "text" })
        {
            if (parameters.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        if (parameters.TryGetProperty("item", out var item) &&
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("text", out var itemText) &&
            itemText.ValueKind == JsonValueKind.String)
        {
            return itemText.GetString();
        }

        return null;
    }

    private static Process StartCodexAppServer(string workingDirectory)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /s /c \"codex app-server\"",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
            : new ProcessStartInfo
            {
                FileName = "codex",
                ArgumentList = { "app-server" },
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start codex app-server.");

        _ = Task.Run(async () =>
        {
            try
            {
                while (!process.HasExited)
                {
                    var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;
                    Debug.WriteLine($"[CodexAppServerClient] {line}");
                }
            }
            catch { }
        });

        return process;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CodexAppServerClient));
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { }

        _process.Dispose();
        _turnLock.Dispose();
    }

    private sealed class PendingRequest
    {
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
