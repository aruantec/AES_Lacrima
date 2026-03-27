using System.Runtime.InteropServices.Marshalling;
using AES_Mpv.Native;

namespace AES_Mpv.Events;

public unsafe static class MpvPropertyValueReader
{
    public static string ReadString(this ref MpvPropertyEvent property)
    {
        EnsureFormat(property.Name, property.Format, MpvFormat.String);
        return Utf8StringMarshaller.ConvertToManaged((byte*)property.Data)!;
    }

    public static long ReadInt64(this ref MpvPropertyEvent property)
    {
        EnsureFormat(property.Name, property.Format, MpvFormat.Int64);
        return *(long*)property.Data;
    }

    public static bool ReadFlag(this ref MpvPropertyEvent property)
    {
        EnsureFormat(property.Name, property.Format, MpvFormat.Flag);
        return *(int*)property.Data != 0;
    }

    public static double ReadDouble(this ref MpvPropertyEvent property)
    {
        EnsureFormat(property.Name, property.Format, MpvFormat.Double);
        return *(double*)property.Data;
    }

    private static void EnsureFormat(string name, MpvFormat actual, MpvFormat expected)
    {
        if (actual != expected)
            throw new FormatException($"The property '{name}' format is {actual}, but expected {expected}.");
    }
}
