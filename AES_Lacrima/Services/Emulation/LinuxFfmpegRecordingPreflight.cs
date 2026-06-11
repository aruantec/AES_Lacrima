using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.Emulation;

/// <summary>
/// Linux encoder preflight: software encoders first because FFmpeg may list AMF/NVENC
/// encoders that fail at runtime when vendor libraries are missing.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxFfmpegRecordingPreflight
{
    /// <summary>
    /// Pick an encoder immediately for recording. Avoids subprocess probe timeouts that stall capture
    /// (~8–11s of duplicate frames while the compositor worker is blocked).
    /// </summary>
    public static FfmpegRecordingPreflight.PreflightResult ResolveRecordingEncoder(
        string ffmpegPath,
        GameplayRecordingVideoCodec videoCodec,
        GameplayRecordingEncoderPreference encoderPreference,
        GameplayRecordingContainer preferredContainer)
    {
        var container = preferredContainer;

        if (videoCodec == GameplayRecordingVideoCodec.H264)
        {
            if (encoderPreference is GameplayRecordingEncoderPreference.Auto
                or GameplayRecordingEncoderPreference.Software)
            {
                return new FfmpegRecordingPreflight.PreflightResult(
                    "libx264",
                    "-preset veryfast -tune zerolatency",
                    container,
                    null);
            }

            var (codec, extra) = GameplayRecordingFormat.ResolveVideoEncoder(
                videoCodec, encoderPreference, ffmpegPath);
            return new FfmpegRecordingPreflight.PreflightResult(codec, extra, container, null);
        }

        if (encoderPreference is GameplayRecordingEncoderPreference.Auto
            or GameplayRecordingEncoderPreference.Software)
        {
            if (container == GameplayRecordingContainer.Mp4)
                container = GameplayRecordingContainer.Mkv;

            return new FfmpegRecordingPreflight.PreflightResult(
                "libsvtav1",
                "-preset 8",
                container,
                null);
        }

        var (av1Codec, av1Extra) = GameplayRecordingFormat.ResolveVideoEncoder(
            videoCodec, encoderPreference, ffmpegPath);
        if (container == GameplayRecordingContainer.Mp4 &&
            string.Equals(av1Codec, "libsvtav1", StringComparison.OrdinalIgnoreCase))
        {
            container = GameplayRecordingContainer.Mkv;
        }

        return new FfmpegRecordingPreflight.PreflightResult(av1Codec, av1Extra, container, null);
    }

    public static async Task<FfmpegRecordingPreflight.PreflightResult?> ProbeBestEncoderAsync(
        string ffmpegPath,
        GameplayRecordingVideoCodec videoCodec,
        GameplayRecordingEncoderPreference encoderPreference,
        GameplayRecordingContainer preferredContainer,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        CancellationToken cancellationToken = default)
    {
        var candidates = BuildCandidates(ffmpegPath, videoCodec, encoderPreference, preferredContainer);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = await TryEncodeProbeFrameAsync(
                ffmpegPath,
                candidate.CodecName,
                candidate.CodecExtra,
                candidate.Container,
                width,
                height,
                fps,
                bitrateKbps,
                cancellationToken).ConfigureAwait(false);

            if (error == null)
                return candidate;
        }

        return null;
    }

    private static IReadOnlyList<FfmpegRecordingPreflight.PreflightResult> BuildCandidates(
        string ffmpegPath,
        GameplayRecordingVideoCodec videoCodec,
        GameplayRecordingEncoderPreference encoderPreference,
        GameplayRecordingContainer preferredContainer)
    {
        var list = new List<FfmpegRecordingPreflight.PreflightResult>();
        var encoders = FfmpegHardwareEncoderProbe.ListEncoders(ffmpegPath);

        static void AddUnique(
            List<FfmpegRecordingPreflight.PreflightResult> target,
            string codec,
            string extra,
            GameplayRecordingContainer container)
        {
            if (target.Exists(c => string.Equals(c.CodecName, codec, StringComparison.OrdinalIgnoreCase)
                                    && c.Container == container))
            {
                return;
            }

            target.Add(new FfmpegRecordingPreflight.PreflightResult(codec, extra, container, null));
        }

        if (videoCodec == GameplayRecordingVideoCodec.H264)
        {
            AddUnique(list, "libx264", "-preset veryfast -tune zerolatency", preferredContainer);

            if (encoderPreference is GameplayRecordingEncoderPreference.Auto or GameplayRecordingEncoderPreference.Intel
                && encoders.Contains("h264_vaapi", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(list, "h264_vaapi", "-qp 24", preferredContainer);
            }

            if (encoderPreference is GameplayRecordingEncoderPreference.Auto or GameplayRecordingEncoderPreference.Amd
                && encoders.Contains("h264_amf", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var amfExtra in FfmpegHardwareEncoderProbe.GetAmfH264ExtraArgCandidates())
                    AddUnique(list, "h264_amf", amfExtra, preferredContainer);
            }

            if (encoderPreference is GameplayRecordingEncoderPreference.Auto or GameplayRecordingEncoderPreference.Nvidia
                && encoders.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(list, "h264_nvenc", "-preset p4 -tune ll -rc vbr -zerolatency 1", preferredContainer);
            }

            if (encoderPreference == GameplayRecordingEncoderPreference.Software)
                return list;

            var (codec, extra) = GameplayRecordingFormat.ResolveVideoEncoder(videoCodec, encoderPreference, ffmpegPath);
            AddUnique(list, codec, extra, preferredContainer);
        }
        else
        {
            AddUnique(list, "libsvtav1", "-preset 8", preferredContainer);

            if (encoderPreference is GameplayRecordingEncoderPreference.Auto or GameplayRecordingEncoderPreference.Amd
                && encoders.Contains("av1_amf", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(list, "av1_amf", "-usage ultralowlatency -quality speed", preferredContainer);
            }

            if (encoderPreference is GameplayRecordingEncoderPreference.Auto or GameplayRecordingEncoderPreference.Nvidia
                && encoders.Contains("av1_nvenc", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(list, "av1_nvenc", "-preset p4 -tune ll -rc vbr", preferredContainer);
            }

            if (encoderPreference == GameplayRecordingEncoderPreference.Software)
                return list;

            var (codec, extra) = GameplayRecordingFormat.ResolveVideoEncoder(videoCodec, encoderPreference, ffmpegPath);
            AddUnique(list, codec, extra, preferredContainer);
        }

        if (preferredContainer == GameplayRecordingContainer.Mp4)
        {
            foreach (var candidate in list.ToArray())
            {
                if (candidate.Container == GameplayRecordingContainer.Mp4)
                    AddUnique(list, candidate.CodecName, candidate.CodecExtra, GameplayRecordingContainer.Mkv);
            }
        }

        return list;
    }

    private static async Task<string?> TryEncodeProbeFrameAsync(
        string ffmpegPath,
        string codecName,
        string codecExtra,
        GameplayRecordingContainer container,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        CancellationToken cancellationToken)
    {
        var ext = GameplayRecordingFormat.GetFileExtension(container);
        var outputPath = Path.Combine(Path.GetTempPath(), $"aes_linux_rec_probe_{Guid.NewGuid():N}{ext}");
        try
        {
            var args = LinuxGameplayRecorderService.BuildFfmpegArguments(
                outputPath, width, height, fps, container, codecName, codecExtra, bitrateKbps, null);

            var stderr = new StringBuilder();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    stderr.AppendLine(e.Data);
            };

            if (!process.Start())
                return "Failed to start FFmpeg.";

            process.BeginErrorReadLine();

            var frameSize = width * height * 4;
            var frame = new byte[frameSize];
            await process.StandardInput.BaseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            using var reg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            });

            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return "FFmpeg probe timed out.";
            }

            if (process.ExitCode != 0)
                return ExtractError(stderr.ToString()) ?? $"FFmpeg probe failed (code {process.ExitCode}).";

            var size = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0;
            return size > 256 ? null : ExtractError(stderr.ToString()) ?? "Encoder produced an empty file.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
            catch
            {
            }
        }
    }

    private static string? ExtractError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return null;

        foreach (var line in stderr.Split('\n', '\r'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                || line.Contains("not supported", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }

        return null;
    }
}
