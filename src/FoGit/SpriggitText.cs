using System.Text;
using System.Text.RegularExpressions;

namespace FoGit;

public static partial class SpriggitText {
    [GeneratedRegex(@"^(.*) - ([0-9A-F]{6}_.+)$")]
    private static partial Regex NamedSegment();

    public sealed class ApplyReport {
        public int Added, Changed, Removed, Moved;
        public readonly List<string> Notes = new();
        public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
    }

    // "Key: >", "Key: |+", "Key: -", etc.
    [GeneratedRegex(@"^( *)(- )?[^ :#][^:]*: [>|][+-]?$")]
    private static partial Regex BlockScalarHeader();

    // Numeric value
    [GeneratedRegex(@"^[-+0-9., Ee]+$")]
    private static partial Regex NumberList();

    [GeneratedRegex(@"(?<![\d.])-0(?![\d.Ee])")]
    private static partial Regex NegativeZero();

    public static byte[] Normalize(byte[] content) {
        var text = Encoding.UTF8.GetString(content);
        if (!text.Contains("-0") && !text.Contains(": >\n")) {
            return content;
        }

        var lines = text.Split('\n');
        int blockIndent = -1;
        for (int i = 0; i < lines.Length; i++) {
            var l = lines[i];
            int indent = l.Length - l.TrimStart(' ').Length;
            // Text inside a block scalar stays as is
            if (blockIndent >= 0) {
                if (l.Length == 0 || indent > blockIndent) {
                    continue;
                }
                blockIndent = -1;
            }

            var header = BlockScalarHeader().Match(l);
            if (header.Success) {
                blockIndent = indent + header.Groups[2].Length;
                if (l.EndsWith(": >")) {
                    lines[i] = l + "+";
                }
                continue;
            }

            var colon = l.IndexOf(": ", StringComparison.Ordinal);
            var valueStart = colon >= 0 ? colon + 2 : l.TrimStart().StartsWith("- ") ? indent + 2 : -1;
            if (valueStart < 0 || valueStart >= l.Length || !NumberList().IsMatch(l[valueStart..])) {
                continue;
            }

            lines[i] = l[..valueStart] + NegativeZero().Replace(l[valueStart..], "0");
        }
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    public static string Key(string rel) =>
        string.Join('/', rel.Split('/').Select(seg => {
            var m = NamedSegment().Match(seg);
            return m.Success ? m.Groups[2].Value : seg;
        })).ToLowerInvariant();

    public static ApplyReport Apply(Repo r, string newDir, bool baseIsCurrent) {
        var rep = new ApplyReport();
        var curDir = r.SpriggitDir;
        Dictionary<string, string> Files(string dir) => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                .GroupBy(Key).ToDictionary(g => g.Key, g => g.First())
            : new();
        var current = Files(curDir);
        var incoming = Files(newDir);
        var history = baseIsCurrent ? null : HistoryIndex.Build(r);
        using var blobs = baseIsCurrent ? null : new GitBlobReader(r.Root);

        var output = new Dictionary<string, (string? curRel, string newRel, byte[] content)>();
        var segmentNames = new Dictionary<string, string>(); // path prefix key, segment name
        var changed = new HashSet<string>();
        foreach (var (key, newRel) in incoming) {
            var newBytes = Repo.StripCr(File.ReadAllBytes(Path.Combine(newDir, Repo.Norm(newRel))));
            current.TryGetValue(key, out var curRel);
            byte[] content = newBytes;
            bool useNewName = true;
            if (curRel != null) {
                var curBytes = Repo.StripCr(File.ReadAllBytes(Path.Combine(curDir, Repo.Norm(curRel))));
                if (curBytes.AsSpan().SequenceEqual(newBytes)) {
                    content = curBytes;
                } else if (!baseIsCurrent) {
                    var cur = YamlMerge.SplitText(Encoding.UTF8.GetString(curBytes));
                    var inc = YamlMerge.SplitText(Encoding.UTF8.GetString(newBytes));
                    var hist = history!.Versions(key, blobs!, r.Config.SpriggitFolder)
                        .Select(YamlMerge.SplitText).Where(h => !h.SequenceEqual(cur)).ToList();
                    var opt = new YamlMerge.Options { Label = curRel };
                    var merged = YamlMerge.MergeWithHistory(hist, cur, inc, opt).Lines;
                    rep.Notes.AddRange(opt.Notes);
                    content = Encoding.UTF8.GetBytes(string.Join("\n", merged) + "\n");
                    // Keep the current file name unless the plugin changed the EditorID
                    useNewName = EditorIdLine(merged) == EditorIdLine(inc);
                }
                if (!content.AsSpan().SequenceEqual(curBytes)) {
                    rep.Changed++;
                    changed.Add(key);
                }
            } else {
                rep.Added++;
                changed.Add(key);
            }

            output[key] = (curRel, newRel, content);
            SetSegmentName(useNewName || curRel == null ? newRel : curRel, segmentNames);
        }

        // Second pass, write everything and delete old or removed files
        foreach (var (key, (curRel, newRel, content)) in output) {
            var finalRel = FinalPath(newRel, curRel, segmentNames);
            if (changed.Contains(key)) {
                rep.Files.Add(finalRel);
            }
            Repo.WriteIfChanged(Path.Combine(curDir, Repo.Norm(finalRel)), content);
            if (curRel != null && !string.Equals(curRel, finalRel, StringComparison.Ordinal)) {
                File.Delete(Path.Combine(curDir, Repo.Norm(curRel)));
                rep.Moved++;
            }
        }
        foreach (var (key, curRel) in current) {
            if (incoming.ContainsKey(key)) {
                continue;
            }

            File.Delete(Path.Combine(curDir, Repo.Norm(curRel)));
            rep.Removed++;
        }
        Repo.DeleteEmptyFolders(curDir);
        return rep;
    }

    private static string? EditorIdLine(List<string> lines) => lines.FirstOrDefault(l => l.StartsWith("EditorID:"));

    private static void SetSegmentName(string rel, Dictionary<string, string> names) {
        var segs = rel.Split('/');
        int idx = segs[^1].Equals("RecordData.yaml", StringComparison.OrdinalIgnoreCase) ? segs.Length - 2 : segs.Length - 1;
        if (idx < 0 || !NamedSegment().IsMatch(segs[idx])) {
            return;
        }

        names[Key(string.Join('/', segs.Take(idx + 1)))] = segs[idx];
    }

    private static string FinalPath(string newRel, string? curRel, Dictionary<string, string> names) {
        var segs = newRel.Split('/');
        var curSegs = curRel?.Split('/');
        var result = new string[segs.Length];
        for (int i = 0; i < segs.Length; i++) {
            var key = Key(string.Join('/', segs.Take(i + 1)));
            if (names.TryGetValue(key, out var chosen)) {
                result[i] = chosen;
            } else if (curSegs != null && curSegs.Length == segs.Length && Key(curSegs[i]) == Key(segs[i])) {
                result[i] = curSegs[i];
            } else {
                result[i] = segs[i];
            }
        }
        return string.Join('/', result);
    }

    private sealed class HistoryIndex {
        private const int MaxCommits = 400;
        private const int MaxVersionsPerFile = 30;
        private readonly Dictionary<string, List<(string commit, string path)>> _byKey = new();

        public static HistoryIndex Build(Repo r) {
            var idx = new HistoryIndex();
            var prefix = r.Config.SpriggitFolder.TrimEnd('/') + "/";
            var (code, output, _) = r.GitRaw("-c", "core.quotepath=off", "log", "--format=@%H", "--name-only", "--no-renames", "-n", MaxCommits.ToString(), "--", r.Config.SpriggitFolder);
            if (code != 0) {
                return idx;
            }

            string? commit = null;
            foreach (var raw in output.Split('\n')) {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith('@')) {
                    commit = line[1..];
                    continue;
                }
                if (commit == null || line.Length == 0 || !line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                var key = Key(line[prefix.Length..]);
                if (!idx._byKey.TryGetValue(key, out var list)) {
                    idx._byKey[key] = list = new();
                }

                if (list.Count < MaxVersionsPerFile) {
                    list.Add((commit, line));
                }
            }
            return idx;
        }

        public IEnumerable<string> Versions(string key, GitBlobReader blobs, string folder) {
            if (!_byKey.TryGetValue(key, out var list)) {
                yield break;
            }

            var seen = new HashSet<string>();
            foreach (var (commit, path) in list) {
                var text = blobs.Read(commit, path);
                if (text != null && seen.Add(text)) {
                    yield return text;
                }
            }
        }
    }
}
