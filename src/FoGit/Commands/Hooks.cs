using FoGit;

static partial class Cli {
    static int Hook(Repo r, string[] args) {
        switch (args.FirstOrDefault()) {
            case "pre-commit":
                return PreCommit(r);
            case "post-merge":
                if (TextChanged(r, "ORIG_HEAD", "HEAD")) {
                    AfterMerge(r);
                }
                break;
            case "post-commit":
                // Only matters for merge commits made by hand after fixing conflicts
                if (r.HeadParents().Length >= 2 && TextChanged(r, "HEAD^1", "HEAD")) {
                    AfterMerge(r);
                }
                break;
            case "post-checkout":
                // Third arg is 1 for branch switch, 0 for file checkout
                if (args.ElementAtOrDefault(3) != "0" && !r.RebaseInProgress() && TextChanged(r, args.ElementAtOrDefault(1) ?? "", args.ElementAtOrDefault(2) ?? "")) {
                    AfterTextChange(r);
                }
                break;
            case "post-rewrite":
                if (args.ElementAtOrDefault(1) == "rebase" && TextChanged(r, "ORIG_HEAD", "HEAD")) {
                    AfterTextChange(r);
                }
                break;
        }
        return 0;
    }

    static int PreCommit(Repo r) {
        // Only if Spriggit text is being committed
        if (r.Git("diff", "--cached", "--name-only", "--", r.Config.SpriggitFolder).Length == 0) {
            return 0;
        }

        if (r.EsmEdited()) {
            switch (AskAboutEdits($"{r.Config.Master} has changes that aren't reflected in the Spriggit text.", canAbort: true)) {
                case EditChoice.Generate:
                    GenerateText(r);
                    Console.Error.WriteLine("Generated the Spriggit text. Stage the changes you want to keep and commit again.");
                    return 1;
                case EditChoice.Rebuild:
                    BuildEsm(r, discardEdits: true);
                    break;
                default:
                    Console.Error.WriteLine($"fo-git blocked this commit. Run Tools/{GenerateBat}, then retry the commit. To discard the edits, run Tools/{BuildBat}.");
                    return 1;
            }
        }

        // Only check staged changes here, GitHub handles the rest
        var markers = StagedMarkers(r);
        if (markers.Count == 0) {
            return 0;
        }

        Console.Error.WriteLine("fo-git blocked this commit. There are merge conflict markers left in the staged Spriggit text.");
        foreach (var m in markers) {
            Console.Error.WriteLine("  " + m);
        }

        return 1;
    }

    static void AfterMerge(Repo r) {
        FixCollisions(r);
        ValidateText(r, quiet: true);
        AfterTextChange(r);
    }

    static void AfterTextChange(Repo r) {
        switch (r.GetBuildState()) {
            case Repo.BuildState.UpToDate:
                return;
            case Repo.BuildState.EsmEdited:
                if (ResolveEdits(r, $"The Spriggit text changed, but {r.Config.Master} has changes that aren't reflected in the text.", canAbort: false) == EditChoice.Leave) {
                    Ui.Info($"The ESM wasn't built. Run \"{GenerateBat}\" to merge your edits with the new text, or \"{BuildBat}\" to discard them.");
                }
                return;
            case Repo.BuildState.Missing when File.Exists(r.EsmPath):
                if (ResolveEdits(r, $"{r.Config.Master} wasn't built by fo-git.", canAbort: false) == EditChoice.Leave) {
                    Ui.Info($"Run \"{GenerateBat}\" to keep its edits, or \"{BuildBat}\" to replace it.");
                }
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

    static int FixCollisionsCommand(Repo r) {
        if (!FixCollisions(r, again: true)) {
            Ui.Info("Nothing to fix.");
        }

        AfterTextChange(r);
        return 0;
    }

    // Runs once per merge commit unless again is set
    static bool FixCollisions(Repo r, bool again = false) {
        var head = r.Git("rev-parse", "HEAD");
        var fixedLog = Path.Combine(r.StateDir, "fixed-merges.txt");
        if (again) {
            Repo.TryDelete(fixedLog);
        } else if (File.Exists(fixedLog) && File.ReadLines(fixedLog).Contains(head)) {
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
}
