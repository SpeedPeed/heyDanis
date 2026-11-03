namespace Clicky.Hotkey;

/// <summary>
/// Push-to-talk chord transition. Mirrors BuddyPushToTalkShortcut.ShortcutTransition
/// in BuddyDictationManager.swift.
/// </summary>
public enum ShortcutTransition
{
    None,
    Pressed,
    Released,
}
