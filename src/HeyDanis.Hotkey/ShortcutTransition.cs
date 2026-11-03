namespace HeyDanis.Hotkey;

/// <summary>
/// Push-to-talk chord transition. Mirrors BuddyPushToTalkShortcut.ShortcutTransition
/// in BuddyDictationManager.swift.
/// </summary>
public enum ShortcutTransition
{
    None,
    Pressed,
    Released,

    /// <summary>
    /// Another key was pressed while the chord was held (e.g. Strg+Alt+L in an IDE):
    /// the user meant a normal shortcut, not push-to-talk. Discard the recording silently.
    /// No <see cref="Released"/> follows a cancel.
    /// </summary>
    Cancelled,
}
