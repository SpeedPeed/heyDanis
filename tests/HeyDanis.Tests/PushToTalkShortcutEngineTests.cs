using HeyDanis.Hotkey;
using Xunit;

namespace HeyDanis.Tests;

public class PushToTalkShortcutEngineTests
{
    [Fact]
    public void ControlAlt_firesPressedOnlyWhenBothModifiersAreDown()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);

        Assert.Equal(ShortcutTransition.None,
            engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true));
        Assert.False(engine.IsPressed);

        Assert.Equal(ShortcutTransition.Pressed,
            engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true));
        Assert.True(engine.IsPressed);
    }

    [Fact]
    public void ControlAlt_firesReleasedWhenEitherModifierGoesUp()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);
        engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true);
        engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true);
        Assert.True(engine.IsPressed);

        Assert.Equal(ShortcutTransition.Released,
            engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: false));
        Assert.False(engine.IsPressed);
    }

    [Fact]
    public void HoldingChord_doesNotRefirePressedOnSubsequentKeyEvents()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);
        engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true);
        engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true);

        // Pressing control again (e.g. auto-repeat events for the modifier) must not re-fire.
        Assert.Equal(ShortcutTransition.None,
            engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true));
        Assert.True(engine.IsPressed);
    }

    [Fact]
    public void OtherKeyWhileChordHeld_cancelsAndSuppressesUntilRelease()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);
        engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true);
        engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true);
        Assert.True(engine.IsPressed);

        // Strg+Alt+L (IDE shortcut) — the user did not mean push-to-talk.
        Assert.Equal(ShortcutTransition.Cancelled, engine.Process(0x4C, isKeyDown: true));
        Assert.False(engine.IsPressed);
        Assert.Equal(ShortcutTransition.None, engine.Process(0x4C, isKeyDown: false));

        // Releasing the modifiers after a cancel produces no Released.
        Assert.Equal(ShortcutTransition.None, engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: false));
        Assert.Equal(ShortcutTransition.None, engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: false));

        // A fresh press works again.
        engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true);
        Assert.Equal(ShortcutTransition.Pressed, engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true));
    }

    [Fact]
    public void KeysPressedBeforeTheChord_areIgnored()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);
        Assert.Equal(ShortcutTransition.None, engine.Process(0x41, isKeyDown: true));
        Assert.Equal(ShortcutTransition.None, engine.Process(0x41, isKeyDown: false));
        engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true);
        Assert.Equal(ShortcutTransition.Pressed, engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true));
    }

    [Fact]
    public void AltGr_neverTriggersPushToTalk()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);

        // Windows sends AltGr as a fake left Ctrl followed by right Alt, then the
        // character key (here Q for @ on a German keyboard).
        Assert.Equal(ShortcutTransition.None, engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true));
        Assert.Equal(ShortcutTransition.None, engine.Process(PushToTalkShortcutEngine.VK_RMENU, isKeyDown: true));
        Assert.Equal(ShortcutTransition.None, engine.Process(0x51, isKeyDown: true));
        Assert.Equal(ShortcutTransition.None, engine.Process(0x51, isKeyDown: false));
        Assert.Equal(ShortcutTransition.None, engine.Process(PushToTalkShortcutEngine.VK_RMENU, isKeyDown: false));
        Assert.Equal(ShortcutTransition.None, engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: false));
        Assert.False(engine.IsPressed);
    }

    [Fact]
    public void RightControlWithLeftAlt_works()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);

        Assert.Equal(ShortcutTransition.None,
            engine.Process(PushToTalkShortcutEngine.VK_RCONTROL, isKeyDown: true));
        Assert.Equal(ShortcutTransition.Pressed,
            engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true));
        Assert.Equal(ShortcutTransition.Released,
            engine.Process(PushToTalkShortcutEngine.VK_RCONTROL, isKeyDown: false));
    }

    [Fact]
    public void AltShift_chordConfiguration_tracksAltAndShift()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.AltShift);

        // Ctrl alone is not part of the chord — no transition.
        Assert.Equal(ShortcutTransition.None,
            engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true));

        Assert.Equal(ShortcutTransition.None,
            engine.Process(PushToTalkShortcutEngine.VK_LSHIFT, isKeyDown: true));
        Assert.Equal(ShortcutTransition.Pressed,
            engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true));
        Assert.Equal(ShortcutTransition.Released,
            engine.Process(PushToTalkShortcutEngine.VK_LSHIFT, isKeyDown: false));
    }

    [Fact]
    public void Reset_clearsPendingChordState()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);
        engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true);
        engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true);
        Assert.True(engine.IsPressed);

        engine.Reset();
        Assert.False(engine.IsPressed);

        // After reset, the chord must be re-entered from scratch.
        Assert.Equal(ShortcutTransition.None,
            engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true));
        Assert.Equal(ShortcutTransition.Pressed,
            engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true));
    }
}

public class HeyDanisHotkeyEngineTests
{
    private const int VK_LCONTROL = PushToTalkShortcutEngine.VK_LCONTROL;
    private const int VK_LMENU = PushToTalkShortcutEngine.VK_LMENU;
    private const int VK_LSHIFT = PushToTalkShortcutEngine.VK_LSHIFT;
    private const int VK_SPACE = HeyDanisHotkeyEngine.VK_SPACE;

    [Fact]
    public void ControlAltSpace_RequestsTextInput_CancelsPushToTalk_AndSwallowsSpace()
    {
        var engine = new HeyDanisHotkeyEngine();
        engine.Process(VK_LCONTROL, true);
        Assert.Equal(ShortcutTransition.Pressed, engine.Process(VK_LMENU, true).PushToTalk);

        var spaceDown = engine.Process(VK_SPACE, true);
        Assert.True(spaceDown.TextInputRequested);
        Assert.True(spaceDown.SwallowKey);
        Assert.Equal(ShortcutTransition.Cancelled, spaceDown.PushToTalk);

        var spaceUp = engine.Process(VK_SPACE, false);
        Assert.True(spaceUp.SwallowKey);
        Assert.False(spaceUp.TextInputRequested);

        // Letting go afterwards neither releases push-to-talk nor starts anything.
        Assert.Equal(ShortcutTransition.None, engine.Process(VK_LMENU, false).PushToTalk);
        Assert.Equal(ShortcutTransition.None, engine.Process(VK_LCONTROL, false).PushToTalk);
    }

    [Fact]
    public void ControlShift_StartsAndStopsDictation_NotPushToTalk()
    {
        var engine = new HeyDanisHotkeyEngine();
        Assert.Equal(ShortcutTransition.None, engine.Process(VK_LCONTROL, true).Dictation);
        var shiftDown = engine.Process(VK_LSHIFT, true);
        Assert.Equal(ShortcutTransition.Pressed, shiftDown.Dictation);
        Assert.Equal(ShortcutTransition.None, shiftDown.PushToTalk);

        Assert.Equal(ShortcutTransition.Released, engine.Process(VK_LSHIFT, false).Dictation);
    }

    [Fact]
    public void ControlShiftLetter_CancelsDictation()
    {
        var engine = new HeyDanisHotkeyEngine();
        engine.Process(VK_LCONTROL, true);
        engine.Process(VK_LSHIFT, true);

        // Strg+Shift+T (reopen tab) is a normal shortcut.
        Assert.Equal(ShortcutTransition.Cancelled, engine.Process(0x54, true).Dictation);
        Assert.Equal(ShortcutTransition.None, engine.Process(VK_LSHIFT, false).Dictation);
    }

    [Fact]
    public void AddingAltToDictation_CancelsInsteadOfReleasing_AndDoesNotStartPushToTalk()
    {
        var engine = new HeyDanisHotkeyEngine();
        engine.Process(VK_LCONTROL, true);
        engine.Process(VK_LSHIFT, true);

        var altDown = engine.Process(VK_LMENU, true);
        Assert.Equal(ShortcutTransition.Cancelled, altDown.Dictation);
        Assert.Equal(ShortcutTransition.None, altDown.PushToTalk);
    }

    [Fact]
    public void SpaceWithoutChord_IsNotSwallowed()
    {
        var engine = new HeyDanisHotkeyEngine();
        var result = engine.Process(VK_SPACE, true);
        Assert.False(result.SwallowKey);
        Assert.False(result.TextInputRequested);
    }
}
