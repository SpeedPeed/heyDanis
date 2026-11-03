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
