namespace AES_Emulation.Windows.API;

public sealed record GameplayRecordingAudioSessionItem(int ProcessId, string DisplayName)
{
    public override string ToString() => DisplayName;
}
