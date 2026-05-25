using System.Collections.Generic;

namespace AES_Lacrima.Services.DuckStation;

public sealed record DuckStationCheatFileItem(string FilePath, string FileName);

public sealed class DuckStationCheatEntry
{
    public string Name { get; set; } = string.Empty;

    public string? Group { get; set; }

    public string Type { get; set; } = "Gameshark";

    public string Activation { get; set; } = "EndFrame";

    public string? Description { get; set; }

    public string? Author { get; set; }

    public string Body { get; set; } = string.Empty;

    public List<DuckStationCheatOption>? Options { get; set; }

    public DuckStationCheatOptionRange? OptionRange { get; set; }

    public bool IsManual => string.Equals(Activation, "Manual", System.StringComparison.OrdinalIgnoreCase);
}

public sealed class DuckStationCheatOption
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class DuckStationCheatOptionRange
{
    public int Min { get; set; }
    public int Max { get; set; }
}

public sealed class DuckStationChtDocument
{
    public string Serial { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public List<DuckStationCheatEntry> Entries { get; set; } = [];
}
