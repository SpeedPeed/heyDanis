namespace HeyDanis.Hotkey;

/// <summary>
/// What one key event means for heyDanis' three shortcuts.
/// </summary>
/// <param name="PushToTalk">Strg+Alt held: ask by voice.</param>
/// <param name="Dictation">Strg+Shift held: dictate into the focused text field.</param>
/// <param name="TextInputRequested">Strg+Alt+Leertaste: open the text question box.</param>
/// <param name="SwallowKey">True if the key must not reach the focused app (the Space of Strg+Alt+Leertaste).</param>
public readonly record struct HotkeyEngineResult(
    ShortcutTransition PushToTalk,
    ShortcutTransition Dictation,
    bool TextInputRequested,
    bool SwallowKey);

/// <summary>
/// Pure (testable) combination of all heyDanis shortcuts on top of
/// <see cref="PushToTalkShortcutEngine"/>:
/// <list type="bullet">
/// <item>Strg+Alt (left Alt) held → push-to-talk</item>
/// <item>Strg+Alt+Leertaste → text input (cancels the just-started push-to-talk)</item>
/// <item>Strg+Shift held → dictation</item>
/// </list>
/// </summary>
public sealed class HeyDanisHotkeyEngine
{
    public const int VK_SPACE = 0x20;

    private readonly PushToTalkShortcutEngine _pushToTalk = new(PushToTalkShortcut.ControlAlt);
    private readonly PushToTalkShortcutEngine _dictation = new(PushToTalkShortcut.ControlShift);
    private bool _swallowNextSpaceUp;

    public bool IsPushToTalkPressed => _pushToTalk.IsPressed;
    public bool IsDictationPressed => _dictation.IsPressed;

    public HotkeyEngineResult Process(int virtualKeyCode, bool isKeyDown)
    {
        if (virtualKeyCode == VK_SPACE)
        {
            if (isKeyDown && _pushToTalk.IsPressed)
            {
                // Strg+Alt+Leertaste: the push-to-talk recording that started on Strg+Alt is
                // discarded (Cancelled) and the text box opens instead.
                var pushToTalk = _pushToTalk.Process(virtualKeyCode, isKeyDown);
                var dictationDuringSpace = _dictation.Process(virtualKeyCode, isKeyDown);
                _swallowNextSpaceUp = true;
                return new HotkeyEngineResult(pushToTalk, dictationDuringSpace, TextInputRequested: true, SwallowKey: true);
            }

            if (!isKeyDown && _swallowNextSpaceUp)
            {
                _swallowNextSpaceUp = false;
                return new HotkeyEngineResult(
                    _pushToTalk.Process(virtualKeyCode, isKeyDown),
                    _dictation.Process(virtualKeyCode, isKeyDown),
                    TextInputRequested: false,
                    SwallowKey: true);
            }
        }

        return new HotkeyEngineResult(
            _pushToTalk.Process(virtualKeyCode, isKeyDown),
            _dictation.Process(virtualKeyCode, isKeyDown),
            TextInputRequested: false,
            SwallowKey: false);
    }

    public void Reset()
    {
        _pushToTalk.Reset();
        _dictation.Reset();
        _swallowNextSpaceUp = false;
    }
}
