namespace LibMPVSharp
{
    /// <summary>
    /// Minimal mpv property constants used by AudioPlayer.
    /// </summary>
    public partial class MPVMediaPlayer
    {
        public static class VideoOpts
        {
            public static readonly string Vo = "vo";
        }

        public static class Properties
        {
            public static readonly string Duration = "duration";
            public static readonly string TimePos = "time-pos";
        }
    }
}
