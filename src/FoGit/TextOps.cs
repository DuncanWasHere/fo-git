using System.Text.RegularExpressions;

namespace FoGit;

public static partial class TextOps {
    [GeneratedRegex(@"^\s*(?:- )?FormKey: ([0-9A-F]{6}:[^\s]+)\s*$")]
    public static partial Regex DefRegex();

    [GeneratedRegex(@"\b([0-9A-F]{6}):([^\s:'""\]\},]+\.es[mpl])\b", RegexOptions.IgnoreCase)]
    public static partial Regex RefRegex();

    [GeneratedRegex(@"^(<<<<<<<|>>>>>>>|=======$|\|\|\|\|\|\|\|)")]
    private static partial Regex ConflictMarker();

    public readonly record struct Location(string File, int Line);

    public sealed class ScanResult {
        public readonly Dictionary<string, List<Location>> Defs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Location> Refs = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Location> Markers = new();
    }

    public static ScanResult Scan(string spriggitDir) {
        var res = new ScanResult();
        var root = Path.GetFullPath(spriggitDir);
        foreach (var file in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)) {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            int lineNo = 0;
            foreach (var line in File.ReadLines(file)) {
                lineNo++;
                if (line.Length > 0 && line[0] is '<' or '>' or '=' or '|' && ConflictMarker().IsMatch(line)) {
                    res.Markers.Add(new Location(rel, lineNo));
                    continue;
                }
                var d = DefRegex().Match(line);
                if (d.Success) {
                    var fk = d.Groups[1].Value;
                    if (!res.Defs.TryGetValue(fk, out var list)) {
                        res.Defs[fk] = list = new();
                    }

                    list.Add(new Location(rel, lineNo));
                    continue;
                }
                foreach (Match m in RefRegex().Matches(line)) {
                    res.Refs.TryAdd(m.Value, new Location(rel, lineNo));
                }
            }
        }
        return res;
    }

    public sealed class TextReport {
        public readonly List<string> Errors = new();
        public readonly List<string> Warnings = new();
        public int Records;
        public ScanResult Scan = null!;
    }

    public static TextReport Validate(string spriggitDir, RepoConfig cfg, HashSet<string> known) {
        var rep = new TextReport { Scan = Scan(spriggitDir) };
        var scan = rep.Scan;
        rep.Records = scan.Defs.Count;
        foreach (var m in scan.Markers) {
            rep.Errors.Add($"Merge conflict marker left in {m.File} line {m.Line}.");
        }

        foreach (var (fk, locs) in scan.Defs.Where(x => x.Value.Count > 1)) {
            rep.Errors.Add($"FormID collision. {fk} is used {locs.Count} times, in {string.Join(" and ", locs.Take(3).Select(l => l.File))}.");
        }

        var masters = cfg.Masters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (fk, where) in scan.Refs) {
            var mod = fk[7..];
            if (mod.Equals(cfg.Master, StringComparison.OrdinalIgnoreCase)) {
                if (!scan.Defs.ContainsKey(fk)) {
                    Add(rep, known, $"Dangling reference. Form {fk} doesn't exist, referenced in {where.File}.");
                }
            } else if (!masters.Contains(mod)) {
                Add(rep, known, $"{where.File} references form {fk}, but {mod} isn't a master of {cfg.Master}.");
            }
        }
        return rep;
    }

    private static void Add(TextReport rep, HashSet<string> known, string warning) {
        if (!known.Contains(IssueKey(warning))) {
            rep.Warnings.Add(warning);
        }
    }

    public static string IssueKey(string message) {
        var m = Regex.Match(message, @"Dangling reference[.:] (?:Form )?([0-9A-F]{6}:\S+)");
        if (m.Success) {
            return "Dangling reference " + m.Groups[1].Value.TrimEnd(',', '.');
        }

        m = Regex.Match(message, @"Dangling reference\. ([0-9A-F]{6}:\S+?) \(");
        if (m.Success) {
            return "Dangling reference " + m.Groups[1].Value;
        }

        return message;
    }

    public static HashSet<string> LoadKnownIssues(string path) =>
        File.Exists(path)
            ? File.ReadLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(IssueKey).ToHashSet()
            : new HashSet<string>();

    public static uint IdOf(string formKey) => Convert.ToUInt32(formKey[..6], 16);
}
