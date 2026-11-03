namespace Clicky.Overlay;

/// <summary>
/// Debug behavior for pointer rendering and instrumentation.
/// </summary>
public sealed record PointerDebugOptions
{
    public bool ShowTargetCrosshair { get; init; }
    public bool PinpointMode { get; init; }

    public static PointerDebugOptions Default { get; } = new()
    {
        ShowTargetCrosshair = false,
        PinpointMode = true,
    };

    public static PointerDebugOptions FromEnvironment()
    {
        var forceDebug = IsEnabled("CLICKY_POINTER_DEBUG");
        var disableCrosshair = IsDisabled("CLICKY_POINTER_CROSSHAIR");
        var disablePinpoint = IsDisabled("CLICKY_POINTER_PINPOINT");

        return new PointerDebugOptions
        {
            ShowTargetCrosshair = forceDebug && !disableCrosshair,
            PinpointMode = forceDebug || !disablePinpoint,
        };
    }

    private static bool IsEnabled(string variable)
    {
        var value = System.Environment.GetEnvironmentVariable(variable);
        return value is not null &&
            (value.Equals("1", System.StringComparison.OrdinalIgnoreCase) ||
             value.Equals("true", System.StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", System.StringComparison.OrdinalIgnoreCase) ||
             value.Equals("on", System.StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDisabled(string variable)
    {
        var value = System.Environment.GetEnvironmentVariable(variable);
        return value is not null &&
            (value.Equals("0", System.StringComparison.OrdinalIgnoreCase) ||
             value.Equals("false", System.StringComparison.OrdinalIgnoreCase) ||
             value.Equals("no", System.StringComparison.OrdinalIgnoreCase) ||
             value.Equals("off", System.StringComparison.OrdinalIgnoreCase));
    }
}
