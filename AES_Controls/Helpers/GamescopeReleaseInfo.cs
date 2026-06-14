namespace AES_Controls.Helpers;

public enum GamescopeInstallMethod
{
    DistroPackage,
    SourceBuild,
}

/// <summary>
/// Describes an installable gamescope version from distro packages or Valve git tags.
/// </summary>
public sealed class GamescopeReleaseInfo
{
    public string Tag { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public GamescopeInstallMethod InstallMethod { get; set; }

    /// <summary>
    /// Full distro package version (for example <c>3.16.22+ds-1</c> on Debian/Ubuntu).
    /// </summary>
    public string? PackageVersion { get; set; }

    public bool IsPrerelease { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? Tag : Title;
}
