namespace AES_Emulation.Services;

public sealed class ArcadePillarboxApplyLockMessage
{
    public string? RomPath;
    public int Left;
    public int Right;
    public int FrameWidth;
}

public sealed class ArcadePillarboxUnlockMessage
{
    public string? RomPath;
}
