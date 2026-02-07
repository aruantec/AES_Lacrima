using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AES_Controls.Composition;

/// <summary>
/// A message containing settings for the particle visual.
/// </summary>
internal class ParticleSettingsMessage
{
    public int ParticleCount;
    public Bitmap? Background;
    public Stretch Stretch;
    public bool IsPaused;
}