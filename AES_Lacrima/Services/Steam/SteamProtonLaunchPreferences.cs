using System.Collections.Generic;

namespace AES_Lacrima.Services.Steam;

public sealed class SteamProtonLaunchPreferences
{
    public static SteamProtonLaunchPreferences Empty { get; } = new();

    public SteamProtonLaunchPreferences()
    {
    }

    public SteamProtonLaunchPreferences(string? defaultProtonDirectory, IReadOnlyDictionary<string, string>? gameOverrides)
    {
        DefaultProtonDirectory = defaultProtonDirectory;
        GameOverrides = gameOverrides ?? EmptyGameOverrides;
    }

    public string? DefaultProtonDirectory { get; init; }

    public IReadOnlyDictionary<string, string> GameOverrides { get; init; } = EmptyGameOverrides;

    private static IReadOnlyDictionary<string, string> EmptyGameOverrides { get; } =
        new Dictionary<string, string>();
}
