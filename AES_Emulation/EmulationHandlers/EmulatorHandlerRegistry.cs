namespace AES_Emulation.EmulationHandlers;

public static class EmulatorHandlerRegistry
{
    private static readonly IEmulatorHandler[] Handlers =
    [
        DuckStationHandler.Instance
    ];

    public static IEmulatorHandler GetHandler(string? albumTitle)
    {
        foreach (var handler in Handlers)
        {
            if (handler.CanHandleAlbumTitle(albumTitle))
                return handler;
        }

        return DefaultEmulatorHandler.Instance;
    }
}
