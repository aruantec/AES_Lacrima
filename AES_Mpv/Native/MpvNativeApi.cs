namespace AES_Mpv.Native;

internal static partial class MpvNativeApi
{
    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_client_api_version")]
    internal static partial ulong ClientApiVersion();

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_create")]
    internal static unsafe partial MpvHandle* Create();

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_initialize")]
    internal static unsafe partial int Initialize(MpvHandle* handle);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_destroy")]
    internal static unsafe partial void Destroy(MpvHandle* handle);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_terminate_destroy")]
    internal static unsafe partial void TerminateAndDestroy(MpvHandle* handle);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_create_client", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial MpvHandle* CreateClient(MpvHandle* handle, string? name);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_create_weak_client", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial MpvHandle* CreateWeakClient(MpvHandle* handle, string? name);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int SetProperty(MpvHandle* handle, string name, MpvFormat format, void* data);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int SetPropertyString(MpvHandle* handle, string name, string? data);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int GetProperty(MpvHandle* handle, string name, MpvFormat format, void* data);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int ObserveProperty(MpvHandle* handle, ulong replyUserData, string name, MpvFormat format);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_command")]
    internal static unsafe partial int Command(MpvHandle* handle, byte** args);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_command_async")]
    internal static unsafe partial int CommandAsync(MpvHandle* handle, ulong replyUserData, byte** args);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_abort_async_command")]
    internal static unsafe partial void AbortAsyncCommand(MpvHandle* handle, ulong replyUserData);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_wait_event")]
    internal static unsafe partial MpvEvent* WaitEvent(MpvHandle* handle, double timeout);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_set_wakeup_callback")]
    internal static unsafe partial void SetWakeupCallback(MpvHandle* handle, MpvWakeupCallback callback, void* context);

    [LibraryImport(MpvNativeLibrary.ImportName, EntryPoint = "mpv_error_string")]
    private static unsafe partial byte* ErrorStringNative(int error);

    internal static unsafe string DescribeError(int error)
        => Utf8Pointer.ToManaged((nint)ErrorStringNative(error)) ?? $"mpv error {error}";
}
