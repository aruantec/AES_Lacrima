# JSON serialization (Native AOT)

AES_Lacrima is published with **Native AOT** (`PublishAot=true`). `System.Text.Json` reflection-based serialization is disabled in that configuration, so any type written to or read from disk must be registered in a **source-generated** `JsonSerializerContext`.

All contexts in this folder are `internal` to `AES_Lacrima`. Other projects use their own contexts (for example `SettingsJsonContext`, `AppUpdateJsonContext`, `BinaryMetadataJsonContext` in referenced assemblies).

## Rules for contributors

1. **Never** call `JsonSerializer.Serialize<T>(value)` or `Deserialize<T>(json)` without a `JsonTypeInfo` from a context.
2. Prefer the typed overload:
   ```csharp
   JsonSerializer.Serialize(document, XeniaCustomConfigJsonContext.Default.XeniaCustomConfigDocument);
   JsonSerializer.Deserialize(json, ShadPs4JsonContext.Default.ShadPs4CustomConfigDocument);
   ```
3. When adding a new persisted model, create or extend a `partial class *JsonContext : JsonSerializerContext` and add `[JsonSerializable(typeof(YourType))]` for the type and any nested collections it needs.
4. UI bindings should use **compiled bindings** in AXAML (`x:DataType` on the view/window). Do not use `new Binding("PropertyName")` in code-behind.
5. Settings types saved via `SettingsBase.WriteObjectSetting<T>` must also be listed on `SettingsJsonContext`.

## Context inventory

| File | Purpose | Consumers |
|------|---------|-----------|
| `XeniaCustomConfigJsonContext` | Per-game Xenia override JSON (`custom_configs/{TitleId}.json`) | `XeniaCustomConfigService` |
| `ShadPs4JsonContext` | shadPS4 custom config, cheats, patch index, `files.json` maps | `ShadPs4CustomConfigService`, `ShadPs4CheatsService`, `ShadPs4PatchesService`, `ShadPs4ContentDownloadService` |
| `RomTitleDatabaseJsonContext` | Embedded `Database/*.json` title lists (`serial` / `title`, Xbox `titleid`) | `MetadataService`, `GenericAlbumNormalizer`, `Xbox360MetadataService` |
| `EmulatorUpdateJsonContext` | GitHub release-list HTTP cache for updater services | `EmulatorReleaseCachePersistence` |
| `FlycastUpdateJsonContext` | Flycast updater cache (`Payload` holds JSON or nightly XML) | `FlycastReleaseCachePersistence` |

## Emulator update caches

Most updater services share `EmulatorReleaseCache` (repository, ETag, releases JSON, timestamp) via `EmulatorReleaseCachePersistence`.

**Flycast** uses `FlycastReleaseCache` because the cached `Payload` field may contain either GitHub release JSON or nightly S3 listing XML.

**RPCS3** and **Dolphin** caches use `JsonNode` / `JsonObject` manual parsing instead of these contexts (also AOT-safe).

## ROM title databases

Files under `AES_Lacrima/Database/` (for example `psx.json`, `ps2.json`, `x360.json`) use this shape:

```json
[
  { "serial": "SLUS-12345", "title": "Example Game" }
]
```

Xbox 360 entries use `titleid` instead of `serial`. Loading is implemented in `EmbeddedDatabaseResource` and deserialized through `RomTitleDatabaseJsonContext`.

## Related documentation

- [BUILDING.md](../../BUILDING.md) — compile, test, publish, and Native AOT builds
- [Settings/SettingsJsonContext.cs](../Settings/SettingsJsonContext.cs) — persisted UI/settings graph
- [rd.xml](../rd.xml) — trimmer directives for Autofac, log4net, TagLib#

## Verify an AOT build locally

```bash
dotnet build AES_Lacrima/AES_Lacrima.csproj -c Release /p:PublishAot=true
```

A clean tree should report **0** IL2026/IL3050 warnings from reflection JSON or reflection bindings in `AES_Lacrima`.
