using System.Collections.Generic;
using System.Linq;

namespace AES_Lacrima.Services.Rpcs3;

public enum Rpcs3ConfigValueKind
{
    Boolean,
    Integer,
    Float,
    String,
    Choice
}

public sealed record Rpcs3ConfigFieldDefinition(
    string Section,
    string Key,
    string Label,
    string? Description,
    Rpcs3ConfigValueKind Kind,
    string? UiSection = null,
    string? ParentSection = null,
    string[]? ChoiceLabels = null,
    string[]? ChoiceValues = null,
    int? IntMin = null,
    int? IntMax = null,
    double? FloatMin = null,
    double? FloatMax = null)
{
    public string DisplaySection => UiSection ?? Section;
}

public static class Rpcs3ConfigSchema
{
    public const string VideoSection = "Video";
    public const string VulkanSection = "Vulkan";
    public const string AdvancedUiSection = "Advanced";
    public const string ResolutionScaleKey = "Resolution Scale";
    public const string ResolutionScaleThresholdKey = "Minimum Scalable Dimension";

    private static readonly string[] PpuDecoders =
    [
        "Interpreter (static)",
        "Interpreter (dynamic)",
        "Recompiler (LLVM)",
        "Recompiler (ASMJIT)"
    ];

    private static readonly string[] SpuDecoders = PpuDecoders;

    private static readonly string[] Renderers = ["Vulkan", "OpenGL", "Null"];

    private static readonly string[] AspectRatios = ["Auto", "4:3", "16:9"];

    private static readonly string[] ShaderModes =
    [
        "Legacy",
        "Async",
        "Async with Shader Interpreter",
        "Shader Interpreter only"
    ];

    private static readonly string[] VsyncModes = ["Off", "On", "Adaptive", "Relaxed"];

    private static readonly string[] MsaaLevels = ["Auto", "Disabled", "2x", "4x", "8x", "16x"];

    private static readonly string[] AudioRenderers = ["Cubeb", "XAudio2", "Null"];

    private static readonly string[] AudioFormats = ["Stereo", "Surround 5.1", "Surround 7.1"];

    private static readonly string[] AnisotropicLabels =
    [
        "Default",
        "Disabled",
        "1x",
        "2x",
        "4x",
        "8x",
        "16x"
    ];

    private static readonly string[] AnisotropicValues = ["-1", "0", "1", "2", "3", "4", "5"];

    private static readonly string[] Languages =
    [
        "English (US)",
        "English (UK)",
        "Japanese",
        "French",
        "Spanish",
        "German",
        "Italian",
        "Dutch",
        "Portuguese (Portugal)",
        "Russian",
        "Korean",
        "Chinese (Simplified)",
        "Chinese (Traditional)"
    ];

    private static readonly string[] SleepTimerAccuracy =
    [
        "As Host",
        "Usleep Only",
        "All Timers"
    ];

    private static readonly string[] RsxFifoAccuracy =
    [
        "Fast",
        "Atomic",
        "Atomic & Ordered"
    ];

    private static readonly string[] VkQueueScheduler = ["Safe", "Fast"];

    private static readonly string[] VkFullscreenMode =
    [
        "Automatic",
        "Prefer exclusive fullscreen",
        "Prefer borderless fullscreen"
    ];

    public static readonly IReadOnlyList<Rpcs3ConfigFieldDefinition> AllFields = BuildFields();

    public static IReadOnlyList<string> UiSectionHeaders =>
        AllFields.Select(static definition => definition.DisplaySection).Distinct().ToList();

    public static IReadOnlyList<Rpcs3ConfigFieldDefinition> GetFieldsForUiSection(string uiSection) =>
        AllFields.Where(definition => string.Equals(definition.DisplaySection, uiSection, System.StringComparison.OrdinalIgnoreCase)).ToList();

    public static string ComposeKey(string section, string key, string? parentSection = null) =>
        string.IsNullOrWhiteSpace(parentSection)
            ? $"{section}\u001f{key}"
            : $"{parentSection}\u001f{section}\u001f{key}";

    public static bool TryParseKey(string compositeKey, out string section, out string key, out string? parentSection)
    {
        section = string.Empty;
        key = string.Empty;
        parentSection = null;

        var parts = compositeKey.Split('\u001f');
        if (parts.Length == 2)
        {
            section = parts[0];
            key = parts[1];
            return true;
        }

        if (parts.Length == 3)
        {
            parentSection = parts[0];
            section = parts[1];
            key = parts[2];
            return true;
        }

        return false;
    }

    private static IReadOnlyList<Rpcs3ConfigFieldDefinition> BuildFields() =>
    [
        F("Core", "PPU Decoder", "PPU decoder", Choice(PpuDecoders)),
        F("Core", "SPU Decoder", "SPU decoder", Choice(SpuDecoders)),
        F("Core", "SPU loop detection", "SPU loop detection", Bool()),
        F("Core", "Preferred SPU Threads", "Preferred SPU threads", Int(0, 6)),
        F("Core", "PPU Threads", "PPU threads", Int(1, 16)),
        F("Core", "Max CPU Preempt Count", "Max CPU preempt count", Int(0, 4096)),
        F("Core", "Thread Scheduler Mode", "Thread scheduler", Choice(["Operating System", "RPCS3"])),
        F("Core", "Set DAZ and FTZ", "Set DAZ and FTZ", Bool()),

        F(VideoSection, "Renderer", "Renderer", Choice(Renderers)),
        F(VideoSection, ResolutionScaleKey, "Resolution scale (%)", Int(25, 800)),
        F(VideoSection, ResolutionScaleThresholdKey, "Resolution scale threshold", Int(1, 1024),
            description: "Minimum framebuffer dimension to upscale (RPCS3: Minimum Scalable Dimension)."),
        F(VideoSection, "Aspect ratio", "Aspect ratio", Choice(AspectRatios)),
        F(VideoSection, "Frame limit", "Frame limit", Choice(["Off", "30", "50", "60", "120", "Auto"])),
        F(VideoSection, "VSync Mode", "VSync", Choice(VsyncModes)),
        F(VideoSection, "Stretch To Display Area", "Stretch to display area", Bool()),
        F(VideoSection, "Anisotropic Filter Override", "Anisotropic filter", Choice(AnisotropicLabels, AnisotropicValues)),
        F(VideoSection, "MSAA", "Anti-aliasing", Choice(MsaaLevels)),
        F(VideoSection, "Shader Mode", "Shader mode", Choice(ShaderModes)),
        F(VideoSection, "Write Color Buffers", "Write color buffers", Bool()),
        F(VideoSection, "Write Depth Buffer", "Write depth buffer", Bool()),
        F(VideoSection, "Read Color Buffers", "Read color buffers", Bool()),
        F(VideoSection, "Read Depth Buffer", "Read depth buffer", Bool()),
        F(VideoSection, "Multithreaded RSX", "Multithreaded RSX", Bool()),

        F("Audio", "Renderer", "Audio renderer", Choice(AudioRenderers)),
        F("Audio", "Master Volume", "Master volume", Int(0, 100)),
        F("Audio", "Enable Time Stretching", "Time stretching", Bool()),
        F("Audio", "Audio Format", "Audio format", Choice(AudioFormats)),
        F("Audio", "Disable Audio Buffer", "Disable audio buffer", Bool()),
        F("Audio", "Enable Buffering", "Enable buffering", Bool()),
        F("Audio", "Convert to 16-bit", "Convert to 16-bit", Bool()),

        F("System", "Language", "Language", Choice(Languages)),

        F("Miscellaneous", "Start games in fullscreen mode", "Start fullscreen", Bool()),
        F("Miscellaneous", "Automatically start games after boot", "Auto-start after boot", Bool()),
        F("Miscellaneous", "Pause emulation on RPCS3 focus loss", "Pause on focus loss", Bool()),
        F("Miscellaneous", "Exit RPCS3 when process finishes", "Exit when game closes", Bool()),
        F("Miscellaneous", "Prevent display sleep while running games", "Prevent display sleep", Bool()),
        F("Miscellaneous", "Show trophy popups", "Show trophy popups", Bool()),

        F("Input/Output", "Keyboard Handler", "Keyboard handler", Choice(["Null", "Basic"])),
        F("Input/Output", "Mouse Handler", "Mouse handler", Choice(["Null", "Basic"])),
        F("Input/Output", "Camera type", "Camera type", Choice(["Unknown", "EyeToy", "PS Eye", "UVC 1.1"])),
        F("Input/Output", "Camera flip", "Camera flip", Choice(["None", "Horizontal", "Vertical", "Both"])),
        F("Input/Output", "Camera FPS", "Camera FPS", Int(30, 120)),

        Adv("Core", "Debug Console Mode", "Debug console mode", Bool()),
        Adv("Core", "Use Accurate DFMA", "Accurate DFMA", Bool()),
        Adv("Core", "Accurate RSX reservation access", "Accurate RSX reservation access", Bool()),
        Adv("Core", "Accurate SPU DMA", "Accurate SPU DMA", Bool()),
        Adv("Core", "PPU LLVM Java Mode Handling", "PPU LLVM Java mode handling", Bool()),
        Adv("Core", "PPU Accurate Vector NaN Values", "PPU accurate vector NaN", Bool()),
        Adv("Core", "LLVM Precompilation", "LLVM precompilation", Bool()),
        Adv("Core", "Sleep Timers Accuracy", "Sleep timers accuracy", Choice(SleepTimerAccuracy)),
        Adv("Core", "Max SPURS Threads", "Max SPURS threads", Int(0, 6)),
        Adv("Core", "Clocks scale", "Clocks scale (%)", Int(10, 3000)),
        Adv("Core", "RSX FIFO Fetch Accuracy", "RSX FIFO accuracy", Choice(RsxFifoAccuracy)),

        Adv(VideoSection, "Disable On-Disk Shader Cache", "Disable on-disk shader cache", Bool()),
        Adv(VideoSection, "Disable Vertex Cache", "Disable vertex cache", Bool()),
        Adv(VideoSection, "Allow Host GPU Labels", "Allow host GPU labels", Bool()),
        Adv(VideoSection, "Handle RSX Memory Tiling", "Handle RSX memory tiling", Bool()),
        Adv(VideoSection, "Strict Rendering Mode", "Strict rendering mode", Bool()),
        Adv(VideoSection, "Relaxed ZCULL Sync", "Relaxed ZCULL sync", Bool()),
        Adv(VideoSection, "Texture LOD Bias Addend", "Texture LOD bias", Float(-12, 12)),
        Adv(VideoSection, "Driver Wake-Up Delay", "Driver wake-up delay (µs)", Int(0, 16667)),
        Adv(VideoSection, "Vblank Rate", "VBlank rate (Hz)", Int(1, 6000)),
        Adv(VideoSection, "Vblank NTSC Fixup", "VBlank NTSC fixup", Bool()),
        Adv(VideoSection, "Force CPU Blit", "Force CPU blit", Bool()),
        Adv(VideoSection, "Strict Texture Flushing", "Strict texture flushing", Bool()),
        Adv(VideoSection, "Disable FIFO Reordering", "Disable FIFO reordering", Bool()),
        Adv(VideoSection, "Disable ZCull Occlusion Queries", "Disable ZCull occlusion queries", Bool()),

        Adv(VulkanSection, "Exclusive Fullscreen Mode", "Exclusive fullscreen mode", Choice(VkFullscreenMode), parent: VideoSection),
        Adv(VulkanSection, "Asynchronous Queue Scheduler", "Vulkan queue scheduler", Choice(VkQueueScheduler), parent: VideoSection),
        Adv(VulkanSection, "Asynchronous Texture Streaming", "Async texture streaming", Bool(), parent: VideoSection),

        Adv("Miscellaneous", "Silence All Logs", "Silence all logs", Bool())
    ];

    private static FieldBuilder Bool() => new(Rpcs3ConfigValueKind.Boolean);
    private static FieldBuilder Int(int min, int max) => new(Rpcs3ConfigValueKind.Integer, intMin: min, intMax: max);
    private static FieldBuilder Float(double min, double max) => new(Rpcs3ConfigValueKind.Float, floatMin: min, floatMax: max);
    private static FieldBuilder Choice(string[] values) => new(Rpcs3ConfigValueKind.Choice, choiceLabels: values, choiceValues: values);
    private static FieldBuilder Choice(string[] labels, string[] values) => new(Rpcs3ConfigValueKind.Choice, choiceLabels: labels, choiceValues: values);

    private static Rpcs3ConfigFieldDefinition F(
        string section,
        string key,
        string label,
        FieldBuilder builder,
        string? uiSection = null,
        string? description = null) =>
        builder.Build(section, key, label, description, uiSection ?? section);

    private static Rpcs3ConfigFieldDefinition Adv(
        string section,
        string key,
        string label,
        FieldBuilder builder,
        string? parent = null,
        string? description = null) =>
        builder.Build(section, key, label, description, AdvancedUiSection, parent);

    private sealed class FieldBuilder(
        Rpcs3ConfigValueKind kind,
        string[]? choiceLabels = null,
        string[]? choiceValues = null,
        int? intMin = null,
        int? intMax = null,
        double? floatMin = null,
        double? floatMax = null)
    {
        public Rpcs3ConfigFieldDefinition Build(
            string section,
            string key,
            string label,
            string? description,
            string uiSection,
            string? parentSection = null) =>
            new(section, key, label, description, kind, uiSection, parentSection, choiceLabels, choiceValues, intMin, intMax, floatMin, floatMax);
    }
}
