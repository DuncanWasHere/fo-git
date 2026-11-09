using Mutagen.Bethesda.Plugins;

namespace FoGit;

public sealed class ScriptVars {
    public sealed record VarCondition(string File, int FunctionLine, int FirstLine, int SecondLine, int Function, int First, int Second);

    public sealed record FormParam(string File, int Line, int Raw);

    public readonly Dictionary<string, Dictionary<int, string>> Vars = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> ScriptOf = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> BaseOf = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> EditorIds = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<VarCondition> VarConditions = new();
    public readonly List<FormParam> FormParams = new();
    public readonly Dictionary<string, HashSet<string>> ScriptRefs = new(StringComparer.OrdinalIgnoreCase);
    public List<ModKey> Masters = new();
    public ModKey Self;

    private readonly object _lock = new();

    private static (int indent, bool item, string text) KeyOf(string line) {
        var t = line.TrimStart(' ');
        int indent = line.Length - t.Length;
        bool item = t.StartsWith("- ");
        if (item) {
            t = t[2..];
            indent += 2;
        }
        return (indent, item, t);
    }

    private static string Value(string text, string key) => text[(key.Length + 2)..].Trim();

    private sealed class PendingCondition {
        public int Indent, FunctionLine, FirstLine = -1, SecondLine = -1, Function, First, Second;
    }

    public void Read(string rel, string[] lines) {
        if (rel.Equals("RecordData.yaml", StringComparison.OrdinalIgnoreCase)) {
            ReadHeader(lines);
            return;
        }

        var fkAt = new Dictionary<int, string>();
        var vars = rel.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase) ? new Dictionary<int, string>() : null;
        string? scriptFk = null;
        int varsIndent = -1, pendingIndex = -1;
        PendingCondition? cond = null;
        int scriptRefIndent = -1;
        var conditions = new List<VarCondition>();
        var formParams = new List<FormParam>();
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scriptOf = new List<(string, string)>();
        var baseOf = new List<(string, string)>();
        var editorIds = new List<(string, string)>();

        void Finish() {
            if (cond == null) {
                return;
            }

            if (Conditions.IsVariable(cond.Function)) {
                conditions.Add(new VarCondition(rel, cond.FunctionLine, cond.FirstLine, cond.SecondLine, cond.Function, cond.First, cond.Second));
            }
            if (cond.FirstLine >= 0 && Conditions.IsForm(cond.Function, cond.First, second: false)) {
                formParams.Add(new FormParam(rel, cond.FirstLine, cond.First));
            }
            if (cond.SecondLine >= 0 && Conditions.IsForm(cond.Function, cond.First, second: true)) {
                formParams.Add(new FormParam(rel, cond.SecondLine, cond.Second));
            }
            cond = null;
        }

        for (int i = 0; i < lines.Length; i++) {
            var (k, item, t) = KeyOf(lines[i]);
            if (t.Length == 0) {
                continue;
            }

            foreach (var stale in fkAt.Keys.Where(x => x > k || (item && x == k)).ToList()) {
                fkAt.Remove(stale);
            }
            if (cond != null && (k < cond.Indent || (item && k == cond.Indent))) {
                Finish();
            }
            if (scriptRefIndent >= 0 && (k < scriptRefIndent || (item && k == scriptRefIndent))) {
                scriptRefIndent = -1;
            }
            if (varsIndent >= 0 && k <= varsIndent) {
                varsIndent = -1;
            }

            if (t.StartsWith("FormKey: ")) {
                fkAt[k] = Value(t, "FormKey");
                if (k == 0) {
                    scriptFk = fkAt[k];
                }
            } else if (t.StartsWith("EditorID: ") && fkAt.TryGetValue(k, out var owner)) {
                editorIds.Add((owner, Value(t, "EditorID")));
            } else if (t.StartsWith("Script: ") && fkAt.TryGetValue(k, out owner)) {
                scriptOf.Add((owner, Value(t, "Script")));
            } else if (t.StartsWith("Base: ") && fkAt.TryGetValue(k, out owner)) {
                baseOf.Add((owner, Value(t, "Base")));
            } else if (t.StartsWith("Function: ")) {
                Finish();
                var f = Conditions.FunctionIndex(Value(t, "Function"));
                if (f != null) {
                    cond = new PendingCondition { Indent = k, FunctionLine = i, Function = f.Value };
                }
            } else if (cond != null && k == cond.Indent && t.StartsWith("FirstParameter: ") && int.TryParse(Value(t, "FirstParameter"), out var p1)) {
                cond.First = p1;
                cond.FirstLine = i;
            } else if (cond != null && k == cond.Indent && t.StartsWith("SecondParameter: ") && int.TryParse(Value(t, "SecondParameter"), out var p2)) {
                cond.Second = p2;
                cond.SecondLine = i;
            } else if (t == "MutagenObjectType: ScriptObjectReference") {
                scriptRefIndent = k;
            } else if (scriptRefIndent == k && t.StartsWith("Reference: ")) {
                refs.Add(Value(t, "Reference"));
            } else if (vars != null && t == "LocalVariables:") {
                varsIndent = k;
            } else if (varsIndent >= 0 && t.StartsWith("Index: ") && int.TryParse(Value(t, "Index"), out var idx)) {
                pendingIndex = idx;
            } else if (varsIndent >= 0 && t.StartsWith("Name: ") && pendingIndex >= 0) {
                vars![pendingIndex] = Value(t, "Name");
                pendingIndex = -1;
            }
        }
        Finish();

        lock (_lock) {
            VarConditions.AddRange(conditions);
            FormParams.AddRange(formParams);
            if (refs.Count > 0) {
                ScriptRefs[rel] = refs;
            }
            if (vars != null && scriptFk != null) {
                Vars[scriptFk] = vars;
            }
            foreach (var (a, b) in scriptOf) {
                ScriptOf[a] = b;
            }
            foreach (var (a, b) in baseOf) {
                BaseOf[a] = b;
            }
            foreach (var (a, b) in editorIds) {
                EditorIds[a] = b;
            }
        }
    }

    private void ReadHeader(string[] lines) {
        var masters = new List<ModKey>();
        foreach (var l in lines) {
            var t = l.Trim();
            if (t.StartsWith("ModKey: ")) {
                Self = ModKey.FromFileName(t[8..].Trim());
            } else if (t.StartsWith("- Master: ")) {
                masters.Add(ModKey.FromFileName(t[10..].Trim()));
            }
        }
        lock (_lock) {
            Masters = masters;
        }
    }

    public static string Key(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public string? Target(int raw) => Conditions.Decode(raw, Masters, Self) is { } fk ? Key(fk) : null;

    // Script that a quest, a placed reference or a base form runs
    public string? ScriptFor(string? fk) {
        if (fk == null) {
            return null;
        }
        if (ScriptOf.TryGetValue(fk, out var s)) {
            return s;
        }
        return BaseOf.TryGetValue(fk, out var b) && ScriptOf.TryGetValue(b, out s) ? s : null;
    }

    public string Describe(string fk) => EditorIds.TryGetValue(fk, out var e) ? $"{e} ({fk})" : fk;

    // True if var index changed
    public static bool Breaks(Dictionary<int, string> before, Dictionary<int, string> after) =>
        before.Any(kv => !after.TryGetValue(kv.Key, out var n) || !n.Equals(kv.Value, StringComparison.OrdinalIgnoreCase));

    // Script variables in the Spriggit text at a commit
    public static ScriptVars AtCommit(Repo r, GitBlobReader blobs, string commit) {
        var vars = new ScriptVars();
        var folder = r.Config.SpriggitFolder.TrimEnd('/');
        var (code, output, _) = r.GitRaw("-c", "core.quotepath=off", "ls-tree", "-r", "--name-only", commit, "--", folder + "/Scripts");
        if (code != 0) {
            return vars;
        }

        foreach (var path in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
            var text = blobs.Read(commit, path.Trim());
            if (text != null) {
                vars.Read(path.Trim()[(folder.Length + 1)..], text.Split('\n'));
            }
        }
        return vars;
    }

    public static FixReport FixMerge(Repo r, IReadOnlyDictionary<uint, uint> renumbered) {
        var parents = r.HeadParents();
        if (parents.Length < 2) {
            return new FixReport();
        }

        var (code, mergeBase, _) = r.GitRaw("merge-base", parents[0], parents[1]);
        if (code != 0) {
            return new FixReport();
        }

        mergeBase = mergeBase.Trim();
        var folder = r.Config.SpriggitFolder.TrimEnd('/');
        var current = CollisionFixer.ChangedFiles(r, mergeBase, parents[0], folder);
        var incoming = CollisionFixer.ChangedFiles(r, mergeBase, parents[1], folder);
        // Nothing to fix unless a script changed
        if (!current.Concat(incoming).Any(f => f.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase))) {
            return new FixReport();
        }

        using var blobs = new GitBlobReader(r.Root);
        var baseVars = AtCommit(r, blobs, mergeBase);
        var currentVars = AtCommit(r, blobs, parents[0]);
        var incomingVars = AtCommit(r, blobs, parents[1]);
        var currentKeys = current.Select(SpriggitText.Key).ToHashSet();
        var incomingKeys = incoming.Select(SpriggitText.Key).ToHashSet();

        // Renumbered forms have their old IDs on the current branch
        var master = r.Config.Master.ToLowerInvariant();
        var oldIds = renumbered.ToDictionary(kv => $"{kv.Value:x6}", kv => $"{kv.Key:x6}");
        string Unrenumber(string s, char separator) {
            var lower = s.ToLowerInvariant();
            foreach (var (now, before) in oldIds) {
                lower = lower.Replace($"{now}{separator}{master}", $"{before}{separator}{master}");
            }
            return lower;
        }

        return Fix(r.SpriggitDir, TextOps.Scan(r.SpriggitDir).Vars, (file, script) => {
            var key = SpriggitText.Key(file);
            var oldKey = Unrenumber(key, '_');
            bool onCurrent = currentKeys.Contains(oldKey), onIncoming = incomingKeys.Contains(key);
            if (onCurrent && oldKey != key) {
                script = Unrenumber(script, ':');
            }
            // Changed on both branches, so idfk which variables it uses
            if (onCurrent && onIncoming) {
                return null;
            }

            var from = onCurrent ? currentVars : onIncoming ? incomingVars : baseVars;
            return from.Vars.TryGetValue(script, out var v) ? v : null;
        });
    }

    public sealed class FixReport {
        public readonly List<string> Fixed = new();
        public readonly List<string> Warnings = new();
    }

    public static FixReport Fix(string spriggitDir, ScriptVars cur, Func<string, string, Dictionary<int, string>?> origin) {
        var rep = new FixReport();
        var edits = new Dictionary<string, List<(int line, bool insert, int value)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cur.VarConditions) {
            var script = cur.ScriptFor(cur.Target(c.First));
            if (script == null || !cur.Vars.TryGetValue(script, out var now)) {
                continue;
            }

            var before = origin(c.File, script);
            if (before == null || !before.TryGetValue(c.Second, out var name)) {
                continue;
            }
            if (now.TryGetValue(c.Second, out var n) && n.Equals(name, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var match = now.Where(kv => kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => (int?)kv.Key).FirstOrDefault();
            if (match == null) {
                rep.Warnings.Add($"{c.File} has a condition referencing {name} in {cur.Describe(script)}, but the variable was deleted from the script.");
                continue;
            }
            if (!edits.TryGetValue(c.File, out var list)) {
                edits[c.File] = list = new();
            }

            list.Add(c.SecondLine >= 0 ? (c.SecondLine, false, match.Value) : (c.FirstLine, true, match.Value));
            rep.Fixed.Add($"{c.File} condition referencing {name} in {cur.Describe(script)} changed to reference index {match} instead of {c.Second}");
        }

        foreach (var (file, list) in edits) {
            var path = Path.Combine(spriggitDir, Repo.Norm(file));
            var lines = File.ReadAllLines(path).ToList();
            foreach (var (line, insert, value) in list.OrderByDescending(x => x.line)) {
                var l = lines[line];
                var key = insert ? "FirstParameter: " : "SecondParameter: ";
                var prefix = l[..l.IndexOf(key, StringComparison.Ordinal)];
                if (insert) {
                    lines.Insert(line + 1, new string(' ', prefix.Length) + "SecondParameter: " + value);
                } else {
                    lines[line] = prefix + key + value;
                }
            }
            Repo.WriteIfChanged(path, System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        }

        foreach (var (file, refs) in cur.ScriptRefs) {
            foreach (var fk in refs) {
                var script = cur.ScriptFor(fk);
                if (script == null || !cur.Vars.TryGetValue(script, out var now)) {
                    continue;
                }

                var before = origin(file, script);
                if (before != null && Breaks(before, now)) {
                    rep.Warnings.Add($"{file} has a script that references {cur.Describe(fk)}, but the variables in {cur.Describe(script)} changed. Recompile it in the GECK.");
                }
            }
        }
        return rep;
    }
}
