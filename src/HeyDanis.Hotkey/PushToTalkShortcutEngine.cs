namespace HeyDanis.Hotkey;

/// <summary>
/// Pure (testable) state machine that turns a stream of low-level key
/// events into <see cref="ShortcutTransition"/>s for a modifier-only chord.
///
/// The chord is "pressed" while every modifier it requires is held. Two rules make
/// it safe on a German keyboard while programming:
/// <list type="bullet">
/// <item>"Alt" means the LEFT Alt key only. AltGr (right Alt) is sent by Windows as
/// a fake left Ctrl + right Alt, so without this rule typing @ € { } [ ] \ | ~ would
/// trigger push-to-talk.</item>
/// <item>Pressing any non-modifier key while the chord is held (Strg+Alt+L, Strg+Alt+Entf …)
/// cancels it — the user wanted a normal shortcut.</item>
/// </list>
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
    private bool _leftAlt;
    private bool _rightAlt;
    private bool _shift;
    private bool _win;
    private bool _wasPressed;

    // After a cancel we stay quiet until the chord's modifiers are released,
    // so letting go of Strg+Alt after Strg+Alt+L doesn't start a recording.
    private bool _suppressedUntilChordReleased;

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
            // A real key while the chord is held → it was a normal keyboard shortcut.
            if (isKeyDown && _wasPressed)
            {
                _wasPressed = false;
                _suppressedUntilChordReleased = true;
                return ShortcutTransition.Cancelled;
            }
            return ShortcutTransition.None;
        }

        var nowPressed = IsChordSatisfied();

        if (_suppressedUntilChordReleased)
        {
            if (!RequiredModifiersHeld())
                _suppressedUntilChordReleased = false;
            return ShortcutTransition.None;
        }

        // An extra modifier joined a held chord (Strg+Shift → Strg+Shift+Alt): that's a
        // different shortcut, not "let go" — cancel instead of releasing.
        if (_wasPressed && !nowPressed && RequiredModifiersHeld())
        {
            _wasPressed = false;
            _suppressedUntilChordReleased = true;
            return ShortcutTransition.Cancelled;
        }

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
        _ctrl = _leftAlt = _rightAlt = _shift = _win = false;
        _wasPressed = false;
        _suppressedUntilChordReleased = false;
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
                _leftAlt = down;
                return true;
            case VK_RMENU:
                // AltGr. Tracked only so it counts as a modifier (not as "another key").
                _rightAlt = down;
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

    private bool IsChordSatisfied() => RequiredModifiersHeld() && !ExcludedModifierHeld();

    private bool RequiredModifiersHeld() => _chord switch
    {
        PushToTalkShortcut.ControlAlt => _ctrl && _leftAlt,
        PushToTalkShortcut.ControlShift => _ctrl && _shift,
        PushToTalkShortcut.AltShift => _leftAlt && _shift,
        PushToTalkShortcut.WinAlt => _win && _leftAlt,
        _ => false,
    };

    /// <summary>
    /// Modifiers that turn the chord into something else. AltGr always (special characters);
    /// Strg+Alt and Strg+Shift exclude each other so push-to-talk and dictation never fire together.
    /// </summary>
    private bool ExcludedModifierHeld() => _rightAlt || _chord switch
    {
        PushToTalkShortcut.ControlAlt => _shift,
        PushToTalkShortcut.ControlShift => _leftAlt,
        _ => false,
    };
}
