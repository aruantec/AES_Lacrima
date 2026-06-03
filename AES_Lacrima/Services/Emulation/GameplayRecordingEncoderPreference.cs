namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Video encoder selection for gameplay recording (OBS-style hardware encoding when available).
/// </summary>
public enum GameplayRecordingEncoderPreference
{
    /// <summary>Prefer GPU encoder (NVENC, then AMF, then QSV), fall back to software x264.</summary>
    Auto,
    /// <summary>Force CPU libx264 / libsvtav1.</summary>
    Software,
    /// <summary>NVIDIA NVENC (h264_nvenc).</summary>
    Nvidia,
    /// <summary>AMD AMF (h264_amf).</summary>
    Amd,
    /// <summary>Intel Quick Sync (h264_qsv).</summary>
    Intel
}
