using System.Runtime.InteropServices;
using System.Windows;

namespace HeyDanis.Companion;

/// <summary>
/// Types dictated text into whatever text field currently has focus (Word, browser,
/// IDE, chat …) the most compatible way on Windows: put it on the clipboard, send
/// Strg+V, then restore the user's previous clipboard text.
/// Must be called on the WPF UI thread (the clipboard needs an STA thread).
/// </summary>
public static class FocusedTextInserter
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Time the target app gets to read the clipboard before we restore it.</summary>
    private static readonly TimeSpan PasteSettleTime = TimeSpan.FromMilliseconds(250);

    public static async Task InsertAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        string? previousClipboardText = null;
        try
        {
            if (Clipboard.ContainsText())
                previousClipboardText = Clipboard.GetText();
        }
        catch (COMException)
        {
            // Another app holds the clipboard — we just won't restore it.
        }

        await SetClipboardTextWithRetryAsync(text).ConfigureAwait(true);
        SendControlV();
        await Task.Delay(PasteSettleTime).ConfigureAwait(true);

        if (previousClipboardText is not null)
        {
            try
            {
                await SetClipboardTextWithRetryAsync(previousClipboardText).ConfigureAwait(true);
            }
            catch
            {
                // Best effort — the dictated text simply stays on the clipboard.
            }
        }
    }

    /// <summary>
    /// The clipboard is a shared resource; if another app has it open for a moment,
    /// setting it throws. A few short retries fix that in practice.
    /// </summary>
    private static async Task SetClipboardTextWithRetryAsync(string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                await Task.Delay(40).ConfigureAwait(true);
            }
        }
    }

    private static void SendControlV()
    {
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, keyUp: false),
            KeyInput(VK_V, keyUp: false),
            KeyInput(VK_V, keyUp: true),
            KeyInput(VK_CONTROL, keyUp: true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort virtualKey, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // The union must be as large as its biggest member (MOUSEINPUT) or SendInput rejects the size.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
