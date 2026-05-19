using System;
using System.Collections.Generic;

namespace AES_Lacrima.Services.Xenia;

public sealed class XeniaCustomConfigDocument
{
    public Dictionary<string, Dictionary<string, string?>> Overrides { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public enum XeniaConfigValueKind
{
    Boolean,
    Integer,
    Float,
    String,
    Choice,
    GpuAdapterIndex
}

public sealed record XeniaConfigFieldDefinition(
    string Section,
    string Key,
    string Label,
    string? Description,
    XeniaConfigValueKind Kind,
    string[]? ChoiceLabels = null,
    string[]? ChoiceValues = null,
    int? IntMin = null,
    int? IntMax = null,
    double? FloatMin = null,
    double? FloatMax = null);
