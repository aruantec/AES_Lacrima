using System;
using System.Collections.Generic;

namespace AES_Lacrima.Services.Dolphin;

public enum DolphinGameIniEntryKind
{
    OnFrame,
    ActionReplay,
    Gecko
}

public sealed class DolphinGameIniEntry
{
    public required DolphinGameIniEntryKind Kind { get; init; }

    public required string Name { get; init; }

    public required List<string> Lines { get; init; }

    public bool Enabled { get; set; }

    public bool DefaultEnabled { get; set; }

    public bool UserDefined { get; set; }
}

public sealed class DolphinGameSettingsDocument
{
    public required string GameId { get; init; }

    public required List<DolphinGameIniEntry> Entries { get; init; }
}
