using FoGit;
using FoGit.Repo;

static partial class Cli {
    static int Hook(FoGitRepo r, string[] args) {
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

    static int PreCommit(FoGitRepo foGitRepo) {
        // Only continue if Spriggit text is being committed
        if (foGitRepo.Git("diff", "--cached", "--name-only", "--", foGitRepo.RepoConfig.SpriggitFolder).Length == 0) {
            return 0;
        }

        if (foGitRepo.GetBuildState() == BuildState.TextOutOfDate) {
            switch (AskAboutEdits($"{foGitRepo.RepoConfig.Master} has changes that aren't reflected in the Spriggit text.", canAbort: true)) {
                case EditChoice.RegenerateText:
                    GenerateTextCommand(foGitRepo);
                    Console.Error.WriteLine("Generated the Spriggit text. Stage the changes you want to keep and commit again.");
                    return 1;
                case EditChoice.RebuildEsm:
                    BuildEsm(foGitRepo, discardEdits: true);
                    break;
                default:
                    Console.Error.WriteLine($"fo-git blocked this commit. Run Tools/{GenerateTextBat}, then retry the commit. To discard the edits, run Tools/{BuildEsmBat}.");
                    return 1;
            }
        }

        // Only check staged changes here, GitHub handles the rest
        var markers = StagedMarkers(foGitRepo);
        if (markers.Count == 0) {
            return 0;
        }

        Console.Error.WriteLine("fo-git blocked this commit. There are merge conflict markers left in the staged Spriggit text.");
        foreach (var m in markers) {
            Console.Error.WriteLine("  " + m);
        }

        return 1;
    }

    static void AfterMerge(FoGitRepo r) {
        FixCollisions(r);
        ValidateText(r, quiet: true);
        AfterTextChange(r);
    }

    static void AfterTextChange(FoGitRepo r) {
        switch (r.GetBuildState()) {
            case BuildState.UpToDate:
                return;
            case BuildState.TextOutOfDate:
                if (ResolveEdits(r, $"The Spriggit text changed, but {r.RepoConfig.Master} has changes that aren't reflected in the text.", canAbort: false) == EditChoice.Leave) {
                    Ui.Info($"The ESM wasn't built. Run \"{GenerateTextBat}\" to merge your edits with the new text, or \"{BuildEsmBat}\" to discard them.");
                }
                return;
            case BuildState.EsmNotTracked when File.Exists(r.EsmPath):
                if (ResolveEdits(r, $"{r.RepoConfig.Master} wasn't built by fo-git.", canAbort: false) == EditChoice.Leave) {
                    Ui.Info($"Run \"{GenerateTextBat}\" to keep its edits, or \"{BuildEsmBat}\" to replace it.");
                }
                return;
            default:
                BuildEsm(r);
                return;
        }
    }

    // True if the Spriggit text changed between the two commits
    static bool TextChanged(FoGitRepo r, string from, string to) {
        var (code, stdout, _) = r.GitRaw("diff", "--name-only", from, to, "--", r.RepoConfig.SpriggitFolder);
        return code != 0 || stdout.Trim().Length > 0;
    }

    static List<string> StagedMarkers(FoGitRepo r) {
        var found = new List<string>();
        var diff = r.Git("-c", "core.quotepath=off", "diff", "--cached", "-U0", "--no-color", "--", r.RepoConfig.SpriggitFolder);
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

    static int FixCollisionsCommand(FoGitRepo r) {
        if (!FixCollisions(r, again: true)) {
            Ui.Info("Nothing to fix.");
        }

        AfterTextChange(r);
        return 0;
    }

    // Runs once per merge commit unless again is set
    static bool FixCollisions(FoGitRepo r, bool again = false) {
        var head = r.Git("rev-parse", "HEAD");
        var fixedLog = Path.Combine(r.CacheDir, "fixed-merges.txt");
        if (again) {
            FoGitRepo.TryDelete(fixedLog);
        } else if (File.Exists(fixedLog) && File.ReadLines(fixedLog).Contains(head)) {
            return false;
        }

        var rep = CollisionFixer.Run(r);
        var vars = ScriptVars.FixMerge(r, rep.OldToNew);
        File.AppendAllLines(fixedLog, new[] { head });
        foreach (var w in rep.Warnings.Concat(vars.Warnings)) {
            Ui.Warn(w);
        }

        if (rep.Renumbered.Count > 0) {
            Ui.Warn("The last merge added records with the same FormIDs as new records on this branch. Renumbered the ones from this branch.");
            foreach (var n in rep.Renumbered) {
                Ui.Info("  " + n);
            }
        }
        if (vars.Fixed.Count > 0) {
            Ui.Warn("The last merge changed script variable indices referenced by conditions. Updated the conditions.");
            foreach (var n in vars.Fixed) {
                Ui.Info("  " + n);
            }
        }

        if (rep.Renumbered.Count == 0 && vars.Fixed.Count == 0) {
            return false;
        }

        Ui.Info($"Commit the changes in {r.RepoConfig.SpriggitFolder} now!");
        return true;
    }


    // For ESM changes that aren't in the Spriggit text
    static EditChoice AskAboutEdits(string problem, bool canAbort) =>
        Ui.Confirm(problem, "Discard changes in the ESM?", canAbort) switch {
            true => EditChoice.RebuildEsm,
            false => EditChoice.RegenerateText,
            null => EditChoice.Leave,
        };

    static EditChoice ResolveEdits(FoGitRepo r, string problem, bool canAbort) {
        var choice = AskAboutEdits(problem, canAbort);
        if (choice == EditChoice.RegenerateText) {
            GenerateTextCommand(r);
        } else if (choice == EditChoice.RebuildEsm) {
            BuildEsm(r, discardEdits: true);
        }

        return choice;
    }
}
