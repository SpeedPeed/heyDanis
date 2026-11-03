namespace Clicky.Companion;

/// <summary>
/// The lifecycle state of a push-to-talk turn. Mirrors the Mac
/// CompanionManager.voiceState enum used to drive the panel UI.
/// </summary>
public enum VoiceState
{
    Idle,
    Listening,
    Processing,
    Responding,
}
