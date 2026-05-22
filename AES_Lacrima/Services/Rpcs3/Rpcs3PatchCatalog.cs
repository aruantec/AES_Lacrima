namespace AES_Lacrima.Services.Rpcs3;

/// <summary>
/// Which RPCS3 patch file / entry set the patches overlay is managing.
/// </summary>
public enum Rpcs3PatchCatalog
{
    /// <summary>Official RPCS3 compatibility patches (<c>patches/patch.yml</c>).</summary>
    Official,

    /// <summary>Artemis cheat patches from chidreams/Artemis-Patch-Collection-RPCS3 (<c>patches/artemis_cheats.yml</c>).</summary>
    ArtemisCheats,
}
