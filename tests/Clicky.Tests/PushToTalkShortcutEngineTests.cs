using Clicky.Hotkey;
using Xunit;

namespace Clicky.Tests;

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
    public void UnrelatedKeys_areIgnored()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);
        engine.Process(PushToTalkShortcutEngine.VK_LCONTROL, isKeyDown: true);
        engine.Process(PushToTalkShortcutEngine.VK_LMENU, isKeyDown: true);
        Assert.True(engine.IsPressed);

        // 'A' key down/up while chord held — must not affect state.
        Assert.Equal(ShortcutTransition.None, engine.Process(0x41, isKeyDown: true));
        Assert.Equal(ShortcutTransition.None, engine.Process(0x41, isKeyDown: false));
        Assert.True(engine.IsPressed);
    }

    [Fact]
    public void RightSideModifiers_areEquivalentToLeftSide()
    {
        var engine = new PushToTalkShortcutEngine(PushToTalkShortcut.ControlAlt);

        Assert.Equal(ShortcutTransition.None,
            engine.Process(PushToTalkShortcutEngine.VK_RCONTROL, isKeyDown: true));
        Assert.Equal(ShortcutTransition.Pressed,
            engine.Process(PushToTalkShortcutEngine.VK_RMENU, isKeyDown: true));
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
