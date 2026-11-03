using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Clicky.Hotkey;

/// <summary>
/// Installs a system-wide WH_KEYBOARD_LL hook that watches for the configured
/// push-to-talk chord and publishes Pressed/Released transitions to a
/// <see cref="Channel{T}"/> the caller can read from.
///
/// Mirrors GlobalPushToTalkShortcutMonitor.swift on Mac: listen-only (never
/// swallows keys), installed from the UI thread, and safe to restart.
///
/// Threading: WH_KEYBOARD_LL callbacks execute on the thread that called
/// <see cref="SetWindowsHookEx"/> — in practice the WPF dispatcher thread, which
/// already pumps messages. The hook must therefore be installed from that thread
/// and <see cref="Dispose"/> must also be called on it so the callback delegate
/// isn't torn down while Windows is still invoking it.
/// </summary>
public sealed class GlobalPushToTalkHook : IDisposable
{
    private readonly Channel<ShortcutTransition> _channel;
    private readonly PushToTalkShortcutEngine _engine;

    // The delegate is held as a field so the GC can't collect it while the
    // hook is installed. Collecting it would crash the process the next time
    // Windows calls into managed code.
    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _disposed;

    public GlobalPushToTalkHook(PushToTalkShortcut chord)
    {
        _engine = new PushToTalkShortcutEngine(chord);
        _channel = Channel.CreateUnbounded<ShortcutTransition>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>Read transitions as they happen. Safe to await from any thread.</summary>
    public ChannelReader<ShortcutTransition> Transitions => _channel.Reader;

    /// <summary>
    /// Install the hook. Idempotent: a second Start() while the hook is already
    /// installed is a no-op, mirroring the guard in GlobalPushToTalkShortcutMonitor.start().
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GlobalPushToTalkHook));
        if (_hookId != IntPtr.Zero) return;

        _engine.Reset();
        _proc = HookCallback;

        // For WH_KEYBOARD_LL the module handle must be the module that contains
        // the hook procedure. Grabbing the current process's main module is the
        // canonical pattern and works for both framework-dependent and
        // self-contained .NET 8 WPF apps.
        IntPtr hMod;
        using (var process = Process.GetCurrentProcess())
        using (var module = process.MainModule!)
        {
            hMod = NativeMethods.GetModuleHandle(module.ModuleName);
        }

        _hookId = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hookId == IntPtr.Zero)
        {
            _proc = null;
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetWindowsHookEx(WH_KEYBOARD_LL) failed");
        }
    }

    /// <summary>
    /// Uninstall the hook. Safe to call multiple times. Leaves the channel
    /// open so consumers can finish draining any buffered transitions.
    /// </summary>
    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _proc = null;
        _engine.Reset();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0 means "don't process, just forward" (MSDN contract).
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            var isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var transition = _engine.Process((int)data.vkCode, isDown);
                if (transition != ShortcutTransition.None)
                {
                    // Unbounded single-writer channel — TryWrite always succeeds.
                    _channel.Writer.TryWrite(transition);
                }
            }
        }

        // Always listen-only: forward the event untouched so no keystrokes are
        // swallowed. This mirrors the `.listenOnly` flag on Mac's CGEvent tap.
        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _channel.Writer.TryComplete();
    }
}
