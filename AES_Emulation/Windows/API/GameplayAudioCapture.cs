using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AES_Emulation.Services;

namespace AES_Emulation.Windows.API;

/// <summary>
/// Captures PCM audio for gameplay recording via AudioBridge (process or device loopback).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameplayAudioCapture : IDisposable
{
    private const string AudioBridgeDll = "AudioBridge.dll";
    private static readonly bool NativeAvailable;

    static GameplayAudioCapture()
    {
        try
        {
            var lib = NativeLibrary.Load(AudioBridgeDll);
            NativeAvailable = lib != IntPtr.Zero;
            NativeLibrary.Free(lib);
        }
        catch
        {
            NativeAvailable = false;
        }
    }

    [DllImport(AudioBridgeDll, PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int AudioBridge_DeviceCaptureStart(string? deviceId);

    [DllImport(AudioBridgeDll, PreserveSig = true)]
    private static extern int AudioBridge_ProcessCaptureStart(uint pid);

    [DllImport(AudioBridgeDll, PreserveSig = true)]
    private static extern void AudioBridge_ProcessCaptureStop();

    [DllImport(AudioBridgeDll, PreserveSig = true)]
    private static extern int AudioBridge_ProcessCaptureIsRunning();

    [DllImport(AudioBridgeDll, PreserveSig = true)]
    private static extern int AudioBridge_ProcessCaptureGetFormat(out uint sampleRate, out ushort channels, out ushort bitsPerSample);

    [DllImport(AudioBridgeDll, PreserveSig = true)]
    private static extern int AudioBridge_ProcessCaptureRead(byte[] buffer, uint bufferSize, out uint bytesRead);

    private bool _isCapturing;

    public static bool IsSupported => NativeAvailable && OperatingSystem.IsWindows();

    public int SampleRate { get; private set; } = 48_000;
    public int Channels { get; private set; } = 2;
    public int BitsPerSample { get; private set; } = 16;
    public int BytesPerSample => Channels * BitsPerSample / 8;

    public bool TryStart(GameplayRecordingAudioSource source, int processId, string? deviceId)
    {
        if (!IsSupported || source == GameplayRecordingAudioSource.None)
            return false;

        Stop();

        int hr = source switch
        {
            GameplayRecordingAudioSource.OutputDevice =>
                AudioBridge_DeviceCaptureStart(string.IsNullOrWhiteSpace(deviceId) ? null : deviceId),
            GameplayRecordingAudioSource.Application or GameplayRecordingAudioSource.EmulatorProcess =>
                processId > 0 ? AudioBridge_ProcessCaptureStart((uint)processId) : -1,
            _ => -1
        };

        if (hr < 0)
            return false;

        if (AudioBridge_ProcessCaptureGetFormat(out var rate, out var ch, out var bits) >= 0)
        {
            SampleRate = (int)rate;
            Channels = ch;
            BitsPerSample = bits;
        }

        _isCapturing = AudioBridge_ProcessCaptureIsRunning() != 0;
        return _isCapturing;
    }

    public int Read(byte[] buffer)
    {
        if (!_isCapturing || buffer.Length == 0)
            return 0;

        var hr = AudioBridge_ProcessCaptureRead(buffer, (uint)buffer.Length, out var read);
        if (hr < 0)
            return 0;

        return (int)read;
    }

    public void Stop()
    {
        if (!_isCapturing)
            return;

        AudioBridge_ProcessCaptureStop();
        _isCapturing = false;
    }

    public void Dispose() => Stop();
}
