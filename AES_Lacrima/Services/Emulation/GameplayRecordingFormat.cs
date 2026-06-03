using System;

namespace AES_Lacrima.Services.Emulation;

public enum GameplayRecordingContainer
{
    Mp4,
    Mkv
}

public enum GameplayRecordingVideoCodec
{
    H264,
    Av1
}

public static class GameplayRecordingFormat
{
    public static string GetFileExtension(GameplayRecordingContainer container) =>
        container == GameplayRecordingContainer.Mkv ? ".mkv" : ".mp4";

    public static string GetContainerName(GameplayRecordingContainer container) =>
        container == GameplayRecordingContainer.Mkv ? "matroska" : "mp4";

    public static (string CodecName, string ExtraArgs) ResolveVideoEncoder(
        GameplayRecordingVideoCodec codec,
        GameplayRecordingEncoderPreference encoderPreference,
        string ffmpegPath) =>
        codec == GameplayRecordingVideoCodec.Av1
            ? FfmpegHardwareEncoderProbe.ResolveAv1Encoder(ffmpegPath, encoderPreference)
            : FfmpegHardwareEncoderProbe.ResolveH264Encoder(ffmpegPath, encoderPreference);

    public static int ApplyRecordingScale(int value, int scalePercent) =>
        Math.Max(2, (value * Math.Clamp(scalePercent, 25, 100) / 100) & ~1);
}
