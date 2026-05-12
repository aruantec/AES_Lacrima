using System;
using System.Collections.Generic;
using System.Linq;

namespace AES_Emulation.EmulationHandlers;

public static class EmulatorHandlerRegistry
{
    private static readonly IEmulatorHandler[] Handlers =
    [
        DuckStationHandler.Instance,
        Pcsx2Handler.Instance,
        Rpcs3Handler.Instance,
        ShadPs4Handler.Instance,
        DolphinHandler.Instance,
        FlyCastHandler.Instance,
        RedreamHandler.Instance,
        FbNeoHandler.Instance,
        RetroArchHandler.Instance,
        RetroArchGbaHandler.Instance,
        RetroArchGenesisHandler.Instance,
        RetroArchSaturnHandler.Instance,
        AresHandler.Instance,
        XeniaHandler.Instance,
        EdenHandler.Instance,
        CemuHandler.Instance,
        Snes9xHandler.Instance
    ];

    public static IReadOnlyList<IEmulatorHandler> GetRegisteredHandlers()
        => Handlers;

    public static IReadOnlyList<IEmulatorHandler> GetHandlersForSection(string? albumTitle)
        => [.. Handlers.Where(handler =>
            string.Equals(handler.SectionTitle, albumTitle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(handler.SectionKey, albumTitle, StringComparison.OrdinalIgnoreCase) ||
            handler.CanHandleAlbumTitle(albumTitle))];

    public static IEmulatorHandler GetHandler(string? albumTitle)
    {
        foreach (var handler in Handlers)
        {
            if (string.Equals(handler.SectionTitle, albumTitle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(handler.SectionKey, albumTitle, StringComparison.OrdinalIgnoreCase) ||
                handler.CanHandleAlbumTitle(albumTitle))
            {
                return handler;
            }
        }

        return DefaultEmulatorHandler.Instance;
    }
}
