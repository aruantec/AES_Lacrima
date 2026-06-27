using AES_Emulation.Services;
using AES_Emulation.Windows;
using Xunit;

namespace AES_Tests.AES_Emulation;

public sealed class ArcadePillarboxCropResolverTests
{
    [Fact]
    public void Resolve_AppliesSafetyMarginAndThinPillarRetain()
    {
        var resolver = new ArcadePillarboxCropResolver();

        var (left, right) = resolver.Resolve(1920, detectedLeft: 240, detectedRight: 240);

        Assert.Equal(229, left);
        Assert.Equal(229, right);
    }

    [Fact]
    public void Reset_LoadsLockedCropFromMetadata()
    {
        var romPath = Path.Combine(Path.GetTempPath(), $"aes-lock-crop-{Guid.NewGuid():N}.zip");
        try
        {
            ArcadePillarboxCropMetadataHelper.SaveLockedCrop(romPath, left: 220, right: 220, frameWidth: 1920);

            var resolver = new ArcadePillarboxCropResolver();
            resolver.Reset(romPath);

            Assert.True(resolver.IsLocked);
            Assert.True(resolver.TryGetLockedCrop(1920, out var left, out var right));
            Assert.Equal(220, left);
            Assert.Equal(220, right);
        }
        finally
        {
            ArcadePillarboxCropMetadataHelper.ClearLockedCrop(romPath);
        }
    }

    [Fact]
    public void SetLockedCrop_IgnoresDetection()
    {
        var resolver = new ArcadePillarboxCropResolver();
        resolver.SetLockedCrop(left: 220, right: 220, frameWidth: 1920);

        var (left, right) = resolver.Resolve(1920, detectedLeft: 360, detectedRight: 360);

        Assert.Equal(220, left);
        Assert.Equal(220, right);
    }
}
