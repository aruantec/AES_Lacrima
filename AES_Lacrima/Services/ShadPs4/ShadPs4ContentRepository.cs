using System.Collections.Generic;

namespace AES_Lacrima.Services.ShadPs4;

public sealed record ShadPs4ContentRepository(string Id, string DisplayName)
{
    public static ShadPs4ContentRepository GoldHen { get; } = new("GoldHEN", "GoldHEN");

    public static ShadPs4ContentRepository ShadPs4 { get; } = new("shadPS4", "shadPS4");

    public static IReadOnlyList<ShadPs4ContentRepository> All { get; } = [GoldHen, ShadPs4];

    public override string ToString() => DisplayName;
}
