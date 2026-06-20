using AES_Emulation.EmulationHandlers;

namespace AES_Tests.AES_Emulation;

public sealed class EmulatorFlatpakCatalogTests
{
    [Fact]
    public void IsCompatibleApplicationId_AcceptsKnownHandlerFlatpak()
    {
        Assert.True(EmulatorFlatpakCatalog.IsCompatibleApplicationId(
            ShadPs4Handler.Instance.HandlerId,
            "net.shadps4.shadPS4"));
        Assert.True(EmulatorFlatpakCatalog.IsCompatibleApplicationId(
            DolphinHandler.Instance.HandlerId,
            "org.DolphinEmu.dolphin-emu"));
    }

    [Fact]
    public void IsCompatibleApplicationId_RejectsCrossHandlerFlatpak()
    {
        Assert.False(EmulatorFlatpakCatalog.IsCompatibleApplicationId(
            ShadPs4Handler.Instance.HandlerId,
            "org.DolphinEmu.dolphin-emu"));
        Assert.False(EmulatorFlatpakCatalog.IsCompatibleApplicationId(
            DolphinHandler.Instance.HandlerId,
            "net.shadps4.shadPS4"));
    }
}
