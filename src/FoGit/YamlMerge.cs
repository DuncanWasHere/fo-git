using System.Text.RegularExpressions;

namespace FoGit;

public static partial class YamlMerge {
    [GeneratedRegex(@"^(\s*)(- )?(FormKey|NavigationMesh): (\S+)\s*$")]
    private static partial Regex ChildKey();

    [GeneratedRegex(@"^\s*- [0-9A-F]{6}:\S+\s*$")]
    private static partial Regex FormKeyListItem();

    public sealed class Result {
        public readonly List<string> Lines = new();
        public int Conflicts;
    }

    public sealed class Options {
        // Override master records
        public bool Override;

        // Keep both if a new child has the same FormKey on both sides but different content
        public bool KeepIdCollisions;

        public string Label = "";

        public readonly List<string> Notes = new();
    }

    public static int RunDriver(string basePath, string currentPath, string incomingPath, string label) {
        var b = ReadLines(basePath);
        var cur = ReadLines(currentPath);
        var inc = ReadLines(incomingPath);
        var opt = new Options { KeepIdCollisions = true, Label = label };
        Result res;
        try {
            res = MergeFile(b, cur, inc, opt);
        } catch (Exception e) {
            Console.Error.WriteLine($"fo-git merge. Could not split {label} into fields ({e.Message}). Used a line merge instead.");
            res = TextMerge(b, cur, inc, opt);
        }
        File.WriteAllText(currentPath, string.Join("\n", res.Lines) + "\n");
        foreach (var n in opt.Notes) {
            Console.Error.WriteLine("fo-git merge. " + n);
        }

        if (res.Conflicts > 0) {
            Console.Error.WriteLine($"fo-git merge. {label} has {res.Conflicts} conflict(s) requiring manual intervention.");
            return 1;
        }
        return 0;
    }

    public static List<string> ReadLines(string path) =>
        SplitText(File.Exists(path) ? File.ReadAllText(path) : "");

    public static List<string> SplitText(string text) {
        text = text.Replace("\r\n", "\n");
        if (text.EndsWith('\n')) {
            text = text[..^1];
        }

        return text.Length == 0 ? new List<string>() : text.Split('\n').ToList();
    }

    private static List<(string key, List<string> lines)> SplitTopLevel(List<string> lines) {
        var entries = new List<(string, List<string>)>();
        foreach (var l in lines) {
            bool isKey = l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith("- ") && l != "-" && !l.StartsWith('#');
            if (isKey) {
                var colon = l.IndexOf(':');
                entries.Add((colon > 0 ? l[..colon] : l, new List<string> { l }));
            } else {
                if (entries.Count == 0) {
                    throw new FormatException("text before the first key");
                }

                entries[^1].Item2.Add(l);
            }
        }
        if (entries.Select(x => x.Item1).Distinct().Count() != entries.Count) {
            throw new FormatException("a top level key is used twice");
        }

        return entries;
    }

    private static (string header, List<(string fk, List<string> lines)> children)? SplitRecordList(List<string>? entry) {
        if (entry == null || entry.Count < 2 || !entry[0].TrimEnd().EndsWith(':')) {
            return null;
        }

        var items = new List<List<string>>();
        for (int i = 1; i < entry.Count; i++) {
            var l = entry[i];
            if (l.StartsWith("- ")) {
                items.Add(new List<string> { l });
            } else if (items.Count == 0) {
                return null;
            } else {
                items[^1].Add(l);
            }
        }
        var children = new List<(string, List<string>)>();
        foreach (var item in items) {
            string? fk = null;
            string? navmesh = null;
            foreach (var l in item) {
                var m = ChildKey().Match(l);
                if (!m.Success || m.Groups[1].Value.Length + m.Groups[2].Value.Length > 2) {
                    continue;
                }

                if (m.Groups[3].Value == "FormKey") {
                    fk = m.Groups[4].Value;
                    break;
                }
                navmesh ??= "NavigationMesh " + m.Groups[4].Value;
            }
            fk ??= navmesh;
            if (fk == null) {
                return null;
            }

            children.Add((fk, item));
        }
        if (children.Select(x => x.Item1).Distinct().Count() != children.Count) {
            return null;
        }

        return (entry[0], children);
    }

    // Split fields of a nested block
    private static List<string>? Body(List<string>? entry) {
        if (entry == null || entry.Count < 2 || !entry[0].TrimEnd().EndsWith(':') || entry[1].TrimStart().StartsWith("- ")) {
            return null;
        }

        var body = new List<string>();
        for (int i = 1; i < entry.Count; i++) {
            var l = entry[i];
            if (l.Length == 0) {
                body.Add(l);
            } else if (l.StartsWith("  ")) {
                body.Add(l[2..]);
            } else {
                return null;
            }
        }
        return body;
    }

    private static void AppendBody(Result into, string header, Result body) {
        into.Lines.Add(header);
        foreach (var l in body.Lines) {
            bool marker = l.StartsWith("<<<<<<< ") || l.StartsWith("||||||| ") || l == "=======" || l.StartsWith(">>>>>>> ");
            into.Lines.Add(marker || l.Length == 0 ? l : "  " + l);
        }
        into.Conflicts += body.Conflicts;
    }

    private static bool Same(List<string>? a, List<string>? b) =>
        a == null ? b == null : b != null && a.SequenceEqual(b);

    // ------------------------------------------------------------------ Merging git branches

    public static Result MergeFile(List<string> b, List<string> cur, List<string> inc, Options opt) {
        var res = new Result();
        if (Same(cur, inc)) {
            res.Lines.AddRange(cur);
            return res;
        }
        if (Same(cur, b)) {
            res.Lines.AddRange(inc);
            return res;
        }
        if (Same(inc, b)) {
            res.Lines.AddRange(cur);
            return res;
        }

        var baseFields = SplitTopLevel(b).ToDictionary(x => x.key, x => x.lines);
        var curList = SplitTopLevel(cur);
        var incList = SplitTopLevel(inc);
        var curFields = curList.ToDictionary(x => x.key, x => x.lines);
        var incFields = incList.ToDictionary(x => x.key, x => x.lines);
        foreach (var key in MergeOrder(curList.Select(x => x.key).ToList(), incList.Select(x => x.key).ToList())) {
            baseFields.TryGetValue(key, out var bv);
            curFields.TryGetValue(key, out var cv);
            incFields.TryGetValue(key, out var iv);
            MergeUnit(bv, cv, iv, res, opt, key, (bb, cc, ii, rr) => MergeEntry(bb, cc, ii, rr, opt));
        }
        return res;
    }

    private static void MergeUnit(List<string>? b, List<string>? cur, List<string>? inc, Result res, Options opt, string unit,
        Action<List<string>, List<string>, List<string>, Result> mergeBoth) {
        if (Same(cur, inc)) {
            if (cur != null) {
                res.Lines.AddRange(cur);
            }
            return;
        }
        if (Same(cur, b)) {
            if (inc != null) {
                res.Lines.AddRange(inc);
            }
            return;
        }
        if (Same(inc, b)) {
            if (cur != null) {
                res.Lines.AddRange(cur);
            }
            return;
        }
        if (cur != null && inc != null) {
            if (b == null && opt.KeepIdCollisions && unit.Contains(':') && !unit.StartsWith("NavigationMesh ")) {
                // Same new FormKey on both sides. Keep both, one gets renumbered after the merge
                res.Lines.AddRange(cur);
                res.Lines.AddRange(inc);
                opt.Notes.Add($"{opt.Label} has a new record {unit} on both sides. Kept both. One gets a new FormID after the merge.");
                return;
            }
            mergeBoth(b ?? new List<string>(), cur, inc, res);
            return;
        }
        // Deleted on one side, edited on the other
        if (opt.Override) {
            if (inc != null) {
                res.Lines.AddRange(inc);
            }

            opt.Notes.Add($"{opt.Label} {unit} was {(inc == null ? "deleted in the plugin but changed in the master" : "edited in the plugin but deleted in the master")}. Used the plugin version.");
            return;
        }
        WriteConflict(cur ?? new(), b ?? new(), inc ?? new(), res);
    }

    private static void MergeEntry(List<string> b, List<string> cur, List<string> inc, Result res, Options opt) {
        var curList = SplitRecordList(cur);
        var incList = SplitRecordList(inc);
        var baseList = b.Count == 0 ? (cur[0], new List<(string, List<string>)>()) : SplitRecordList(b);
        if (curList != null && incList != null && baseList != null && curList.Value.header == incList.Value.header) {
            res.Lines.Add(curList.Value.header);
            var baseChildren = baseList.Value.Item2.ToDictionary(x => x.Item1, x => x.Item2);
            var curChildren = curList.Value.children.ToDictionary(x => x.fk, x => x.lines);
            var incChildren = incList.Value.children.ToDictionary(x => x.fk, x => x.lines);
            foreach (var fk in MergeOrder(curList.Value.children.Select(x => x.fk).ToList(), incList.Value.children.Select(x => x.fk).ToList())) {
                baseChildren.TryGetValue(fk, out var bv);
                curChildren.TryGetValue(fk, out var cv);
                incChildren.TryGetValue(fk, out var iv);
                MergeUnit(bv, cv, iv, res, opt, fk, (bb, cc, ii, rr) => Append(rr, TextMerge(bb, cc, ii, opt)));
            }
            return;
        }

        var curBody = Body(cur);
        var incBody = Body(inc);
        var baseBody = b.Count == 0 ? new List<string>() : Body(b);
        if (curBody != null && incBody != null && baseBody != null && cur[0] == inc[0]) {
            try {
                AppendBody(res, cur[0], MergeFile(baseBody, curBody, incBody, opt));
                return;
            } catch (FormatException) {
                // Not a plain mapping, fall back to line merge
            }
        }
        Append(res, TextMerge(b, cur, inc, opt));
    }

    private static void Append(Result into, Result from) {
        into.Lines.AddRange(from.Lines);
        into.Conflicts += from.Conflicts;
    }

    private static List<string> MergeOrder(List<string> current, List<string> incoming) {
        var result = new List<string>(current);
        var present = current.ToHashSet();
        string? prev = null;
        foreach (var k in incoming) {
            if (!present.Contains(k)) {
                var idx = prev == null ? 0 : result.IndexOf(prev) + 1;
                if (prev != null && idx == 0) {
                    idx = result.Count;
                }

                result.Insert(idx, k);
                present.Add(k);
            }
            prev = k;
        }
        return result;
    }

    private static void WriteConflict(List<string> cur, List<string> b, List<string> inc, Result res) {
        res.Lines.Add("<<<<<<< current");
        res.Lines.AddRange(cur);
        res.Lines.Add("||||||| base");
        res.Lines.AddRange(b);
        res.Lines.Add("=======");
        res.Lines.AddRange(inc);
        res.Lines.Add(">>>>>>> incoming");
        res.Conflicts++;
    }

    // ------------------------------------------------------------------ Merging plugins

    public static Result MergeWithHistory(List<List<string>> history, List<string> cur, List<string> inc, Options opt) {
        opt.Override = true;
        var res = new Result();
        if (Same(cur, inc)) {
            res.Lines.AddRange(cur);
            return res;
        }
        List<Dictionary<string, List<string>>> histFields;
        List<(string key, List<string> lines)> curList, incList;
        try {
            histFields = history.Select(h => SplitTopLevel(h).ToDictionary(x => x.key, x => x.lines)).ToList();
            curList = SplitTopLevel(cur);
            incList = SplitTopLevel(inc);
        } catch (FormatException) {
            // Not a plain mapping, use the closest whole file as the base
            var all = new List<List<string>?> { cur };
            all.AddRange(history);
            var b = Closest(all, inc)!;
            return Same(b, cur) ? Copy(inc) : TextMerge(b, cur, inc, opt);
        }
        var curFields = curList.ToDictionary(x => x.key, x => x.lines);
        var incFields = incList.ToDictionary(x => x.key, x => x.lines);
        foreach (var key in MergeOrder(curList.Select(x => x.key).ToList(), incList.Select(x => x.key).ToList())) {
            curFields.TryGetValue(key, out var cv);
            incFields.TryGetValue(key, out var iv);
            var hist = histFields.Select(h => h.GetValueOrDefault(key)).ToList();
            MergeWithGuessedBase(hist, cv, iv, res, opt, key,
                (bb, cc, ii, rr) => MergeEntryWithHistory(hist, bb, cc, ii, rr, opt));
        }
        return res;
    }

    private static Result Copy(List<string> lines) {
        var r = new Result();
        r.Lines.AddRange(lines);
        return r;
    }

    private static void MergeWithGuessedBase(List<List<string>?> hist, List<string>? cur, List<string>? inc, Result res, Options opt, string unit,
        Action<List<string>, List<string>, List<string>, Result> mergeBoth) {
        if (Same(cur, inc)) {
            if (cur != null) {
                res.Lines.AddRange(cur);
            }
            return;
        }
        var candidates = new List<List<string>?> { cur };
        candidates.AddRange(hist);
        var b = Closest(candidates, inc);
        if (Same(b, cur)) {
            if (inc != null) {
                res.Lines.AddRange(inc);
            }
            return;
        }
        if (Same(b, inc)) {
            if (cur != null) {
                res.Lines.AddRange(cur);
            }

            opt.Notes.Add($"{opt.Label} {unit} kept the master version. The plugin has an older version because it was probably based on an older master version.");
            return;
        }
        MergeUnit(b, cur, inc, res, opt, unit, mergeBoth);
    }

    private static void MergeEntryWithHistory(List<List<string>?> histEntries, List<string> b, List<string> cur, List<string> inc, Result res, Options opt) {
        var curList = SplitRecordList(cur);
        var incList = SplitRecordList(inc);
        if (curList == null || incList == null || curList.Value.header != incList.Value.header) {
            var curBody = Body(cur);
            var incBody = Body(inc);
            if (curBody != null && incBody != null && cur[0] == inc[0]) {
                var histBodies = histEntries.Select(Body).Where(h => h != null).Select(h => h!).ToList();
                AppendBody(res, cur[0], MergeWithHistory(histBodies, curBody, incBody, opt));
                return;
            }
            Append(res, TextMerge(b, cur, inc, opt));
            return;
        }
        var histChildren = histEntries.Select(e => SplitRecordList(e)?.children.ToDictionary(x => x.fk, x => x.lines)).ToList();
        res.Lines.Add(curList.Value.header);
        var curChildren = curList.Value.children.ToDictionary(x => x.fk, x => x.lines);
        var incChildren = incList.Value.children.ToDictionary(x => x.fk, x => x.lines);
        foreach (var fk in MergeOrder(curList.Value.children.Select(x => x.fk).ToList(), incList.Value.children.Select(x => x.fk).ToList())) {
            curChildren.TryGetValue(fk, out var cv);
            incChildren.TryGetValue(fk, out var iv);
            var hist = histChildren.Select(h => h?.GetValueOrDefault(fk)).ToList();
            MergeWithGuessedBase(hist, cv, iv, res, opt, fk, (bb, cc, ii, rr) => Append(rr, TextMerge(bb, cc, ii, opt)));
        }
    }

    private static List<string>? Closest(List<List<string>?> candidates, List<string>? target) {
        List<string>? best = null;
        int bestDist = int.MaxValue;
        foreach (var c in candidates) {
            var d = Distance(c, target);
            if (d < bestDist) {
                best = c;
                bestDist = d;
            }
            if (d == 0) {
                break;
            }
        }
        return best;
    }

    private static int Distance(List<string>? a, List<string>? b) {
        if (a == null && b == null) {
            return 0;
        }

        if (a == null) {
            return b!.Count + 1;
        }

        if (b == null) {
            return a.Count + 1;
        }

        var counts = new Dictionary<string, int>();
        foreach (var l in a) {
            counts[l] = counts.GetValueOrDefault(l) + 1;
        }

        foreach (var l in b) {
            counts[l] = counts.GetValueOrDefault(l) - 1;
        }

        return counts.Values.Sum(Math.Abs);
    }

    // ------------------------------------------------------------------ Line merge

    private static string LinePrefix(string l) {
        var trimmed = l.TrimStart();
        var indent = l.Length - trimmed.Length;
        if (trimmed.StartsWith("- ")) {
            return new string(' ', indent) + "- ";
        }

        var c = trimmed.IndexOf(": ", StringComparison.Ordinal);
        if (c < 0) {
            c = trimmed.EndsWith(':') ? trimmed.Length - 1 : -1;
        }

        return c < 0 ? new string(' ', indent) + "\u0000" : new string(' ', indent) + trimmed[..c];
    }

    public static Result TextMerge(List<string> b, List<string> cur, List<string> inc, Options opt) {
        var res = new Result();
        if (Same(cur, inc)) {
            res.Lines.AddRange(cur);
            return res;
        }
        if (Same(cur, b)) {
            res.Lines.AddRange(inc);
            return res;
        }
        if (Same(inc, b)) {
            res.Lines.AddRange(cur);
            return res;
        }

        var dir = Path.Combine(Path.GetTempPath(), "fo-git-merge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try {
            string WriteTemp(string name, List<string> l) {
                var p = Path.Combine(dir, name);
                File.WriteAllText(p, string.Join("\n", l) + "\n");
                return p;
            }
            var curFile = WriteTemp("cur", cur);
            var baseFile = WriteTemp("base", b);
            var incFile = WriteTemp("inc", inc);
            var (code, merged, err) = Proc.Run("git", new[] { "merge-file", "-p", "--diff3", "-L", "current", "-L", "base", "-L", "incoming", curFile, baseFile, incFile }, dir);
            if (code < 0) {
                throw new InvalidOperationException(err);
            }

            var lines = merged.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            for (int i = 0; i < lines.Length; i++) {
                if (!lines[i].StartsWith("<<<<<<< ")) {
                    res.Lines.Add(lines[i]);
                    continue;
                }
                var hunkCur = new List<string>();
                var hunkBase = new List<string>();
                var hunkInc = new List<string>();
                int j = i + 1;
                for (; j < lines.Length && !lines[j].StartsWith("||||||| "); j++) {
                    hunkCur.Add(lines[j]);
                }

                for (j++; j < lines.Length && lines[j] != "======="; j++) {
                    hunkBase.Add(lines[j]);
                }

                for (j++; j < lines.Length && !lines[j].StartsWith(">>>>>>> "); j++) {
                    hunkInc.Add(lines[j]);
                }

                i = j;
                if (TryAligned(hunkBase, hunkCur, hunkInc, out var aligned)) {
                    res.Lines.AddRange(aligned);
                    continue;
                }
                if (hunkCur.Concat(hunkInc).Concat(hunkBase).All(l => FormKeyListItem().IsMatch(l))) {
                    var removed = hunkBase.Where(x => !hunkCur.Contains(x) || !hunkInc.Contains(x)).ToHashSet();
                    var union = hunkCur.Concat(hunkInc).Distinct().Where(x => !removed.Contains(x) || (hunkCur.Contains(x) && hunkInc.Contains(x)))
                        .OrderBy(l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(l => l.Trim()[2..8], StringComparer.Ordinal);
                    res.Lines.AddRange(union);
                    continue;
                }
                if (opt.Override) {
                    res.Lines.AddRange(hunkInc);
                    opt.Notes.Add($"{opt.Label} was modified in the same place in the master and the plugin ({Summary(hunkCur)}). Used the plugin version.");
                    continue;
                }
                WriteConflict(hunkCur, hunkBase, hunkInc, res);
            }
        } finally {
            try {
                Directory.Delete(dir, true);
            } catch {
            }
        }
        return res;
    }

    private static string Summary(List<string> lines) =>
        lines.Count == 0 ? "an empty spot" : lines[0].Trim() + (lines.Count > 1 ? $" and {lines.Count - 1} more line(s)" : "");

    private static bool TryAligned(List<string> b, List<string> cur, List<string> inc, out List<string> merged) {
        merged = new List<string>();
        if (cur.Count != b.Count || inc.Count != b.Count) {
            return false;
        }

        for (int i = 0; i < b.Count; i++) {
            var p = LinePrefix(b[i]);
            if (LinePrefix(cur[i]) != p || LinePrefix(inc[i]) != p) {
                return false;
            }

            if (cur[i] == b[i]) {
                merged.Add(inc[i]);
            } else if (inc[i] == b[i] || inc[i] == cur[i]) {
                merged.Add(cur[i]);
            } else {
                return false;
            }
        }
        return true;
    }
}
