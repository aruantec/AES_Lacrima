using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace LibMPVSharp.Extensions
{
    public unsafe static class MpvEventExtensions
    {
        [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = "mpv event payloads are marshalled into known unmanaged structs used by the player event pipeline.")]
        public static T ReadData<T>(this MpvEvent mpvEvent) where T: struct
        {
            return Marshal.PtrToStructure<T>((IntPtr)mpvEvent.data);
        }
    }
}
