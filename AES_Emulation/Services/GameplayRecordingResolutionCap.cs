namespace AES_Emulation.Services;

/// <summary>
/// Maximum output resolution for gameplay recording (OBS-style output size cap).
/// </summary>
public enum GameplayRecordingResolutionCap
{
    /// <summary>No cap — use the capture viewport size.</summary>
    Native = 0,
    P720 = 720,
    P1080 = 1080,
    P1440 = 1440,
    P2160 = 2160
}
