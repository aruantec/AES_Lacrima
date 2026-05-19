using System.Text.Json.Serialization;

namespace AES_Lacrima.Services.ShadPs4;

public sealed class ShadPs4CustomConfigDocument
{
    [JsonPropertyName("Audio")]
    public ShadPs4AudioConfig Audio { get; set; } = new();

    [JsonPropertyName("Debug")]
    public ShadPs4DebugConfig Debug { get; set; } = new();

    [JsonPropertyName("GPU")]
    public ShadPs4GpuConfig Gpu { get; set; } = new();

    [JsonPropertyName("General")]
    public ShadPs4GeneralConfig General { get; set; } = new();

    [JsonPropertyName("Input")]
    public ShadPs4InputConfig Input { get; set; } = new();

    [JsonPropertyName("Log")]
    public ShadPs4LogConfig Log { get; set; } = new();

    [JsonPropertyName("Vulkan")]
    public ShadPs4VulkanConfig Vulkan { get; set; } = new();
}

public sealed class ShadPs4AudioConfig
{
    [JsonPropertyName("audio_backend")]
    public int AudioBackend { get; set; }

    [JsonPropertyName("openal_main_output_device")]
    public string OpenalMainOutputDevice { get; set; } = "Default Device";

    [JsonPropertyName("openal_mic_device")]
    public string OpenalMicDevice { get; set; } = "Default Device";

    [JsonPropertyName("openal_padSpk_output_device")]
    public string OpenalPadSpkOutputDevice { get; set; } = "Default Device";

    [JsonPropertyName("sdl_main_output_device")]
    public string SdlMainOutputDevice { get; set; } = "Default Device";

    [JsonPropertyName("sdl_mic_device")]
    public string SdlMicDevice { get; set; } = "Default Device";

    [JsonPropertyName("sdl_padSpk_output_device")]
    public string SdlPadSpkOutputDevice { get; set; } = "Default Device";
}

public sealed class ShadPs4DebugConfig
{
    [JsonPropertyName("debug_dump")]
    public bool DebugDump { get; set; }

    [JsonPropertyName("shader_collect")]
    public bool ShaderCollect { get; set; }
}

public sealed class ShadPs4GpuConfig
{
    [JsonPropertyName("copy_gpu_buffers")]
    public bool CopyGpuBuffers { get; set; }

    [JsonPropertyName("direct_memory_access_enabled")]
    public bool DirectMemoryAccessEnabled { get; set; }

    [JsonPropertyName("dump_shaders")]
    public bool DumpShaders { get; set; }

    [JsonPropertyName("fsr_enabled")]
    public bool FsrEnabled { get; set; }

    [JsonPropertyName("full_screen")]
    public bool FullScreen { get; set; }

    [JsonPropertyName("full_screen_mode")]
    public string FullScreenMode { get; set; } = "Windowed";

    [JsonPropertyName("hdr_allowed")]
    public bool HdrAllowed { get; set; }

    [JsonPropertyName("null_gpu")]
    public bool NullGpu { get; set; }

    [JsonPropertyName("patch_shaders")]
    public bool PatchShaders { get; set; }

    [JsonPropertyName("present_mode")]
    public string PresentMode { get; set; } = "Immediate";

    [JsonPropertyName("rcas_attenuation")]
    public int RcasAttenuation { get; set; } = 250;

    [JsonPropertyName("rcas_enabled")]
    public bool RcasEnabled { get; set; } = true;

    [JsonPropertyName("readback_linear_images_enabled")]
    public bool ReadbackLinearImagesEnabled { get; set; }

    [JsonPropertyName("readbacks_mode")]
    public int ReadbacksMode { get; set; }

    [JsonPropertyName("vblank_frequency")]
    public int VblankFrequency { get; set; } = 60;

    [JsonPropertyName("window_height")]
    public int WindowHeight { get; set; } = 720;

    [JsonPropertyName("window_width")]
    public int WindowWidth { get; set; } = 1280;
}

public sealed class ShadPs4GeneralConfig
{
    [JsonPropertyName("connected_to_network")]
    public bool ConnectedToNetwork { get; set; }

    [JsonPropertyName("dev_kit_mode")]
    public bool DevKitMode { get; set; }

    [JsonPropertyName("extra_dmem_in_mbytes")]
    public int ExtraDmemInMbytes { get; set; }

    [JsonPropertyName("neo_mode")]
    public bool NeoMode { get; set; }

    [JsonPropertyName("psn_signed_in")]
    public bool PsnSignedIn { get; set; }

    [JsonPropertyName("show_splash")]
    public bool ShowSplash { get; set; }

    [JsonPropertyName("trophy_notification_duration")]
    public double TrophyNotificationDuration { get; set; } = 6.0;

    [JsonPropertyName("trophy_notification_side")]
    public string TrophyNotificationSide { get; set; } = "right";

    [JsonPropertyName("trophy_popup_disabled")]
    public bool TrophyPopupDisabled { get; set; }

    [JsonPropertyName("volume_slider")]
    public int VolumeSlider { get; set; } = 100;
}

public sealed class ShadPs4InputConfig
{
    [JsonPropertyName("background_controller_input")]
    public bool BackgroundControllerInput { get; set; } = true;

    [JsonPropertyName("camera_id")]
    public int CameraId { get; set; } = -1;

    [JsonPropertyName("cursor_hide_timeout")]
    public int CursorHideTimeout { get; set; } = 5;

    [JsonPropertyName("cursor_state")]
    public int CursorState { get; set; } = 1;

    [JsonPropertyName("motion_controls_enabled")]
    public bool MotionControlsEnabled { get; set; } = true;

    [JsonPropertyName("usb_device_backend")]
    public int UsbDeviceBackend { get; set; }
}

public sealed class ShadPs4LogConfig
{
    [JsonPropertyName("append")]
    public bool Append { get; set; }

    [JsonPropertyName("enable")]
    public bool Enable { get; set; } = true;

    [JsonPropertyName("filter")]
    public string Filter { get; set; } = string.Empty;

    [JsonPropertyName("max_skip_duration")]
    public int MaxSkipDuration { get; set; } = 5000;

    [JsonPropertyName("separate")]
    public bool Separate { get; set; }

    [JsonPropertyName("size_limit")]
    public long SizeLimit { get; set; } = 104857600;

    [JsonPropertyName("skip_duplicate")]
    public bool SkipDuplicate { get; set; } = true;

    [JsonPropertyName("sync")]
    public bool Sync { get; set; } = true;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "wincolor";
}

public sealed class ShadPs4VulkanConfig
{
    [JsonPropertyName("gpu_id")]
    public int GpuId { get; set; } = ShadPs4HardwareEnumeration.AutoSelectGpuId;

    [JsonPropertyName("pipeline_cache_archived")]
    public bool PipelineCacheArchived { get; set; }

    [JsonPropertyName("pipeline_cache_enabled")]
    public bool PipelineCacheEnabled { get; set; } = true;

    [JsonPropertyName("renderdoc_enabled")]
    public bool RenderdocEnabled { get; set; }

    [JsonPropertyName("vkcrash_diagnostic_enabled")]
    public bool VkcrashDiagnosticEnabled { get; set; }

    [JsonPropertyName("vkguest_markers")]
    public bool VkguestMarkers { get; set; }

    [JsonPropertyName("vkhost_markers")]
    public bool VkhostMarkers { get; set; }

    [JsonPropertyName("vkvalidation_core_enabled")]
    public bool VkvalidationCoreEnabled { get; set; } = true;

    [JsonPropertyName("vkvalidation_enabled")]
    public bool VkvalidationEnabled { get; set; }

    [JsonPropertyName("vkvalidation_gpu_enabled")]
    public bool VkvalidationGpuEnabled { get; set; }

    [JsonPropertyName("vkvalidation_sync_enabled")]
    public bool VkvalidationSyncEnabled { get; set; }
}
