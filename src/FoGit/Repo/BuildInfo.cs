namespace FoGit.Repo;

/// <summary>
/// Build metadata linking a version of the Spriggit text tree with a synced version of the ESM file, by checksums.
/// </summary>
/// <param name="TextTreeHash">Checksum of the Spriggit text tree</param>
/// <param name="EsmHash">Checksum of the ESM file</param>
/// <param name="EsmSize">Size of the ESM file</param>
/// <param name="EsmWriteTime">Write time of the ESM file</param>
/// <param name="BuiltAt">Time of build, when this info was recorded (may differ from ESM write time if only the text tree was regenerated)</param>
public sealed record BuildInfo(string TextTreeHash, string EsmHash, long EsmSize, DateTime EsmWriteTime, DateTime BuiltAt);
