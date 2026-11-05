using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoGit;

public sealed class RepoConfig {
    public string Master { get; set; } = "";

    public string SpriggitFolder { get; set; } = "";

    public string SpriggitPackage { get; set; } = "Spriggit.Yaml";

    public string SpriggitVersion { get; set; } = "0.41.0";

    public List<string> Masters { get; set; } = new() {
        "FalloutNV.esm", "DeadMoney.esm", "HonestHearts.esm", "OldWorldBlues.esm",
        "LonesomeRoad.esm", "GunRunnersArsenal.esm",
    };

    public string PluginFolder { get; set; } = "../../overwrite";

    public string KnownIssuesFile { get; set; } = "Plugins/known-issues.txt";

    public static RepoConfig Load(string path) {
        var cfg = JsonSerializer.Deserialize<RepoConfig>(File.ReadAllText(path), Json.Options)
                  ?? throw new UserError($"Could not read {path}");
        if (string.IsNullOrWhiteSpace(cfg.Master)) {
            throw new UserError($"{path} is missing \"master\". Add the master plugin file name, like \"master\": \"MyMod.esm\".");
        }

        if (string.IsNullOrWhiteSpace(cfg.SpriggitFolder)) {
            cfg.SpriggitFolder = "Plugins/" + Path.GetFileNameWithoutExtension(cfg.Master);
        }

        return cfg;
    }
}

public sealed class LocalConfig {
    public string? PluginFolder { get; set; }

    public List<string> MasterSearchPaths { get; set; } = new();

    public static LocalConfig Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<LocalConfig>(File.ReadAllText(path), Json.Options) ?? new LocalConfig()
            : new LocalConfig();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
}

public static class Json {
    public static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class UserError(string message) : Exception(message);
