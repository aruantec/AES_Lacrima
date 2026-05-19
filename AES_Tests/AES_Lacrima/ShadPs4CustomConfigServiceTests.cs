using System.Runtime.InteropServices;
using AES_Lacrima.Services.Emulation;
using AES_Lacrima.Services.ShadPs4;

namespace AES_Tests.AES_Lacrima;

public sealed class ShadPs4CustomConfigServiceTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsPerGameConfig()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
        var emulatorDir = Path.Combine(tempRoot, "shadPS4");
        Directory.CreateDirectory(Path.Combine(emulatorDir, "user", "custom_configs"));

        var document = ShadPs4CustomConfigService.CreateDefault();
        document.General.DevKitMode = true;
        document.Gpu.WindowWidth = 1920;
        document.Gpu.WindowHeight = 1080;
        document.Vulkan.GpuId = 0;

        ShadPs4CustomConfigService.Save(emulatorDir, "CUSA03173", document);

        var loaded = ShadPs4CustomConfigService.LoadOrDefault(emulatorDir, "CUSA03173");
        Assert.True(loaded.General.DevKitMode);
        Assert.Equal(1920, loaded.Gpu.WindowWidth);
        Assert.Equal(1080, loaded.Gpu.WindowHeight);
        Assert.Equal(0, loaded.Vulkan.GpuId);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void GetConfigFilePath_UsesTitleIdFileName()
    {
        var path = ShadPs4CustomConfigService.GetConfigFilePath(@"C:\emu\shadPS4", "cusa03173");
        Assert.EndsWith(Path.Combine("user", "custom_configs", "CUSA03173.json"), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseResolution_ParsesPresetLabel()
    {
        Assert.True(ShadPs4CustomConfigService.TryParseResolution("1920 x 1080", out var width, out var height));
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void ShadPs4TitleIdResolver_UsesParamSfoAndRomInspector()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "AES_Lacrima_Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
        var gameDir = Path.Combine(tempRoot, "CUSA09999");
        var sceSysDir = Path.Combine(gameDir, "sce_sys");
        Directory.CreateDirectory(sceSysDir);
        File.WriteAllBytes(Path.Combine(sceSysDir, "param.sfo"), CreateParamSfo("CUSA09999", "Test Game"));

        var titleId = ShadPs4TitleIdResolver.Resolve(gameDir);
        Assert.Equal("CUSA09999", titleId);

        var inspected = RomInspector.Inspect(gameDir, DiscSection.PS4);
        Assert.Equal("CUSA09999", inspected.GameId);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void HardwareEnumeration_OnWindows_FindsGpuAndAudioDevices()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var gpus = ShadPs4HardwareEnumeration.EnumerateGpuAdapters();
        var audio = ShadPs4HardwareEnumeration.EnumerateAudioDevices();

        Assert.True(gpus.Count > 1, $"Expected multiple GPU entries, got: {string.Join(" | ", gpus.Select(g => g.Label))}");
        Assert.True(audio.Count > 1, $"Expected multiple audio devices, got: {string.Join(" | ", audio)}");
        Assert.Equal(ShadPs4HardwareEnumeration.AutoSelectGpuId, gpus[0].GpuId);
        Assert.Equal(ShadPs4HardwareEnumeration.AutoSelectGpuLabel, gpus[0].Label);
    }

    private static byte[] CreateParamSfo(string titleId, string title)
    {
        var keys = new[] { "TITLE_ID", "TITLE" };
        var values = new[] { titleId, title };

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(0x46535000u);
        writer.Write(0x0101u);
        var keyTableOffset = 20 + keys.Length * 16;
        var dataOffset = keyTableOffset;
        foreach (var value in values)
            dataOffset += System.Text.Encoding.UTF8.GetByteCount(value) + 1;

        writer.Write((uint)keyTableOffset);
        writer.Write((uint)keyTableOffset + keys.Length * 8);
        writer.Write((uint)keys.Length);

        var currentDataOffset = keyTableOffset + keys.Length * 8;
        for (var i = 0; i < keys.Length; i++)
        {
            writer.Write((ushort)(keyTableOffset + i * 8));
            writer.Write((byte)1);
            writer.Write((byte)0);
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(values[i] + "\0");
            writer.Write((uint)valueBytes.Length);
            writer.Write((uint)valueBytes.Length);
            writer.Write((uint)(currentDataOffset - (keyTableOffset + keys.Length * 8)));
            currentDataOffset += valueBytes.Length;
        }

        foreach (var key in keys)
        {
            var keyBytes = System.Text.Encoding.ASCII.GetBytes(key + "\0");
            writer.Write(keyBytes);
        }

        foreach (var value in values)
        {
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
            writer.Write(valueBytes);
        }

        return stream.ToArray();
    }
}
