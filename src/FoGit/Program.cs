using System.Diagnostics;
using FoGit;
using Mutagen.Bethesda.Fallout3;
using Mutagen.Bethesda.Plugins;

return Cli.Run(args);

static class Cli {
    const string Usage = """
        fo-git commands:
          merge-plugin [file]     Pick a plugin from the plugin folder and merge it into the master
          gen-spriggit            Regenerate the text files from the current ESM plugin build
          build-esm               Rebuild the ESM plugin from the text
          setup                   Download Spriggit and build the ESM

          status                  Show whether the current ESM build matches the text
          check [--text] [--binary] [--accept-current]
                                  Look for FormID collisions, missing records and conflict markers.
                                  --accept-current adds found issues to the known issues file.
          fix-collisions          Rerun the FormID collision fix for the merge commit at HEAD

          hook <name> [args]      Git hooks (pre-commit, post-merge, post-commit, post-checkout, post-rewrite)
          merge-driver %O %A %B %P

          version                 Print the fo-git version

        --window keeps the window open if there are warnings and closes it otherwise.
        """;

    public static int Run(string[] args) {
        Ui.Window = args.Contains("--window");
        args = args.Where(a => a != "--window").ToArray();
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args[0] is "--version" or "version") {
            Console.WriteLine(typeof(Cli).Assembly.GetName().Version?.ToString(3));
            return 0;
        }
        int code;
        try {
            var cmd = args[0];
            var rest = args[1..];
            if (cmd == "merge-driver") {
                return YamlMerge.RunDriver(rest[0], rest[1], rest[2], rest.ElementAtOrDefault(3) ?? rest[1]);
            }

            var repo = Repo.Find();
            if (cmd != "hook") {
                EnsureGitConfig(repo);
            }

            code = cmd switch {
                "merge-plugin" => MergePlugin(repo, rest),
                "gen-spriggit" => GenSpriggit(repo),
                "build-esm" => BuildEsmCommand(repo),
                "setup" => Setup(repo),
                "status" => Status(repo),
                "check" => Check(repo, rest),
                "fix-collisions" => FixCollisions(repo, rebuild: true),
                "hook" => Hook(repo, rest),
                _ => throw new UserError($"Unknown command {cmd}.\n\n{Usage}"),
            };
        } catch (UserError e) {
            Ui.Error(e.Message);
            code = 2;
        } catch (Exception e) {
            Ui.Error("fo-git crashed.\n" + e);
            code = 3;
        }
        Ui.Finish();
        return code;
    }

    static IEnumerable<ModKey> LoadOrder(Repo r) => r.Config.Masters.Append(r.Config.Master).Select(x => ModKey.FromFileName(x));

    static void BuildEsm(Repo r, bool discardEdits = false) {
        if (!Directory.Exists(r.SpriggitDir)) {
            throw new UserError($"There's no master data in {r.SpriggitDir}.");
        }

        if (File.Exists(r.MasterPath) && r.GetBuildState() is Repo.BuildState.EsmEdited or Repo.BuildState.Missing) {
            if (!discardEdits) {
                throw new UserError($"{r.Config.Master} has edits that aren't saved to the Spriggit text. Run \"Save Master To Text.bat\" to keep them.");
            }

            var backup = Path.Combine(r.StateDir, "backup", $"{Path.GetFileNameWithoutExtension(r.Config.Master)}-{DateTime.Now:yyyyMMdd-HHmmss}.esm");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(r.MasterPath, backup);
            Ui.Info($"{r.Config.Master} backup created in {Path.GetRelativePath(r.Root, backup)}.");
        }
        Ui.Step($"Building {r.Config.Master} from the Spriggit text.");
        var hash = r.TextTree();
        r.Deserialize(r.SpriggitDir, r.MasterPath);
        r.WriteBuildInfo(hash);
        Ui.Good($"{r.Config.Master} is up to date.");
    }

    static int BuildEsmCommand(Repo r) {
        var state = r.GetBuildState();
        bool discard = false;
        if (state == Repo.BuildState.EsmEdited || (state == Repo.BuildState.Missing && File.Exists(r.MasterPath))) {
            Ui.Warn($"{r.Config.Master} has edits that aren't in the Spriggit text, or wasn't built by fo-git. Rebuilding will discard them. A backup will be created in .fo-git/backup.");
            if (Console.IsInputRedirected) {
                throw new UserError("Won't rebuild without confirmation.");
            }

            Console.Write("Press Y to rebuild anyways, or any other key to cancel. ");
            var key = Console.ReadKey(true);
            Console.WriteLine();
            if (key.Key != ConsoleKey.Y) {
                Ui.Info("Cancelled. Nothing changed.");
                return 1;
            }
            discard = true;
        }
        BuildEsm(r, discard);
        return 0;
    }

    static int GenSpriggit(Repo r) {
        var state = r.GetBuildState();
        if (!File.Exists(r.MasterPath)) {
            throw new UserError($"There's no {r.Config.Master} to save.");
        }

        if (!Directory.Exists(r.SpriggitDir) || !Directory.EnumerateFileSystemEntries(r.SpriggitDir).Any()) {
            return FirstGen(r);
        }

        if (state == Repo.BuildState.UpToDate) {
            Ui.Info($"{r.Config.Master} has no unsaved edits. Nothing to do.");
            return 0;
        }
        if (state == Repo.BuildState.TextChanged) {
            Ui.Info($"{r.Config.Master} has no edits, but the Spriggit text changed. Rebuilding.");
            BuildEsm(r);
            return 0;
        }
        var info = r.ReadBuildInfo();
        bool baseIsCurrent = info != null && info.TextHash == r.TextTree();
        if (!baseIsCurrent) {
            Ui.Info("The text changed after the ESM was built. Merging.");
        }

        SaveEsmIntoText(r, r.MasterPath, baseIsCurrent);
        if (baseIsCurrent) {
            r.WriteBuildInfo(r.TextTree());
        } else {
            BuildEsm(r, discardEdits: true);
        }

        RunTextCheck(r);
        Ui.Good($"Saved. Commit changes in {r.Config.SpriggitFolder} now.");
        return 0;
    }

    static int FirstGen(Repo r) {
        var tmp = r.TempDir("first");
        try {
            var pass1 = Path.Combine(tmp, "pass1");
            r.Serialize(r.MasterPath, pass1);
            var esm = Path.Combine(tmp, r.Config.Master);
            r.Deserialize(pass1, esm);
            r.Serialize(esm, r.SpriggitDir);

            var backup = Path.Combine(r.StateDir, "backup", $"{Path.GetFileNameWithoutExtension(r.Config.Master)}-{DateTime.Now:yyyyMMdd-HHmmss}.esm");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(r.MasterPath, backup);
            Repo.CopyWithRetry(esm, r.MasterPath);
            r.WriteBuildInfo(r.TextTree());
            Ui.Info($"{r.Config.Master} backup created in {Path.GetRelativePath(r.Root, backup)}.");
        } finally {
            Repo.TryDelete(tmp);
        }
        RunTextCheck(r);
        Ui.Good($"Created Spriggit text in {r.Config.SpriggitFolder}.");
        return 0;
    }

    static MasterText.ApplyReport SaveEsmIntoText(Repo r, string esmPath, bool baseIsCurrent) {
        Ui.Step("Converting the ESM to text");
        var tmp = r.TempDir("text");
        try {
            r.Serialize(esmPath, tmp);
            Ui.Step("Updating " + r.Config.SpriggitFolder);
            var rep = MasterText.Apply(r, tmp, baseIsCurrent);
            Ui.Info($"  {rep.Changed} changed, {rep.Added} added, {rep.Removed} removed record files");
            ShowNotes(r, rep.Notes, "gen-spriggit");
            return rep;
        } finally {
            Repo.TryDelete(tmp);
        }
    }

    static void ShowNotes(Repo r, List<string> notes, string what) {
        if (notes.Count == 0) {
            return;
        }

        var log = Path.Combine(r.StateDir, "logs", $"{what}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, notes);
        var clashes = notes.Where(n => n.Contains("Used the plugin version")).ToList();
        var kept = notes.Count - clashes.Count;
        foreach (var n in clashes) {
            Ui.Warn(n);
        }

        if (kept > 0) {
            Ui.Info($"  Kept the master value for {kept} field(s) where the plugin had an older value.");
        }
    }

    static int MergePlugin(Repo r, string[] args) {
        string pluginPath;
        if (args.Length > 0 && !args[0].StartsWith("--")) {
            pluginPath = Path.GetFullPath(args[0]);
            if (!File.Exists(pluginPath)) {
                throw new UserError($"There's no plugin at {pluginPath}");
            }
        } else {
            var picked = PickPlugin(r);
            if (picked == null) {
                return 1;
            }

            pluginPath = picked;
        }
        var name = Path.GetFileName(pluginPath);

        // ESM needs to match the text before merging
        var state = r.GetBuildState();
        if (state == Repo.BuildState.EsmEdited || (state == Repo.BuildState.Missing && File.Exists(r.MasterPath))) {
            Ui.Info($"{r.Config.Master} has unsaved edits. Saving them first!");
            GenSpriggit(r);
        } else if (state != Repo.BuildState.UpToDate) {
            BuildEsm(r);
        }

        Ui.Step($"Merging {name} into {r.Config.Master}");
        var sw = Stopwatch.StartNew();
        var master = PluginOps.LoadMutable(r.MasterPath);
        var plugin = PluginOps.LoadMutable(pluginPath);
        var previous = MergeMap.Load(r, name);
        var res = PluginOps.MergeInto(master, plugin, previous);
        foreach (var e in res.Errors) {
            Ui.Error(e);
        }

        if (res.Errors.Count > 0) {
            throw new UserError($"Canceled merging {name}. Nothing changed.");
        }

        foreach (var w in res.Warnings) {
            Ui.Warn(w);
        }

        Ui.Info($"  {res.Overrides} edited, {res.NewRecords} new, {res.Deleted} deleted record(s)");
        if (res.Renumbered > 0) {
            Ui.Info($"  {res.Renumbered} new form(s) had their formIDs renumbered.");
        }

        if (previous.Count > 0) {
            Ui.Info($"  {name} was merged before. Reused the FormIDs.");
        }

        var tmp = r.TempDir("merge");
        try {
            var tmpEsm = Path.Combine(tmp, r.Config.Master);
            master.WriteToBinary(tmpEsm, PluginOps.WriteParams(LoadOrder(r)));
            // Plugin might be made from an older master, compare with history to find what it changed
            SaveEsmIntoText(r, tmpEsm, baseIsCurrent: false);
        } finally {
            Repo.TryDelete(tmp);
        }

        var log = Path.Combine(r.StateDir, "logs", $"merge-{Path.GetFileNameWithoutExtension(name)}-{DateTime.Now:yyyyMMdd-HHmmss}-records.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, res.Details.Concat(res.Warnings.Select(w => "warning " + w)));

        var map = new Dictionary<uint, uint>(previous);
        foreach (var (k, v) in res.IdMap) {
            map[k] = v;
        }

        MergeMap.Save(r, name, map);

        RunTextCheck(r);
        BuildEsm(r, discardEdits: true);
        Ui.Good($"Merged {name} in {sw.Elapsed:m\\:ss}.");
        Ui.Info($"Commit the changes in {r.Config.SpriggitFolder}. Merged forms are listed in {Path.GetRelativePath(r.Root, log)}.");
        return 0;
    }

    static string? PickPlugin(Repo r) {
        var folder = r.PluginFolder;
        if (!Directory.Exists(folder)) {
            throw new UserError($"The plugin folder {folder} doesn't exist. Set \"pluginFolder\" in fo-git.local.json to wherever your new GECK plugins are saved.");
        }

        var plugins = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Equals(r.Config.Master, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        if (plugins.Count == 0) {
            throw new UserError($"No plugin files in {folder}.");
        }

        Console.WriteLine($"Plugins in {folder}");
        Console.WriteLine();
        for (int i = 0; i < plugins.Count; i++) {
            var fi = new FileInfo(plugins[i]);
            Console.WriteLine($"  {i + 1,3}   {fi.Name,-45} {fi.Length / 1024.0,8:0} KB");
        }
        Console.WriteLine();
        if (Console.IsInputRedirected) {
            throw new UserError("No plugin was specified.");
        }

        int choice;
        if (plugins.Count <= 9) {
            Console.Write($"Select a plugin to merge into {r.Config.Master}, or Esc to cancel. ");
            while (true) {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Escape) {
                    Console.WriteLine();
                    Ui.Info("Cancelled. Nothing changed.");
                    return null;
                }
                if (k.KeyChar >= '1' && k.KeyChar <= '0' + plugins.Count) {
                    choice = k.KeyChar - '0';
                    break;
                }
            }
            Console.WriteLine(choice);
        } else {
            while (true) {
                Console.Write($"Select a plugin to merge into {r.Config.Master} and press Enter, or just Enter to cancel. ");
                var line = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(line)) {
                    Ui.Info("Cancelled. Nothing changed.");
                    return null;
                }
                if (int.TryParse(line, out choice) && choice >= 1 && choice <= plugins.Count) {
                    break;
                }
            }
        }
        return plugins[choice - 1];
    }

    static readonly (string key, string value)[] GitSettings = {
        ("core.hooksPath", "Tools/githooks"),
        ("merge.fo-git.name", "fo-git record merge for Spriggit YAML"),
        ("merge.fo-git.driver", "Tools/fo-git.exe merge-driver %O %A %B %P"),
        ("core.longpaths", "true"),
        ("core.safecrlf", "false"),
        // Collision fix needs merge commits with the local branch as the first parent
        ("pull.rebase", "false"),
    };

    static void EnsureGitConfig(Repo r) {
        bool changed = false;
        foreach (var (key, value) in GitSettings) {
            var (code, current, _) = r.GitRaw("config", "--local", "--get", key);
            if (code == 0 && current.Trim() == value) {
                continue;
            }

            r.Git("config", "--local", key, value);
            changed = true;
        }
        if (changed) {
            Ui.Info("Enabled the git hooks and merge driver for this local repo.");
        }
    }

    static int Setup(Repo r) {
        Ui.Step("Getting Spriggit");
        _ = r.SpriggitCli;
        Ui.Info("  Spriggit is ready.");

        Ui.Step("Checking the plugin folder");
        if (Directory.Exists(r.PluginFolder)) {
            Ui.Info($"  Plugins will be listed from {r.PluginFolder}");
        } else {
            Ui.Warn($"The plugin folder {r.PluginFolder} doesn't exist. Create fo-git.local.json in the repo folder with your own path. Like this:\n  {{ \"pluginFolder\": \"C:/path/to/MO2/overwrite\" }}");
        }

        var state = r.GetBuildState();
        if (state == Repo.BuildState.UpToDate) {
            Ui.Info($"  {r.Config.Master} is already up to date.");
        } else if (state == Repo.BuildState.EsmEdited) {
            Ui.Warn($"{r.Config.Master} has edits that aren't in the Spriggit text. Run \"Save Master To Text.bat\" to keep them, or \"Rebuild Master.bat\" to discard them.");
        } else {
            BuildEsm(r, discardEdits: true);
        }

        Ui.Good("Setup done.");
        return 0;
    }

    static int Status(Repo r) {
        Ui.Info($"Repo           {r.Root}");
        Ui.Info($"Branch         {r.Git("branch", "--show-current")}");
        Ui.Info($"Plugin folder  {r.PluginFolder}");
        var info = r.ReadBuildInfo();
        Ui.Info($"{r.Config.Master,-14} " + r.GetBuildState() switch {
            Repo.BuildState.Missing => "not built by fo-git. Run Rebuild Master.bat.",
            Repo.BuildState.UpToDate => $"matches the text (built {info!.BuiltAt:g})",
            Repo.BuildState.TextChanged => "older than the text. Run Rebuild Master.bat.",
            Repo.BuildState.EsmEdited => "has unsaved edits. Run Save Master To Text.bat.",
            _ => "",
        });
        return 0;
    }

    // ------------------------------------------------------------------ check

    static TextOps.TextReport RunTextCheck(Repo r, bool quiet = false) {
        if (!quiet) {
            Ui.Step("Checking the master text");
        }

        var rep = TextOps.Check(r.SpriggitDir, r.Config, r.KnownIssues);
        foreach (var w in rep.Warnings.Take(40)) {
            Ui.Warn(w);
        }

        if (rep.Warnings.Count > 40) {
            Ui.Warn($"{rep.Warnings.Count - 40} more warnings. Run \"fo-git check\" to see them all.");
        }

        foreach (var e in rep.Errors.Take(40)) {
            Ui.Error(e);
        }

        if (!quiet && rep.Errors.Count == 0 && rep.Warnings.Count == 0) {
            Ui.Info($"  No problems in {rep.Records} records.");
        }

        return rep;
    }

    static int Check(Repo r, string[] args) {
        bool text = !args.Contains("--binary") || args.Contains("--text"), binary = !args.Contains("--text") || args.Contains("--binary");
        var found = new List<string>();
        if (text) {
            var rep = RunTextCheck(r);
            found.AddRange(rep.Errors.Concat(rep.Warnings));
        }
        if (binary) {
            if (r.GetBuildState() != Repo.BuildState.UpToDate) {
                Ui.Info($"Skipped the ESM check because {r.Config.Master} doesn't match the text.");
            } else {
                Ui.Step($"Checking {r.Config.Master}");
                using var m = PluginOps.LoadOverlay(r.MasterPath);
                var rep = PluginOps.CheckBinary(m, r.Local.MasterSearchPaths);
                var known = r.KnownIssues;
                foreach (var w in rep.Warnings.Take(100)) {
                    Ui.Warn(w);
                }

                foreach (var e in rep.Errors.Where(e => !known.Contains(TextOps.IssueKey(e)))) {
                    Ui.Error(e);
                    found.Add(e);
                }
            }
        }
        if (args.Contains("--accept-current")) {
            var known = r.KnownIssues;
            known.UnionWith(found.Select(TextOps.IssueKey));
            Directory.CreateDirectory(Path.GetDirectoryName(r.KnownIssuesPath)!);
            File.WriteAllLines(r.KnownIssuesPath, new[] {
                "# These don't get reported. REmove the line when fixed.",
            }.Concat(known.OrderBy(x => x)));
            Ui.Info($"Wrote {known.Count} issue(s) to {r.Config.KnownIssuesFile}. Commit the file now!");
            return 0;
        }
        return Ui.Errors == 0 ? 0 : 1;
    }

    static bool FixCollisions(Repo r) {
        var head = r.Git("rev-parse", "HEAD");
        var fixedLog = Path.Combine(r.StateDir, "fixed-merges.txt");
        if (File.Exists(fixedLog) && File.ReadLines(fixedLog).Contains(head)) {
            return false;
        }

        var rep = CollisionFixer.Run(r);
        File.AppendAllLines(fixedLog, new[] { head });
        foreach (var w in rep.Warnings) {
            Ui.Warn(w);
        }

        if (rep.Renumbered.Count == 0) {
            return false;
        }

        Ui.Warn("The last merge added records with the same FormIDs as new records on this branch. Renumbered the ones from this branch.");
        foreach (var n in rep.Renumbered) {
            Ui.Info("  " + n);
        }

        Ui.Info($"Commit the changes in {r.Config.SpriggitFolder} now!");
        return true;
    }

    static int FixCollisions(Repo r, bool rebuild) {
        Repo.TryDelete(Path.Combine(r.StateDir, "fixed-merges.txt"));
        var changed = FixCollisions(r);
        if (!changed) {
            Ui.Info("Nothing to fix.");
        }

        if (rebuild) {
            AfterTextChange(r);
        }

        return 0;
    }

    static void AfterTextChange(Repo r) {
        switch (r.GetBuildState()) {
            case Repo.BuildState.UpToDate:
                return;
            case Repo.BuildState.EsmEdited:
                Ui.Warn($"The  Spriggit text changed, but {r.Config.Master} has unsaved edits and won't be rebuilt. Run \"Save Master To Text.bat\" to merge your edits with the new text.");
                return;
            case Repo.BuildState.Missing when File.Exists(r.MasterPath):
                Ui.Warn($"{r.Config.Master} wasn't built by fo-git. Run \"Save Master To Text.bat\" to keep its edits, or \"Rebuild Master.bat\" to replace it.");
                return;
            default:
                BuildEsm(r);
                return;
        }
    }

    // True if the Spriggit text changed between the two commits
    static bool TextChanged(Repo r, string from, string to) {
        var (code, stdout, _) = r.GitRaw("diff", "--name-only", from, to, "--", r.Config.SpriggitFolder);
        return code != 0 || stdout.Trim().Length > 0;
    }

    static List<string> StagedMarkers(Repo r) {
        var found = new List<string>();
        var diff = r.Git("-c", "core.quotepath=off", "diff", "--cached", "-U0", "--no-color", "--", r.Config.SpriggitFolder);
        string file = "";
        int line = 0;
        foreach (var raw in diff.Split('\n')) {
            var l = raw.TrimEnd('\r');
            if (l.StartsWith("+++ ")) {
                // Git adds a tab after names with spaces
                file = (l.StartsWith("+++ b/") ? l[6..] : l[4..]).TrimEnd('\t');
            } else if (l.StartsWith("@@ ")) {
                // @@ -a,b +c,d @@ where c is the first new line
                var plus = l.Split(' ')[2];
                line = int.Parse(plus[1..].Split(',')[0]);
            } else if (l.StartsWith('+')) {
                var t = l[1..];
                if (t.StartsWith("<<<<<<< ") || t.StartsWith(">>>>>>> ") || t.StartsWith("||||||| ") || t == "=======") {
                    found.Add($"{file} line {line}");
                }
                line++;
            }
        }
        return found;
    }

    static int Hook(Repo r, string[] args) {
        var name = args.FirstOrDefault();
        var folder = r.Config.SpriggitFolder;
        switch (name) {
            case "pre-commit": {
                    // Only if Spriggit files are being committed
                    if (r.Git("diff", "--cached", "--name-only", "--", folder).Length == 0) {
                        return 0;
                    }

                    if (r.EsmEdited()) {
                        Console.Error.WriteLine($"fo-git blocked this commit. {r.Config.Master} has edits that aren't saved to the Spriggit text.");
                        Console.Error.WriteLine("Run Tools/Save Master To Text.bat, then retry the commit. To discard the edits, run Tools/Rebuild Master.bat.");
                        return 1;
                    }

                    // Only check staged changes here, Github handles the rest
                    var markers = StagedMarkers(r);
                    if (markers.Count == 0) {
                        return 0;
                    }

                    Console.Error.WriteLine("fo-git blocked this commit. There are merge conflict markers left in the staged Spriggit text.");
                    foreach (var m in markers.Take(30)) {
                        Console.Error.WriteLine("  " + m);
                    }

                    return 1;
                }
            case "post-merge":
                if (!TextChanged(r, "ORIG_HEAD", "HEAD")) {
                    return 0;
                }

                FixCollisions(r);
                RunTextCheck(r, quiet: true);
                AfterTextChange(r);
                return 0;
            case "post-commit": {
                    // Only matters for merge commits made by hand after fixing conflicts
                    var parents = r.Git("rev-list", "--parents", "-n", "1", "HEAD").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parents.Length < 3 || !TextChanged(r, "HEAD^1", "HEAD")) {
                        return 0;
                    }

                    FixCollisions(r);
                    RunTextCheck(r, quiet: true);
                    AfterTextChange(r);
                    return 0;
                }
            case "post-checkout":
                // Third arg is 1 for a branch switch, 0 for a file checkout
                if (args.ElementAtOrDefault(3) == "0" || !TextChanged(r, args.ElementAtOrDefault(1) ?? "", args.ElementAtOrDefault(2) ?? "")) {
                    return 0;
                }

                AfterTextChange(r);
                return 0;
            case "post-rewrite":
                if (args.ElementAtOrDefault(1) != "rebase" || !TextChanged(r, "ORIG_HEAD", "HEAD")) {
                    return 0;
                }

                AfterTextChange(r);
                return 0;
            default:
                return 0;
        }
    }
}
