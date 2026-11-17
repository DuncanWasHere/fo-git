using System.Text.RegularExpressions;

namespace FoGit.Text;

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
        public readonly ScriptVars Vars = new();
    }

    public static ScanResult Scan(string spriggitDir) {
        var result = new ScanResult();
        var root = Path.GetFullPath(spriggitDir);
        foreach (var file in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories)) {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            result.Vars.Read(rel, lines);
            int lineNo = 0;
            foreach (var line in lines) {
                lineNo++;
                if (line.Length > 0 && line[0] is '<' or '>' or '=' or '|' && ConflictMarker().IsMatch(line)) {
                    result.Markers.Add(new Location(rel, lineNo));
                    continue;
                }
                var d = DefRegex().Match(line);
                if (d.Success) {
                    var fk = d.Groups[1].Value;
                    if (!result.Defs.TryGetValue(fk, out var list)) {
                        result.Defs[fk] = list = new();
                    }

                    list.Add(new Location(rel, lineNo));
                    continue;
                }
                foreach (Match m in RefRegex().Matches(line)) {
                    result.Refs.TryAdd(m.Value, new Location(rel, lineNo));
                }
            }
        }
        return result;
    }

    public sealed class TextReport {
        public readonly List<string> Errors = new();
        public readonly List<string> Warnings = new();
        public int Records;
        public ScanResult Scan = null!;
    }

    public static TextReport Validate(string spriggitDir, RepoConfig cfg) {
        var report = new TextReport { Scan = Scan(spriggitDir) };
        var scan = report.Scan;
        report.Records = scan.Defs.Count;
        foreach (var m in scan.Markers) {
            report.Errors.Add($"Merge conflict marker left in {m.File} line {m.Line}.");
        }

        foreach (var (fk, locs) in scan.Defs.Where(x => x.Value.Count > 1)) {
            report.Errors.Add($"FormID collision. {fk} is used {locs.Count} times, in {string.Join(" and ", locs.Take(3).Select(l => l.File))}.");
        }

        var masters = cfg.Masters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (fk, where) in scan.Refs) {
            var mod = fk[7..];
            if (mod.Equals(cfg.Master, StringComparison.OrdinalIgnoreCase)) {
                if (!scan.Defs.ContainsKey(fk)) {
                    report.Warnings.Add($"Dangling reference. Form {fk} doesn't exist, referenced in {where.File}.");
                }
            } else if (!masters.Contains(mod)) {
                report.Warnings.Add($"{where.File} references form {fk}, but {mod} isn't a master of {cfg.Master}.");
            }
        }
        ValidateConditions(report);
        return report;
    }

    // Condition parameters aren't form links
    private static void ValidateConditions(TextReport report) {
        var vars = report.Scan.Vars;
        foreach (var p in vars.FormParams) {
            var fk = vars.Target(p.Raw);
            if (fk == null) {
                report.Warnings.Add($"{p.File} line {p.Line + 1} has a condition with the invalid FormID {p.Raw:X8}.");
            } else if (fk.EndsWith(":" + vars.Self.FileName, StringComparison.OrdinalIgnoreCase) && !report.Scan.Defs.ContainsKey(fk)) {
                report.Warnings.Add($"Dangling reference. Form {fk} doesn't exist, referenced by a condition in {p.File}.");
            }
        }

        foreach (var c in vars.VarConditions) {
            var script = vars.ScriptFor(vars.Target(c.First));
            if (script != null && vars.Vars.TryGetValue(script, out var names) && !names.ContainsKey(c.Second)) {
                report.Warnings.Add($"{c.File} has a condition referencing variable {c.Second} of {vars.Describe(script)}, which doesn't exist.");
            }
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
    
    public static uint IdOf(string formKey) => Convert.ToUInt32(formKey[..6], 16);
}
