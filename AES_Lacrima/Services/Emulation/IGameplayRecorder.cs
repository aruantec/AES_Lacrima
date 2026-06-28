using System;

namespace AES_Lacrima.Services.Emulation;

public interface IGameplayRecorder : IDisposable
{
    bool IsRecording { get; }

    string? ActiveOutputPath { get; }

    event Action<bool> RecordingStateChanged;

    event Action<string> RecordingFailed;

    void OnFrameFromCapture(byte[] pixels, int width, int height);

    bool TryStart(
        string outputDirectory,
        GameplayRecordingContainer container,
        GameplayRecordingVideoCodec codec,
        int fps,
        int videoBitrateKbps,
        int emulatorProcessId,
        int compositorLaunchPid = 0);

    void Stop();

    void WaitForPendingFinalize();
}
