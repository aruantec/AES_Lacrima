using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AES_Lacrima.Services.Emulation;

internal static class FfmpegHardwareEncoderProbe
{
    private static string? _cachedEncodersList;
    private static readonly object CacheLock = new();

    public static (string Codec, string ExtraArgs) ResolveH264Encoder(string ffmpegPath, GameplayRecordingEncoderPreference preference)
    {
        if (preference == GameplayRecordingEncoderPreference.Software)
            return ("libx264", "-preset veryfast -tune zerolatency");

        var encoders = GetEncodersList(ffmpegPath);

        if (preference == GameplayRecordingEncoderPreference.Amd)
            return RequireOrFallback(encoders, "h264_amf", preference, AmfH264Args, "libx264", "-preset veryfast -tune zerolatency");

        if (preference == GameplayRecordingEncoderPreference.Nvidia)
            return RequireOrFallback(encoders, "h264_nvenc", preference, NvencH264Args, "libx264", "-preset veryfast -tune zerolatency");

        if (preference == GameplayRecordingEncoderPreference.Intel)
            return RequireOrFallback(encoders, "h264_qsv", preference, QsvH264Args, "libx264", "-preset veryfast -tune zerolatency");

        // Auto
        if (Has(encoders, "h264_nvenc"))
            return ("h264_nvenc", NvencH264Args);
        if (Has(encoders, "h264_amf"))
            return ("h264_amf", AmfH264Args);
        if (Has(encoders, "h264_qsv"))
            return ("h264_qsv", QsvH264Args);

        return ("libx264", "-preset veryfast -tune zerolatency");
    }

    public static (string Codec, string ExtraArgs) ResolveAv1Encoder(string ffmpegPath, GameplayRecordingEncoderPreference preference)
    {
        if (preference == GameplayRecordingEncoderPreference.Software)
            return ("libsvtav1", "-preset 8");

        var encoders = GetEncodersList(ffmpegPath);

        if (preference == GameplayRecordingEncoderPreference.Amd)
            return RequireOrFallback(encoders, "av1_amf", preference, AmfAv1Args, "libsvtav1", "-preset 8");

        if (preference == GameplayRecordingEncoderPreference.Nvidia)
            return RequireOrFallback(encoders, "av1_nvenc", preference, NvencAv1Args, "libsvtav1", "-preset 8");

        if (preference == GameplayRecordingEncoderPreference.Intel)
            return RequireOrFallback(encoders, "av1_qsv", preference, QsvAv1Args, "libsvtav1", "-preset 8");

        // Auto — prefer GPU AV1 when available
        if (Has(encoders, "av1_amf"))
            return ("av1_amf", AmfAv1Args);
        if (Has(encoders, "av1_nvenc"))
            return ("av1_nvenc", NvencAv1Args);
        if (Has(encoders, "av1_qsv"))
            return ("av1_qsv", QsvAv1Args);

        return ("libsvtav1", "-preset 8");
    }

    public static bool IsAmdAmfEncoder(string codecName) =>
        codecName.Contains("_amf", StringComparison.OrdinalIgnoreCase);

    public static bool IsHardwareEncoder(string codecName) =>
        IsAmdAmfEncoder(codecName)
        || codecName.Contains("_nvenc", StringComparison.OrdinalIgnoreCase)
        || codecName.Contains("_qsv", StringComparison.OrdinalIgnoreCase)
        || codecName.Contains("_vaapi", StringComparison.OrdinalIgnoreCase);

    /// <summary>AMF accepts BGRA directly — no CPU colorspace filter (faster, fixes many h264_amf failures).</summary>
    public static string GetInputVideoFilter(string codecName) =>
        IsAmdAmfEncoder(codecName) ? string.Empty : IsHardwareEncoder(codecName) ? "-vf format=nv12" : "-vf format=yuv420p";

    public static bool UseCfrFpsMode(string codecName) => !IsAmdAmfEncoder(codecName);

    /// <summary>
    /// libsvtav1 only accepts a VBR target bitrate; -maxrate/-bufsize trigger CBR and fail to open.
    /// </summary>
    public static string GetVideoBitrateArguments(string codecName, int bitrateKbps) =>
        string.Equals(codecName, "libsvtav1", StringComparison.OrdinalIgnoreCase)
            ? $"-b:v {bitrateKbps}k"
            : $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k";

    private const string AmfH264Args = AmfH264Candidate1;
    private const string AmfH264Candidate1 = "-usage lowlatency -profile:v main -quality speed -rc vbr_peak";
    private const string AmfH264Candidate2 = "-usage transcoding -profile:v main -quality balanced";
    private const string AmfH264Candidate3 = "-usage lowlatency_high_quality -profile:v main -quality speed";
    private const string AmfAv1Args = "-usage ultralowlatency -quality speed";

    public static IReadOnlyList<string> GetAmfH264ExtraArgCandidates() =>
        [AmfH264Candidate1, AmfH264Candidate2, AmfH264Candidate3];

    public static string ListEncoders(string ffmpegPath) => GetEncodersList(ffmpegPath);
    private const string NvencH264Args = "-preset p4 -tune ll -rc vbr -zerolatency 1";
    private const string NvencAv1Args = "-preset p4 -tune ll -rc vbr";
    private const string QsvH264Args = "-preset veryfast -look_ahead 0";
    private const string QsvAv1Args = "-preset veryfast";

    private static (string Codec, string ExtraArgs) RequireOrFallback(
        string encoders,
        string requiredCodec,
        GameplayRecordingEncoderPreference preference,
        string hwArgs,
        string fallbackCodec,
        string fallbackArgs)
    {
        if (Has(encoders, requiredCodec))
            return (requiredCodec, hwArgs);

        // Explicit vendor choice but encoder missing from this FFmpeg build.
        if (preference != GameplayRecordingEncoderPreference.Auto)
            return (fallbackCodec, fallbackArgs);

        return (fallbackCodec, fallbackArgs);
    }

    /// <summary>True when the user picked a GPU vendor but the matching encoder is not in the FFmpeg build.</summary>
    public static bool IsVendorEncoderMissing(
        string ffmpegPath,
        GameplayRecordingVideoCodec codec,
        GameplayRecordingEncoderPreference preference,
        out string expectedEncoder)
    {
        expectedEncoder = string.Empty;
        if (preference is GameplayRecordingEncoderPreference.Auto or GameplayRecordingEncoderPreference.Software)
            return false;

        var encoders = GetEncodersList(ffmpegPath);
        expectedEncoder = (codec, preference) switch
        {
            (GameplayRecordingVideoCodec.H264, GameplayRecordingEncoderPreference.Amd) => "h264_amf",
            (GameplayRecordingVideoCodec.H264, GameplayRecordingEncoderPreference.Nvidia) => "h264_nvenc",
            (GameplayRecordingVideoCodec.H264, GameplayRecordingEncoderPreference.Intel) => "h264_qsv",
            (GameplayRecordingVideoCodec.Av1, GameplayRecordingEncoderPreference.Amd) => "av1_amf",
            (GameplayRecordingVideoCodec.Av1, GameplayRecordingEncoderPreference.Nvidia) => "av1_nvenc",
            (GameplayRecordingVideoCodec.Av1, GameplayRecordingEncoderPreference.Intel) => "av1_qsv",
            _ => string.Empty
        };

        return !string.IsNullOrEmpty(expectedEncoder) && !Has(encoders, expectedEncoder);
    }

    private static bool Has(string list, string name) =>
        list.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static string GetEncodersList(string ffmpegPath)
    {
        lock (CacheLock)
        {
            if (_cachedEncodersList != null)
                return _cachedEncodersList;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-hide_banner -encoders",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    _cachedEncodersList = string.Empty;
                    return _cachedEncodersList;
                }

                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                _cachedEncodersList = stdout + stderr;
            }
            catch
            {
                _cachedEncodersList = string.Empty;
            }

            return _cachedEncodersList;
        }
    }

    public static void InvalidateCache() => _cachedEncodersList = null;
}
