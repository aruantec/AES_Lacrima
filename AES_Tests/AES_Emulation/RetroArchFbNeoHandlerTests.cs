using AES_Emulation.EmulationHandlers;

namespace AES_Emulation.Tests;

public sealed class RetroArchFbNeoHandlerTests
{
    [Fact]
    public void EmulatorHandlerRegistry_FinalBurnNeo_IncludesFbNeoAndRetroArchFbNeoHandlers()
    {
        var handlers = EmulatorHandlerRegistry.GetHandlersForSection("Final Burn Neo");

        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "fbneo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "retroarch-fbn", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handlers, handler => string.Equals(handler.HandlerId, "retroarch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmulatorHandlerRegistry_Arcade_IncludesRetroArchHandlerOnly()
    {
        var handlers = EmulatorHandlerRegistry.GetHandlersForSection("Arcade");

        Assert.Contains(handlers, handler => string.Equals(handler.HandlerId, "retroarch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handlers, handler => string.Equals(handler.HandlerId, "retroarch-fbn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RetroArchHandler_DoesNotHandleFinalBurnNeoAlbumTitle()
    {
        Assert.False(RetroArchHandler.Instance.CanHandleAlbumTitle("Final Burn Neo"));
        Assert.False(RetroArchHandler.Instance.CanHandleAlbumTitle("FBN"));
        Assert.True(RetroArchHandler.Instance.CanHandleAlbumTitle("Arcade"));
    }

    [Fact]
    public void RetroArchHandler_FilterArcadeRetroArchCores_ReturnsOnlyArcadeCores()
    {
        var cores = new[]
        {
            "mame2010_libretro.dll",
            "snes9x_libretro.dll",
            "fbneo_libretro.dll",
            "dolphin_libretro.dll"
        };

        var filtered = RetroArchHandler.FilterArcadeRetroArchCores(cores);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, core => string.Equals(core, "mame2010_libretro.dll", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(filtered, core => string.Equals(core, "fbneo_libretro.dll", StringComparison.OrdinalIgnoreCase));
    }
}
