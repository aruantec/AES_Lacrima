namespace AES_Emulation.EmulationHandlers;

public sealed class DefaultEmulatorHandler : EmulatorHandlerBase
{
    public static DefaultEmulatorHandler Instance { get; } = new();

    private DefaultEmulatorHandler()
    {
    }

    public override string HandlerId => "default";

    public override bool CanHandleAlbumTitle(string? albumTitle) => false;
}
