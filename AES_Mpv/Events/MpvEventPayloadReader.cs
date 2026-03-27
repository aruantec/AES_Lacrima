using System.Diagnostics.CodeAnalysis;

namespace AES_Mpv.Events;

public static class MpvEventPayloadReader
{
    [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "mpv event payloads are marshalled into specific unmanaged structs used by the AES player pipeline.")]
    public static unsafe T Read<T>(this Native.MpvEvent mpvEvent) where T : struct
    {
        return Marshal.PtrToStructure<T>((IntPtr)mpvEvent.Data);
    }
}
