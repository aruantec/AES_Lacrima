namespace AES_Emulation.Windows.API;

public sealed record GameplayRecordingAudioDeviceItem(string Id, string DisplayName, bool IsDefault)
{
    public override string ToString() => DisplayName;
}
