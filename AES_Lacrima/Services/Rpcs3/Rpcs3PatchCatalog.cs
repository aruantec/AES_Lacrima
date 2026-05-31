namespace AES_Lacrima.Services.Rpcs3;

/// <summary>
/// Which RPCS3 patch file / entry set the patches overlay is managing.
/// </summary>
public enum Rpcs3PatchCatalog
{
    /// <summary>Official RPCS3 compatibility patches (<c>patches/patch.yml</c>).</summary>
    Official,

    /// <summary>
    /// Artemis cheat patches shown in the cheats overlay. Reads
    /// <see cref="Rpcs3PatchesService.ImportedPatchFileName"/> (RPCS3-compatible) and legacy
    /// <c>artemis_cheats.yml</c> from older Lacrima installs.
    /// </summary>
    ArtemisCheats,

    /// <summary>User-imported and custom RPCS3 patches (<c>patches/imported_patch.yml</c>).</summary>
    Imported,
}
