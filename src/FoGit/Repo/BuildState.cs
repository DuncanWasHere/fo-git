namespace FoGit.Repo;

/// <summary>
/// The fo-git build state of a local repo, in terms of the Spriggit text tree and ESM file versioning.
/// </summary>
public enum BuildState {
    /// <summary>
    /// Usually this applies to a fresh repo clone.
    /// The ESM can be built at any time as long as the Spriggit text tree has already been generated.
    /// </summary>
    EsmMissing,

    /// <summary>
    /// This should only apply to a new project, before the Spriggit text tree has been generated.
    /// </summary>
    EsmNotTracked,

    /// <summary>
    /// The Spriggit text tree and the ESM are in sync.
    /// This should always apply immediately after git operations and merging plugins.
    /// There may or may not be staged changes to the text, but the ESM was built from the current working tree.
    /// </summary>
    UpToDate,

    /// <summary>
    /// This should only apply when there are staged changes to the Spriggit text tree that were made by hand,
    /// since the ESM was last rebuilt, and the ESM has not been manually edited since.
    /// This won't block any git operations since the hooks rebuild the ESM anyways,
    /// but they do have to be discarded or the ESM has to be rebuilt before merging plugins.
    /// </summary>
    EsmOutOfDate,

    /// <summary>
    /// This applies whenever the current checksum of the ESM doesn't match the checksum of the last build.
    /// The changes in the ESM are assumed to be more current than the Spriggit text tree,
    /// and they should be applied by regenerating the text (or discarded by rebuilding from the text)
    /// before git operations or merging plugins, since the hooks rebuild the ESM immediately after.
    /// The text may have its own changes that are not in the ESM.
    /// In that case, they will be merged when the text is regenerated.
    /// </summary>
    TextOutOfDate,
}
