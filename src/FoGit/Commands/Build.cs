using FoGit;

static partial class Cli {
    static void BuildEsm(Repo r, bool discardEdits = false) {
        if (!Directory.Exists(r.SpriggitDir)) {
            throw new UserError($"No Spriggit files in {r.SpriggitDir}.");
        }

        if (r.EsmHasEdits()) {
            if (!discardEdits) {
                throw new UserError($"{r.Config.Master} has edits that aren't reflected in the Spriggit text. Run \"{GenerateBat}\" to keep them.");
            }

            BackupEsm(r);
        }
        var tree = r.TextTree();
        if (r.RestoreCachedEsm(tree)) {
            Ui.Step($"Restored {r.Config.Master} from an earlier build.");
        } else {
            Ui.Step($"Building {r.Config.Master} from the Spriggit text.");
            r.BuildEsm(r.SpriggitDir, r.EsmPath);
        }
        r.WriteBuildInfo(tree);
        Ui.Good($"{r.Config.Master} is up to date.");
    }

    static int BuildEsmCommand(Repo r) {
        bool discard = r.EsmHasEdits();
        if (discard) {
            Ui.Warn($"{r.Config.Master} has edits that aren't reflected in the Spriggit text, or wasn't built by fo-git. Rebuilding will discard them. A backup will be created in .fo-git/backup.");
            if (Console.IsInputRedirected) {
                throw new UserError("Won't rebuild without confirmation.");
            }

            Console.Write("Press Y to rebuild anyways, or any other key to cancel. ");
            var key = Console.ReadKey(true);
            Console.WriteLine();
            if (key.Key != ConsoleKey.Y) {
                Cancelled();
                return 1;
            }
        }
        BuildEsm(r, discard);
        return 0;
    }

    enum EditChoice {
        Generate,
        Rebuild,
        Leave,
    }

    // For ESM changes that aren't in the Spriggit text
    static EditChoice AskAboutEdits(string problem, bool canAbort) =>
        Ui.Confirm(problem, "Discard changes in the ESM?", canAbort) switch {
            true => EditChoice.Rebuild,
            false => EditChoice.Generate,
            null => EditChoice.Leave,
        };

    static EditChoice ResolveEdits(Repo r, string problem, bool canAbort) {
        var choice = AskAboutEdits(problem, canAbort);
        if (choice == EditChoice.Generate) {
            GenerateText(r);
        } else if (choice == EditChoice.Rebuild) {
            BuildEsm(r, discardEdits: true);
        }
        return choice;
    }

    static void BackupEsm(Repo r) {
        var backup = Path.Combine(r.StateDir, "backup", $"{Path.GetFileNameWithoutExtension(r.Config.Master)}-{DateTime.Now:yyyyMMdd-HHmmss}.esm");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(r.EsmPath, backup);
        Ui.Info($"{r.Config.Master} backup created in {Path.GetRelativePath(r.Root, backup)}.");
    }

    static int GenerateText(Repo r) {
        if (!File.Exists(r.EsmPath)) {
            throw new UserError($"Could not find file {r.Config.Master} to generate the Spriggit text from.");
        }

        if (!Directory.Exists(r.SpriggitDir) || !Directory.EnumerateFileSystemEntries(r.SpriggitDir).Any()) {
            return FirstGenerate(r);
        }

        switch (r.GetBuildState()) {
            case Repo.BuildState.UpToDate:
                Ui.Info($"{r.Config.Master} is identical to the Spriggit text. Nothing to do.");
                return 0;
            case Repo.BuildState.TextChanged:
                Ui.Info($"{r.Config.Master} is out of date with the Spriggit text. Rebuilding the ESM instead.");
                BuildEsm(r);
                return 0;
        }

        // ESM has edits. Copy them straight over if it was built from the current text, merge them otherwise
        var info = r.ReadBuildInfo();
        bool baseIsCurrent = info != null && info.TextHash == r.TextTree();
        if (!baseIsCurrent) {
            Ui.Info("The Spriggit text was modified after the ESM was last built. Merging.");
        }

        GenerateTextFromEsm(r, r.EsmPath, baseIsCurrent);
        if (baseIsCurrent) {
            r.WriteBuildInfo(r.TextTree());
        } else {
            BuildEsm(r, discardEdits: true);
        }

        ValidateText(r);
        Ui.Good("Done.");
        return 0;
    }

    // Round trips once since Spriggit reads float signs differently
    static int FirstGenerate(Repo r) {
        r.WithTempDir("first", tmp => {
            var pass1 = Path.Combine(tmp, "pass1");
            r.GenerateText(r.EsmPath, pass1);
            var esm = Path.Combine(tmp, r.Config.Master);
            r.BuildEsm(pass1, esm);
            r.GenerateText(esm, r.SpriggitDir);

            BackupEsm(r);
            Repo.CopyWithRetry(esm, r.EsmPath);
            r.WriteBuildInfo(r.TextTree());
        });
        ValidateText(r);
        Ui.Good($"Generated Spriggit text in {r.Config.SpriggitFolder}.");
        return 0;
    }

    static void GenerateTextFromEsm(Repo r, string esmPath, bool baseIsCurrent) {
        Ui.Step("Generating Spriggit text from the ESM");
        r.WithTempDir("text", tmp => {
            r.GenerateText(esmPath, tmp);
            Ui.Step("Updating " + r.Config.SpriggitFolder);
            var rep = SpriggitText.Apply(r, tmp, baseIsCurrent);
            Ui.Info($"  {rep.Changed} changed, {rep.Added} added, {rep.Removed} removed record files");
            ShowNotes(r, rep.Notes);
        });
    }

    static void ShowNotes(Repo r, List<string> notes) {
        if (notes.Count == 0) {
            return;
        }

        WriteLog(r, "gen-spriggit", notes);
        var clashes = notes.Where(n => n.Contains("Used the plugin version")).ToList();
        foreach (var n in clashes) {
            Ui.Warn(n);
        }

        var kept = notes.Count - clashes.Count;
        if (kept > 0) {
            Ui.Info($"  Kept the master value for {kept} field(s) where the plugin had an older value. Manual intervention may be required.");
        }
    }
}
