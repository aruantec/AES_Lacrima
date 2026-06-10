using System;
using Avalonia.Media.Imaging;

namespace AES_Emulation.EmulationHandlers;

public sealed class FlatpakApplicationItem
{
    public static FlatpakApplicationItem Empty { get; } = new(string.Empty, "AppImage / custom (default)");

    public FlatpakApplicationItem(string applicationId, string displayName, Bitmap? icon = null)
    {
        ApplicationId = applicationId ?? string.Empty;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? applicationId : displayName;
        Icon = icon;
    }

    public string ApplicationId { get; }

    public string DisplayName { get; }

    public Bitmap? Icon { get; }

    public bool HasIcon => Icon != null;

    public bool IsEmpty => string.IsNullOrWhiteSpace(ApplicationId);

    public string Label => IsEmpty ? DisplayName : $"{DisplayName} ({ApplicationId})";

    public override string ToString() => Label;

    public override bool Equals(object? obj)
        => obj is FlatpakApplicationItem other &&
           string.Equals(ApplicationId, other.ApplicationId, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
        => StringComparer.OrdinalIgnoreCase.GetHashCode(ApplicationId);
}
