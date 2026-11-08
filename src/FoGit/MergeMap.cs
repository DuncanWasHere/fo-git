using System.Text.Json;

namespace FoGit;

// FormIDs assigned to records added by a plugin last time it was merged
public static class MergeMap {
    private static string PathFor(Repo r, string pluginFile) => Path.Combine(r.MergeMapDir, pluginFile + ".json");

    public static Dictionary<uint, uint> Load(Repo r, string pluginFile) {
        var p = PathFor(r, pluginFile);
        if (!File.Exists(p)) {
            return new();
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(p), Json.Options) ?? new();
        return raw.ToDictionary(kv => Convert.ToUInt32(kv.Key, 16), kv => Convert.ToUInt32(kv.Value, 16));
    }

    public static void Save(Repo r, string pluginFile, Dictionary<uint, uint> map) {
        Directory.CreateDirectory(r.MergeMapDir);
        var raw = map.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString("X6"), kv => kv.Value.ToString("X6"));
        File.WriteAllText(PathFor(r, pluginFile), JsonSerializer.Serialize(raw, Json.Options));
    }

    public static void ApplyRenumber(Repo r, Dictionary<uint, uint> oldToNew) {
        if (oldToNew.Count == 0 || !Directory.Exists(r.MergeMapDir)) {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(r.MergeMapDir, "*.json")) {
            var name = Path.GetFileNameWithoutExtension(file);
            var map = Load(r, name);
            bool changed = false;
            foreach (var k in map.Keys.ToList()) {
                if (oldToNew.TryGetValue(map[k], out var n)) {
                    map[k] = n;
                    changed = true;
                }
            }

            if (changed) {
                Save(r, name, map);
            }
        }
    }
}
