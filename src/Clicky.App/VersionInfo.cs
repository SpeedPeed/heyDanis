using System.Reflection;

namespace Clicky.App;

/// <summary>
/// Provides the current app version from the assembly metadata.
/// </summary>
internal static class VersionInfo
{
    public static string Current { get; } = GetVersion();

    private static string GetVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";

        // Strip +commithash suffix if present
        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
            version = version[..plusIndex];

        return version;
    }
}
