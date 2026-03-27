using AES_Mpv.Native;

namespace AES_Mpv;

public sealed class AesMpvException : Exception
{
    public AesMpvException(MpvError error, string message = "")
        : base(message)
    {
        Error = error;
    }

    public MpvError Error { get; }
}
