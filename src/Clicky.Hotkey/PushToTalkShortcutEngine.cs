namespace Clicky.Hotkey;

/// <summary>
/// Pure (testable) state machine that turns a stream of low-level key
/// events into <see cref="ShortcutTransition"/>s for a modifier-only chord.
///
/// Mirrors the modifier-only branch of BuddyPushToTalkShortcut.shortcutTransition
/// in BuddyDictationManager.swift: the chord is considered "pressed" as long as
/// every modifier it requires is currently held (superset semantics — extra
/// modifiers or other keys being down do not matter).
///
/// This class is intentionally free of any Win32 P/Invoke so the transition
/// logic can be unit-tested without installing a real keyboard hook.
/// </summary>
public sealed class PushToTalkShortcutEngine
{
    // Windows virtual-key codes. WH_KEYBOARD_LL reports the left/right
    // distinguished codes (VK_LCONTROL vs VK_RCONTROL), not the generic
    // VK_CONTROL — so we track each side independently.
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4; // Left Alt
    public const int VK_RMENU = 0xA5; // Right Alt
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    private readonly PushToTalkShortcut _chord;
    private bool _ctrl;
    private bool _alt;
    private bool _shift;
    private bool _win;
    private bool _wasPressed;

    public PushToTalkShortcutEngine(PushToTalkShortcut chord)
    {
        _chord = chord;
    }

    /// <summary>True if the chord is currently held.</summary>
    public bool IsPressed => _wasPressed;

    /// <summary>
    /// Feed a single key event into the engine and get the resulting transition.
    /// </summary>
    /// <param name="virtualKeyCode">The Win32 VK_* code from KBDLLHOOKSTRUCT.vkCode.</param>
    /// <param name="isKeyDown">True for WM_KEYDOWN / WM_SYSKEYDOWN, false for WM_KEYUP / WM_SYSKEYUP.</param>
    public ShortcutTransition Process(int virtualKeyCode, bool isKeyDown)
    {
        if (!UpdateModifierState(virtualKeyCode, isKeyDown))
        {
            // Non-modifier keys don't change chord state, but Mac also ignores
            // them for modifier-only chords (flagsChanged only). Return None.
            return ShortcutTransition.None;
        }

        var nowPressed = IsChordSatisfied();

        if (nowPressed && !_wasPressed)
        {
            _wasPressed = true;
            return ShortcutTransition.Pressed;
        }

        if (!nowPressed && _wasPressed)
        {
            _wasPressed = false;
            return ShortcutTransition.Released;
        }

        return ShortcutTransition.None;
    }

    /// <summary>
    /// Reset internal state. Call when the hook restarts so a stale "pressed"
    /// flag from before the restart doesn't suppress the next real press.
    /// </summary>
    public void Reset()
    {
        _ctrl = _alt = _shift = _win = false;
        _wasPressed = false;
    }

    private bool UpdateModifierState(int vk, bool down)
    {
        switch (vk)
        {
            case VK_LCONTROL:
            case VK_RCONTROL:
                _ctrl = down;
                return true;
            case VK_LMENU:
            case VK_RMENU:
                _alt = down;
                return true;
            case VK_LSHIFT:
            case VK_RSHIFT:
                _shift = down;
                return true;
            case VK_LWIN:
            case VK_RWIN:
                _win = down;
                return true;
            default:
                return false;
        }
    }

    private bool IsChordSatisfied() => _chord switch
    {
        PushToTalkShortcut.ControlAlt => _ctrl && _alt,
        PushToTalkShortcut.ControlShift => _ctrl && _shift,
        PushToTalkShortcut.AltShift => _alt && _shift,
        PushToTalkShortcut.WinAlt => _win && _alt,
        _ => false,
    };
}
