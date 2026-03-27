using System.Runtime.InteropServices.Marshalling;

namespace AES_Mpv.Native;

internal static class Utf8Pointer
{
    internal static unsafe string? ToManaged(nint ptr)
    {
        if (ptr == 0)
            return null;

        return Utf8StringMarshaller.ConvertToManaged((byte*)ptr);
    }
}

public unsafe struct MpvHandle;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MpvNode
{
    public void* Data;
    public MpvFormat Format;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MpvPropertyEvent
{
    private nint _name;
    public MpvFormat Format;
    public void* Data;

    public string Name => Utf8Pointer.ToManaged(_name) ?? string.Empty;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEndFileInfo
{
    public MpvError Error;
    public long Reason;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct MpvEvent
{
    public MpvEventId EventId;
    public MpvError Error;
    public ulong ReplyUserData;
    public void* Data;
}

public enum MpvError
{
    Success = 0,
    EventQueueFull = -1,
    NoMemory = -2,
    Uninitialized = -3,
    InvalidParameter = -4,
    OptionNotFound = -5,
    OptionFormat = -6,
    OptionError = -7,
    PropertyNotFound = -8,
    PropertyFormat = -9,
    PropertyUnavailable = -10,
    PropertyError = -11,
    CommandError = -12,
    LoadingFailed = -13,
    AudioOutputInitFailed = -14,
    VideoOutputInitFailed = -15,
    NothingToPlay = -16,
    UnknownFormat = -17,
    Unsupported = -18,
    NotImplemented = -19,
    Generic = -20,
}

public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public unsafe delegate void MpvWakeupCallback(void* context);
