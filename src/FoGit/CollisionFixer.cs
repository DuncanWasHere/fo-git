using System.Text.RegularExpressions;

namespace FoGit;

// Fixes duplicate FormIDs after a merge. New records are renumbered.
public static class CollisionFixer {
    public sealed class Report {
        public readonly List<string> Renumbered = new();
        public readonly List<string> Warnings = new();
        public readonly Dictionary<uint, uint> OldToNew = new();
    }

    public static Report Run(Repo r) {
        var report = new Report();
        var parents = r.HeadParents();
        if (parents.Length < 2) {
            return report;
        }

        string current = parents[0], incoming = parents[1];
        var (mbCode, mbOut, _) = r.GitRaw("merge-base", current, incoming);
        if (mbCode != 0) {
            return report;
        }

        var mergeBase = mbOut.Trim();

        var master = r.Config.Master;
        var scan = TextOps.Scan(r.SpriggitDir);
        var collisions = scan.Defs.Where(x => x.Value.Count > 1 && x.Key.EndsWith(":" + master, StringComparison.OrdinalIgnoreCase)).ToList();
        if (collisions.Count == 0) {
            return report;
        }

        var folder = r.Config.SpriggitFolder.TrimEnd('/');
        var currentChanged = ChangedFiles(r, mergeBase, current, folder);
        var incomingChanged = ChangedFiles(r, mergeBase, incoming, folder);
        using var blobs = new GitBlobReader(r.Root);
        var currentDefs = DefsIn(blobs, current, currentChanged, folder);
        var baseDefs = DefsIn(blobs, mergeBase, currentChanged.Union(incomingChanged).ToList(), folder);
        var addedOnCurrent = currentDefs.Keys.Where(k => !baseDefs.ContainsKey(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        uint nextId = scan.Defs.Keys.Where(k => k.EndsWith(":" + master, StringComparison.OrdinalIgnoreCase))
            .Select(TextOps.IdOf).DefaultIfEmpty(PluginOps.FirstFreeId - 1).Max() + 1;
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // old path, new path

        var replaced = new List<(string from, string to)>();
        var selfIndex = (uint)scan.Vars.Masters.Count;
        string ParamOf(string formKey) => "Parameter: " + (int)((selfIndex << 24) | TextOps.IdOf(formKey));
        bool IsParam(string line, string param) => line.EndsWith(param, StringComparison.Ordinal) && line.TrimStart(' ', '-') is var t && (t.StartsWith("First") || t.StartsWith("Second"));
        string WithReplacements(string text) {
            foreach (var (f, t) in replaced) {
                text = text.Replace(f, t, StringComparison.OrdinalIgnoreCase);
            }

            return text;
        }

        foreach (var (fk, locs) in collisions) {
            var where = string.Join(" and ", locs.Select(l => l.File).Distinct());
            if (!addedOnCurrent.Contains(fk)) {
                report.Warnings.Add($"FormID {fk} is used by more than one form ({where}) on the main branch. This should never happen unless someone forgot to set up the git hooks properly!.");
                continue;
            }
            var defFiles = currentDefs[fk];
            var candidates = locs.Where(l => defFiles.Contains(l.File)).ToList();
            if (candidates.Count > 1) {
                // Both copies in the same file (two new refs in one cell). Use the one that matches this branch
                var curText = blobs.Read(current, folder + "/" + candidates[0].File);
                var curLines = curText == null ? null : YamlMerge.SplitText(WithReplacements(curText));
                var curBlock = curLines == null ? null : BlockAt(curLines, FindDef(curLines, fk));
                var lines = File.ReadAllLines(Path.Combine(r.SpriggitDir, Repo.Norm(ApplyRenames(candidates[0].File, renames)))).ToList();
                var match = candidates.Where(p => curBlock != null && BlockAt(lines, p.Line - 1).SequenceEqual(curBlock)).ToList();
                // Otherwise take the first one, this copy is written first
                candidates = match.Count == 1 ? match : candidates.Take(1).ToList();
            }
            if (candidates.Count == 0) {
                report.Warnings.Add($"FormID {fk} is used by more than one form ({where}), but the copy from this branch couldn't be found. Manually renumber the form.");
                continue;
            }
            var loc = candidates[0];
            var newId = nextId++;
            var newFk = $"{newId:X6}:{master}";
            string oldParam = ParamOf(fk), newParam = ParamOf(newFk);
            bool Mentions(string l) => l.Contains(fk, StringComparison.OrdinalIgnoreCase) || IsParam(l, oldParam);
            var oldNamedPath = NamedPath(loc.File);
            var newPath = RenumberDefinition(r, loc with { File = ApplyRenames(loc.File, renames) }, fk, newFk);
            if (newPath != null) {
                renames[oldNamedPath] = newPath;
            }

            report.OldToNew[TextOps.IdOf(fk)] = newId;
            report.Renumbered.Add($"{fk} in {loc.File} is now {newFk}");

            foreach (var rel in currentChanged) {
                var curRel = ApplyRenames(rel, renames);
                var path = Path.Combine(r.SpriggitDir, Repo.Norm(curRel));
                if (!File.Exists(path)) {
                    continue;
                }

                var lines = File.ReadAllLines(path).ToList();
                if (!lines.Any(Mentions)) {
                    continue;
                }

                var curText = blobs.Read(current, folder + "/" + rel);
                var curLines = curText == null ? new List<string>() : YamlMerge.SplitText(WithReplacements(curText));
                var incText = blobs.Read(incoming, folder + "/" + rel);
                var incLines = incText == null ? new List<string>() : YamlMerge.SplitText(incText);
                bool changed = false;
                for (int i = 0; i < lines.Count; i++) {
                    var l = lines[i];
                    if (!Mentions(l) || TextOps.DefRegex().IsMatch(l)) {
                        continue;
                    }

                    var (key, block) = UnitAt(lines, i);
                    bool inRenumbered = key.Equals("child " + newFk, StringComparison.OrdinalIgnoreCase);
                    var curUnit = inRenumbered ? null : FindUnit(curLines, key);
                    var incUnit = inRenumbered ? null : FindUnit(incLines, key);
                    bool fromCurrent;
                    if (inRenumbered) {
                        fromCurrent = true;
                    } else if (curUnit != null && block.SequenceEqual(curUnit) && (incUnit == null || !block.SequenceEqual(incUnit))) {
                        fromCurrent = true;
                    } else if (incUnit != null && block.SequenceEqual(incUnit) && (curUnit == null || !block.SequenceEqual(curUnit))) {
                        fromCurrent = false;
                    } else {
                        // Record has changes from both branches, check the line by itself
                        bool inCur = curUnit?.Contains(l) == true, inInc = incUnit?.Contains(l) == true;
                        if (inCur == inInc) {
                            report.Warnings.Add($"{curRel} line {i + 1} references {fk}, and it's unclear if it should reference {newFk} instead. Manual intervention required.");
                            continue;
                        }
                        fromCurrent = inCur;
                    }
                    if (!fromCurrent) {
                        continue;
                    }

                    lines[i] = IsParam(l, oldParam) ? l[..^oldParam.Length] + newParam : l.Replace(fk, newFk, StringComparison.OrdinalIgnoreCase);
                    changed = true;
                }
                if (changed) {
                    File.WriteAllText(path, string.Join("\n", lines) + "\n");
                }
            }
            replaced.Add((fk, newFk));
            replaced.Add((oldParam + "\n", newParam + "\n"));
        }
        MergeMap.ApplyRenumber(r, report.OldToNew);
        Repo.DeleteEmptyFolders(r.SpriggitDir);
        return report;
    }

    public static List<string> ChangedFiles(Repo r, string from, string to, string folder) {
        var output = r.Git("-c", "core.quotepath=off", "diff", "--name-only", "--no-renames", from, to, "--", folder);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[(folder.Length + 1)..]).ToList();
    }

    private static Dictionary<string, HashSet<string>> DefsIn(GitBlobReader blobs, string commit, List<string> files, string folder) {
        var defs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) {
            var text = blobs.Read(commit, folder + "/" + f);
            if (text == null) {
                continue;
            }

            foreach (var line in text.Split('\n')) {
                var m = TextOps.DefRegex().Match(line);
                if (!m.Success) {
                    continue;
                }

                if (!defs.TryGetValue(m.Groups[1].Value, out var set)) {
                    defs[m.Groups[1].Value] = set = new(StringComparer.OrdinalIgnoreCase);
                }

                set.Add(f);
            }
        }
        return defs;
    }

    private static int FindDef(List<string> lines, string fk) =>
        lines.FindIndex(l => TextOps.DefRegex().Match(l) is { Success: true } m && m.Groups[1].Value.Equals(fk, StringComparison.OrdinalIgnoreCase));

    private static List<string> BlockAt(List<string> lines, int defIndex) {
        if (defIndex < 0) {
            return new();
        }

        if (lines[defIndex].StartsWith("FormKey:")) {
            return lines;
        }

        var (start, end) = BlockRange(lines, defIndex);
        return lines.GetRange(start, end - start);
    }

    private static (int start, int end) BlockRange(List<string> lines, int defIndex) {
        static int Indent(string l) => l.Length - l.TrimStart().Length;
        var def = lines[defIndex];
        int itemCol = def.TrimStart().StartsWith("- ") ? Indent(def) : Indent(def) - 2;
        int start = defIndex;
        while (start > 0 && !(Indent(lines[start]) == itemCol && lines[start].TrimStart().StartsWith("- "))) {
            start--;
        }

        int end = start + 1;
        while (end < lines.Count && (lines[end].Trim().Length == 0 || Indent(lines[end]) > itemCol)) {
            end++;
        }

        return (start, end);
    }

    private static (string key, List<string> block) UnitAt(List<string> lines, int i) {
        for (int j = 0; j < lines.Count; j++) {
            var m = TextOps.DefRegex().Match(lines[j]);
            if (!m.Success || lines[j].StartsWith("FormKey:")) {
                continue;
            }

            var (start, end) = BlockRange(lines, j);
            if (i >= start && i < end) {
                return ("child " + m.Groups[1].Value, lines.GetRange(start, end - start));
            }
        }
        int k = i;
        while (k > 0 && (lines[k].Length == 0 || char.IsWhiteSpace(lines[k][0]) || lines[k].StartsWith("- "))) {
            k--;
        }

        int e = k + 1;
        while (e < lines.Count && (lines[e].Length == 0 || char.IsWhiteSpace(lines[e][0]) || lines[e].StartsWith("- "))) {
            e++;
        }

        var name = lines[k].Contains(':') ? lines[k][..lines[k].IndexOf(':')] : lines[k];
        return ("field " + name, lines.GetRange(k, e - k));
    }

    private static List<string>? FindUnit(List<string> lines, string key) {
        if (key.StartsWith("child ")) {
            var idx = FindDef(lines, key[6..]);
            return idx < 0 ? null : BlockAt(lines, idx);
        }
        var name = key[6..];
        var k = lines.FindIndex(l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith("- ") && (l == name || l.StartsWith(name + ":")));
        if (k < 0) {
            return null;
        }

        int e = k + 1;
        while (e < lines.Count && (lines[e].Length == 0 || char.IsWhiteSpace(lines[e][0]) || lines[e].StartsWith("- "))) {
            e++;
        }

        return lines.GetRange(k, e - k);
    }

    private static string NamedPath(string file) =>
        file.EndsWith("/RecordData.yaml", StringComparison.OrdinalIgnoreCase) ? file[..file.LastIndexOf('/')] : file;

    private static string ApplyRenames(string rel, Dictionary<string, string> renames) {
        foreach (var (from, to) in renames) {
            if (rel.Equals(from, StringComparison.OrdinalIgnoreCase)) {
                return to;
            }

            if (rel.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)) {
                return to + rel[from.Length..];
            }
        }
        return rel;
    }

    private static string? RenumberDefinition(Repo r, TextOps.Location loc, string fk, string newFk) {
        var path = Path.Combine(r.SpriggitDir, Repo.Norm(loc.File));
        var lines = File.ReadAllLines(path);
        lines[loc.Line - 1] = lines[loc.Line - 1].Replace(fk, newFk, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");

        bool topLevel = lines[loc.Line - 1].StartsWith("FormKey:");
        if (!topLevel) {
            return null;
        }

        var named = NamedPath(loc.File);
        var oldTag = fk.Replace(':', '_');
        var newTag = newFk.Replace(':', '_');
        var segs = named.Split('/');
        if (!segs[^1].Contains(oldTag, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        segs[^1] = Regex.Replace(segs[^1], Regex.Escape(oldTag), newTag, RegexOptions.IgnoreCase);
        // Interior cells go in Cells/<block>/<sub block>/. Block is the last decimal digit of the ID, sub block is the one before
        if (segs.Length == 4 && segs[0].Equals("Cells", StringComparison.OrdinalIgnoreCase)) {
            var id = TextOps.IdOf(newFk);
            var oldBlockDir = Path.Combine(r.SpriggitDir, segs[0], segs[1]);
            var oldSubDir = Path.Combine(oldBlockDir, segs[2]);
            segs[1] = (id % 10).ToString();
            segs[2] = (id / 10 % 10).ToString();
            CopyGroupFile(oldBlockDir, Path.Combine(r.SpriggitDir, segs[0], segs[1]));
            CopyGroupFile(oldSubDir, Path.Combine(r.SpriggitDir, segs[0], segs[1], segs[2]));
        }
        var newNamed = string.Join('/', segs);
        var from = Path.Combine(r.SpriggitDir, Repo.Norm(named));
        var to = Path.Combine(r.SpriggitDir, Repo.Norm(newNamed));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (Directory.Exists(from)) {
            Directory.Move(from, to);
        } else {
            File.Move(from, to);
        }

        return newNamed;
    }

    private static void CopyGroupFile(string fromDir, string toDir) {
        Directory.CreateDirectory(toDir);
        var src = Path.Combine(fromDir, "GroupRecordData.yaml");
        var dst = Path.Combine(toDir, "GroupRecordData.yaml");
        if (!File.Exists(dst) && File.Exists(src)) {
            File.Copy(src, dst);
        }
    }
}
