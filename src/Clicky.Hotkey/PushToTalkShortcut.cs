namespace Clicky.Hotkey;

/// <summary>
/// Configurable push-to-talk chord. Mirrors BuddyPushToTalkShortcut.ShortcutOption
/// in the Mac codebase. Default is ControlAlt (the Windows equivalent of
/// macOS control+option).
/// </summary>
public enum PushToTalkShortcut
{
    ControlAlt,
    ControlShift,
    AltShift,
    WinAlt,
}

public static class PushToTalkShortcutExtensions
{
    /// <summary>
    /// Human-readable label shown in the companion panel.
    /// </summary>
    public static string DisplayName(this PushToTalkShortcut shortcut) => shortcut switch
    {
        PushToTalkShortcut.ControlAlt => "Ctrl + Alt",
        PushToTalkShortcut.ControlShift => "Ctrl + Shift",
        PushToTalkShortcut.AltShift => "Alt + Shift",
        PushToTalkShortcut.WinAlt => "Win + Alt",
        _ => "Ctrl + Alt",
    };
}
