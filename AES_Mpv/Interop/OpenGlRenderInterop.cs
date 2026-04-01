namespace AES_Mpv.Interop;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MpvRenderContext;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MpvRenderParameter
{
    public MpvRenderParameterType Type;
    public void* Data;
}

public enum MpvRenderParameterType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4,
    AdvancedControl = 10,
    NextFrameInfo = 11,
    BlockForTargetTime = 12,
    SkipRendering = 13,
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct OpenGlAddressResolverContext
{
    public MpvOpenGlAddressResolver Resolve;
    public void* ResolveContext;
    public void* ExtraExtensions;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvOpenGlFramebuffer
{
    public int Framebuffer;
    public int Width;
    public int Height;
    public int InternalFormat;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public unsafe delegate void MpvRenderUpdateCallback(void* context);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate nint MpvOpenGlAddressResolver(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

internal static partial class MpvRenderApi
{
    [LibraryImport(Native.MpvNativeLibrary.ImportName, EntryPoint = "mpv_render_context_create")]
    internal static unsafe partial int CreateContext(MpvRenderContext** result, Native.MpvHandle* handle, MpvRenderParameter* parameters);

    [LibraryImport(Native.MpvNativeLibrary.ImportName, EntryPoint = "mpv_render_context_set_update_callback")]
    internal static unsafe partial void SetUpdateCallback(MpvRenderContext* context, MpvRenderUpdateCallback? callback, void* callbackContext);

    [LibraryImport(Native.MpvNativeLibrary.ImportName, EntryPoint = "mpv_render_context_update")]
    internal static unsafe partial ulong Update(MpvRenderContext* context);

    [LibraryImport(Native.MpvNativeLibrary.ImportName, EntryPoint = "mpv_render_context_render")]
    internal static unsafe partial int Render(MpvRenderContext* context, MpvRenderParameter* parameters);

    [LibraryImport(Native.MpvNativeLibrary.ImportName, EntryPoint = "mpv_render_context_report_swap")]
    internal static unsafe partial void ReportSwap(MpvRenderContext* context);

    [LibraryImport(Native.MpvNativeLibrary.ImportName, EntryPoint = "mpv_render_context_free")]
    internal static unsafe partial void Free(MpvRenderContext* context);
}
