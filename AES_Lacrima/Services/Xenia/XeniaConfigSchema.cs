using System.Collections.Generic;
using System.Linq;

namespace AES_Lacrima.Services.Xenia;

public static class XeniaConfigSchema
{
    private static readonly string[] ApuBackends = ["any", "nop", "sdl", "xaudio2"];
    private static readonly string[] XmaDecoders = ["fake", "master", "old", "new"];
    private static readonly string[] GpuBackends = ["any", "d3d12", "vulkan", "null"];
    private static readonly string[] ReadbackResolveModes = ["none", "fast", "full"];
    private static readonly string[] RenderTargetD3d12 = ["any", "rtv", "rov"];
    private static readonly string[] RenderTargetVulkan = ["any", "fbo", "fsi"];
    private static readonly string[] PostProcessScaling = ["(default)", "bilinear", "cas", "fsr"];
    private static readonly string[] PostProcessScalingValues = ["", "bilinear", "cas", "fsr"];
    private static readonly string[] PostProcessAa = ["(none)", "fxaa", "fxaa_extreme"];
    private static readonly string[] PostProcessAaValues = ["", "fxaa", "fxaa_extreme"];
    private static readonly string[] HidBackends = ["any", "nop", "sdl", "winkey", "xinput"];
    private static readonly string[] AnisotropicLabels =
    [
        "-1 (No override)",
        "0 (Disabled)",
        "1 (1x)",
        "2 (2x)",
        "3 (4x)",
        "4 (8x)",
        "5 (16x)"
    ];

    private static readonly string[] AnisotropicValues = ["-1", "0", "1", "2", "3", "4", "5"];

    public static readonly IReadOnlyList<XeniaConfigFieldDefinition> AllFields = BuildFields();

    public static IReadOnlyList<string> SectionHeaders =>
        AllFields.Select(static definition => definition.Section).Distinct().ToList();

    public static IReadOnlyList<XeniaConfigFieldDefinition> GetFieldsForSection(string section) =>
        AllFields.Where(definition => string.Equals(definition.Section, section, System.StringComparison.OrdinalIgnoreCase)).ToList();

    private static IReadOnlyList<XeniaConfigFieldDefinition> BuildFields() =>
    [
        F("APU", "apu", "Audio backend", "Audio system.", Choice(ApuBackends)),
        F("APU", "apu_max_queued_frames", "Max queued audio frames", "Range 4–64.", Int(4, 64)),
        F("APU", "mute", "Mute audio", Bool()),
        F("APU", "enable_xmp", "Enable XMP playback", Bool()),
        F("APU", "use_dedicated_xma_thread", "Dedicated XMA thread", Bool()),
        F("APU", "xma_decoder", "XMA decoder", Choice(XmaDecoders)),
        F("APU", "ffmpeg_verbose", "Verbose FFmpeg", Bool()),

        F("CPU", "disable_prefetch_and_cachecontrol", "Disable prefetch/cache flush translation", "May improve performance.", Bool()),
        F("CPU", "disable_context_promotion", "Disable context promotion", "Needed for some sports games.", Bool()),
        F("CPU", "ignore_trap_instructions", "Ignore trap instructions", Bool()),
        F("CPU", "clock_no_scaling", "Disable clock scaling", Bool()),
        F("CPU", "inline_mmio_access", "Inline MMIO access", Bool()),

        F("D3D12", "d3d12_adapter", "D3D12 adapter", "Index: -1 any, -2 WARP.", GpuAdapter()),
        F("D3D12", "d3d12_bindless", "Bindless resources", Bool()),
        F("D3D12", "d3d12_debug", "D3D12 debug layer", Bool()),
        F("D3D12", "d3d12_queue_priority", "Command queue priority", "0 normal, 1 high, 2 realtime.", Int(0, 2)),
        F("D3D12", "d3d12_pipeline_creation_threads", "Pipeline creation threads", "-1 auto.", Int(-1, 128)),
        F("D3D12", "d3d12_allow_variable_refresh_rate_and_tearing", "VRR and tearing", Bool()),
        F("D3D12", "d3d12_submit_on_primary_buffer_end", "Submit on primary buffer end", Bool()),

        F("Display", "fullscreen", "Start fullscreen", Bool()),
        F("Display", "postprocess_scaling_and_sharpening", "Scaling / sharpening", Choice(PostProcessScaling, PostProcessScalingValues)),
        F("Display", "postprocess_antialiasing", "Post-process AA", Choice(PostProcessAa, PostProcessAaValues)),
        F("Display", "postprocess_ffx_cas_additional_sharpness", "CAS additional sharpness", Float(0, 1)),
        F("Display", "postprocess_ffx_fsr_sharpness_reduction", "FSR sharpness reduction", Float(0, 2)),
        F("Display", "postprocess_ffx_fsr_max_upsampling_passes", "FSR max upsampling passes", Int(1, 8)),
        F("Display", "postprocess_dither", "Dither to 8-bit", Bool()),
        F("Display", "present_letterbox", "Letterbox aspect ratio", Bool()),
        F("Display", "present_safe_area_x", "Safe area width %", Int(0, 100)),
        F("Display", "present_safe_area_y", "Safe area height %", Int(0, 100)),

        F("GPU", "gpu", "Graphics backend", Choice(GpuBackends)),
        F("GPU", "vsync", "VSync", Bool()),
        F("GPU", "framerate_limit", "Framerate limit", "0 = unlimited.", Int(0, 240)),
        F("GPU", "anisotropic_override", "Anisotropic filtering", Choice(AnisotropicLabels, AnisotropicValues)),
        F("GPU", "async_shader_compilation", "Async shader compilation", Bool()),
        F("GPU", "clear_memory_page_state", "Clear memory page state", "Team Ninja fix.", Bool()),
        F("GPU", "depth_float24_convert_in_pixel_shader", "Depth float24 in PS", Bool()),
        F("GPU", "depth_float24_round", "Depth float24 round", Bool()),
        F("GPU", "readback_resolve", "Readback resolve", Choice(ReadbackResolveModes)),
        F("GPU", "render_target_path_d3d12", "D3D12 render target path", Choice(RenderTargetD3d12)),
        F("GPU", "render_target_path_vulkan", "Vulkan render target path", Choice(RenderTargetVulkan)),
        F("GPU", "store_shaders", "Persist shaders", Bool()),
        F("GPU", "half_pixel_offset", "Half-pixel offset", Bool()),
        F("GPU", "texture_cache_memory_limit_soft", "Texture cache soft (MB)", Int(64, 4096)),
        F("GPU", "texture_cache_memory_limit_hard", "Texture cache hard (MB)", Int(128, 8192)),
        F("GPU", "query_occlusion_querybatch_range", "Occlusion querybatch range", Int(0, 1000)),
        F("GPU", "query_occlusion_sample_lower_threshold", "Occlusion lower threshold", Int(-1, 1000)),
        F("GPU", "query_occlusion_sample_upper_threshold", "Occlusion upper threshold", Int(0, 1000)),

        F("General", "discord", "Discord rich presence", Bool()),
        F("General", "debug", "Debug mode", Bool()),
        F("General", "apply_patches", "Apply patches", Bool()),
        F("General", "time_scalar", "Time scalar", Float(0.1, 10)),
        F("General", "priority_class", "Process priority", Int(0, 2)),
        F("General", "controller_hotkeys", "Controller hotkeys", Bool()),
        F("General", "disable_doubleclick_fullscreen", "Disable dbl-click fullscreen", Bool()),
        F("General", "recent_titles_entry_amount", "Recent titles count", Int(0, 50)),

        F("HID", "hid", "Input backend", Choice(HidBackends)),
        F("HID", "vibration", "Controller vibration", Bool()),
        F("HID", "guide_button", "Forward guide button", Bool()),
        F("HID", "keyboard_mode", "Keyboard mode", Int(0, 2)),
        F("HID", "left_stick_deadzone_percentage", "Left stick deadzone", Float(0, 1)),
        F("HID", "right_stick_deadzone_percentage", "Right stick deadzone", Float(0, 1)),

        F("Kernel", "apply_title_update", "Apply title updates", Bool()),
        F("Kernel", "allow_incompatible_title_update", "Allow incompatible updates", Bool()),
        F("Kernel", "console_type", "Console type", Int(0, 1)),
        F("Kernel", "ignore_thread_affinities", "Ignore thread affinities", Bool()),
        F("Kernel", "ignore_thread_priorities", "Ignore thread priorities", Bool()),
        F("Kernel", "kernel_display_gamma_type", "Display gamma type", Int(0, 3)),
        F("Kernel", "kernel_display_gamma_power", "Display gamma power", Float(1, 3)),

        F("Logging", "log_level", "Log level", Int(0, 3)),
        F("Logging", "enable_console", "Console window", Bool()),
        F("Logging", "log_to_stdout", "Log to stdout", Bool()),
        F("Logging", "flush_log", "Flush log", Bool()),

        F("Memory", "protect_zero", "Protect zero page", Bool()),
        F("Memory", "writable_executable_memory", "Writable executable memory", Bool()),

        F("Storage", "mount_cache", "Mount cache", Bool()),
        F("Storage", "mount_scratch", "Mount scratch", Bool()),
        F("Storage", "mount_memory_unit", "Mount memory unit", Bool()),
        F("Storage", "force_mount_devkit", "Force devkit mount", Bool()),

        F("UI", "window_size_x", "Window width", Int(320, 7680)),
        F("UI", "window_size_y", "Window height", Int(240, 4320)),
        F("UI", "font_size", "UI font size", Int(8, 32)),
        F("UI", "headless", "Headless", Bool()),
        F("UI", "show_profiler", "Show profiler", Bool()),
        F("UI", "show_achievement_notification", "Achievement notifications", Bool()),
        F("UI", "storage_selection_dialog", "Storage selection dialog", Bool()),

        F("Video", "avpack", "AV pack / video mode", Int(0, 8)),
        F("Video", "enable_3d_mode", "Stereoscopic 3D", Bool()),
        F("Video", "interlaced", "Interlaced", Bool()),
        F("Video", "custom_internal_display_resolution_x", "Custom internal width", Int(0, 1920)),
        F("Video", "custom_internal_display_resolution_y", "Custom internal height", Int(0, 1080)),

        F("Vulkan", "vulkan_device", "Vulkan device", GpuAdapter()),
        F("Vulkan", "vulkan_validation", "Vulkan validation", Bool()),
        F("Vulkan", "vulkan_sparse_shared_memory", "Sparse shared memory", Bool()),
        F("Vulkan", "vulkan_pipeline_creation_threads", "Pipeline threads", Int(-1, 128)),
        F("Vulkan", "vulkan_allow_present_mode_immediate", "Immediate present", Bool()),
        F("Vulkan", "vulkan_allow_present_mode_mailbox", "Mailbox present", Bool()),
        F("Vulkan", "vulkan_allow_present_mode_fifo_relaxed", "FIFO relaxed present", Bool()),
        F("Vulkan", "vulkan_semaphore_reuse_workaround", "Semaphore reuse workaround", Bool()),

        F("Win32", "win32_high_resolution_timer", "High-resolution timer", Bool()),
        F("Win32", "win32_mmcss", "MMCSS scheduling", Bool()),

        F("x64", "enable_host_guest_stack_synchronization", "Host/guest stack sync", Bool()),
        F("x64", "x64_extension_mask", "CPU extension mask", Int(-1, 8191)),

        F("HACKS", "ac6_ground_fix", "AC6 ground fix", Bool()),
    ];

    private static FieldBuilder Bool() => new(XeniaConfigValueKind.Boolean);
    private static FieldBuilder Int(int min, int max) => new(XeniaConfigValueKind.Integer, intMin: min, intMax: max);
    private static FieldBuilder Float(double min, double max) => new(XeniaConfigValueKind.Float, floatMin: min, floatMax: max);
    private static FieldBuilder Choice(string[] values) => new(XeniaConfigValueKind.Choice, choiceLabels: values, choiceValues: values);
    private static FieldBuilder Choice(string[] labels, string[] values) => new(XeniaConfigValueKind.Choice, choiceLabels: labels, choiceValues: values);
    private static FieldBuilder GpuAdapter() => new(XeniaConfigValueKind.GpuAdapterIndex);

    private static XeniaConfigFieldDefinition F(string section, string key, string label, FieldBuilder builder) =>
        builder.Build(section, key, label, null);

    private static XeniaConfigFieldDefinition F(string section, string key, string label, string? description, FieldBuilder builder) =>
        builder.Build(section, key, label, description);

    private sealed class FieldBuilder(
        XeniaConfigValueKind kind,
        string[]? choiceLabels = null,
        string[]? choiceValues = null,
        int? intMin = null,
        int? intMax = null,
        double? floatMin = null,
        double? floatMax = null)
    {
        public XeniaConfigFieldDefinition Build(string section, string key, string label, string? description) =>
            new(section, key, label, description, kind, choiceLabels, choiceValues, intMin, intMax, floatMin, floatMax);
    }
}
