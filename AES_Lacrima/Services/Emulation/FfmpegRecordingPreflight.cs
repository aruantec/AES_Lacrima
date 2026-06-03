using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AES_Lacrima.Services.Emulation;

internal static class FfmpegRecordingPreflight
{
    public sealed record PreflightResult(string CodecName, string CodecExtra, GameplayRecordingContainer Container, string? Error);

    public static async Task<PreflightResult?> ProbeBestEncoderAsync(
        string ffmpegPath,
        GameplayRecordingVideoCodec videoCodec,
        GameplayRecordingEncoderPreference encoderPreference,
        GameplayRecordingContainer preferredContainer,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        bool withAudioPipe,
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
                withAudioPipe,
                cancellationToken).ConfigureAwait(false);

            if (error == null)
                return candidate;
        }

        return null;
    }

    private static IReadOnlyList<PreflightResult> BuildCandidates(
        string ffmpegPath,
        GameplayRecordingVideoCodec videoCodec,
        GameplayRecordingEncoderPreference encoderPreference,
        GameplayRecordingContainer preferredContainer)
    {
        var list = new List<PreflightResult>();
        var encoders = FfmpegHardwareEncoderProbe.ListEncoders(ffmpegPath);

        static void AddAmfH264(List<PreflightResult> target, string extra, GameplayRecordingContainer container)
        {
            target.Add(new PreflightResult("h264_amf", extra, container, null));
        }

        if (videoCodec == GameplayRecordingVideoCodec.H264
            && encoders.Contains("h264_amf", StringComparison.OrdinalIgnoreCase)
            && encoderPreference is GameplayRecordingEncoderPreference.Amd or GameplayRecordingEncoderPreference.Auto)
        {
            foreach (var amfExtra in FfmpegHardwareEncoderProbe.GetAmfH264ExtraArgCandidates())
            {
                AddAmfH264(list, amfExtra, preferredContainer);
                if (preferredContainer == GameplayRecordingContainer.Mp4)
                    AddAmfH264(list, amfExtra, GameplayRecordingContainer.Mkv);
            }
        }

        var (codec, extra) = GameplayRecordingFormat.ResolveVideoEncoder(videoCodec, encoderPreference, ffmpegPath);
        if (!list.Exists(c => string.Equals(c.CodecName, codec, StringComparison.OrdinalIgnoreCase)
                               && c.Container == preferredContainer))
            list.Add(new PreflightResult(codec, extra, preferredContainer, null));

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
        bool withAudioPipe,
        CancellationToken cancellationToken)
    {
        var ext = GameplayRecordingFormat.GetFileExtension(container);
        var outputPath = Path.Combine(Path.GetTempPath(), $"aes_rec_probe_{Guid.NewGuid():N}{ext}");
        try
        {
            var args = GameplayRecorderService.BuildFfmpegArguments(
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
                return line.Trim();
        }

        return null;
    }
}
