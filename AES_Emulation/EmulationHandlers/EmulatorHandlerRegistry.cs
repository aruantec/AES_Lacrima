using System;
using System.Collections.Generic;
using System.Linq;

namespace AES_Emulation.EmulationHandlers;

public static class EmulatorHandlerRegistry
{
    private static readonly IEmulatorHandler[] Handlers =
    [
        DuckStationHandler.Instance
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
