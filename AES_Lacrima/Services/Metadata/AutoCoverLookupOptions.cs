namespace AES_Lacrima.Services;

/// <summary>
/// Tunable limits for background auto-cover lookups.
/// </summary>
public sealed class AutoCoverLookupOptions
{
    public static AutoCoverLookupOptions Default { get; } = new();

    /// <summary>
    /// Fast lookup used by emulation cover scans: short budgets and no permanent skip on timeout.
    /// </summary>
    public static AutoCoverLookupOptions FastSkip { get; } = new()
    {
        SearchTimeoutSeconds = 5,
        DownloadTimeoutSeconds = 2.5,
        TotalBudgetSeconds = 9,
        MaxCandidatesPerQuery = 6,
        PreferSequentialDownloads = true,
        MarkExhaustedOnFailure = true,
        MarkExhaustedOnTimeout = false
    };

    /// <summary>
    /// Background album carousel scan: enough time per title without blocking the UI forever.
    /// </summary>
    public static AutoCoverLookupOptions EmulationAlbumScan { get; } = new()
    {
        SearchTimeoutSeconds = 10,
        DownloadTimeoutSeconds = 5,
        TotalBudgetSeconds = 18,
        MaxCandidatesPerQuery = 8,
        PreferSequentialDownloads = true,
        MarkExhaustedOnFailure = true,
        MarkExhaustedOnTimeout = false
    };

    public int SearchTimeoutSeconds { get; init; } = 10;
    public double DownloadTimeoutSeconds { get; init; } = 4;
    public int TotalBudgetSeconds { get; init; }
    public int MaxCandidatesPerQuery { get; init; } = 6;
    public bool PreferSequentialDownloads { get; init; }
    public bool MarkExhaustedOnFailure { get; init; } = true;
    public bool MarkExhaustedOnTimeout { get; init; } = true;
}
