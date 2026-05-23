using System.Collections.Generic;
using System.Text.Json.Serialization;
using AES_Lacrima.Services.Xenia;

namespace AES_Lacrima.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(XeniaCustomConfigDocument))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string?>>))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal partial class XeniaCustomConfigJsonContext : JsonSerializerContext;
