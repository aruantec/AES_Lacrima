using AES_Mpv.Interop;

namespace AES_Mpv.Player;

public sealed class AesMpvPlayerOptions
{
    public AesMpvPlayerOptions()
    {
    }

    public AesMpvPlayerOptions(string sharedClientName, AesMpvPlayer sharedPlayer, bool weakReference = false)
    {
        SharedClientName = sharedClientName;
        SharedPlayer = sharedPlayer;
        UseWeakReference = weakReference;
    }

    public MpvOpenGlAddressResolver? ResolveOpenGlAddress { get; set; }
    public MpvRenderUpdateCallback? OnRenderInvalidated { get; set; }
    public Action<AesMpvPlayer>? BeforeInitialize { get; set; }
    public bool UseWeakReference { get; }
    public AesMpvPlayer? SharedPlayer { get; }
    public string? SharedClientName { get; }
}
