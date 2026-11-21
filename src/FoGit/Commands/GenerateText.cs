using FoGit;
using FoGit.Repo;
using FoGit.Text;

static partial class Cli {
    static int GenerateTextCommand(FoGitRepo foGitRepo) {
        if (!File.Exists(foGitRepo.EsmPath)) {
            throw new UserError($"Could not find file {foGitRepo.RepoConfig.Master} to generate the text tree from.");
        }

        if (!Directory.Exists(foGitRepo.TextTreeDir) || !Directory.EnumerateFileSystemEntries(foGitRepo.TextTreeDir).Any()) {
            return FirstGenerate(foGitRepo);
        }

        if (foGitRepo.GetBuildState() == BuildState.EsmOutOfDate) {
            Ui.Info(
                $"{foGitRepo.RepoConfig.Master} is out of date from the current text tree. Discard changes through Git instead.");
            return 0;
        }

        var info = foGitRepo.ReadBuildInfo();
        
        var baseIsCurrent = info != null && info.TextTreeHash == foGitRepo.TextTreeHash();
        if (!baseIsCurrent) {
            Ui.Info("The text tree was modified after the ESM was last built. Merging changes.");
        }
        
        GenerateText(foGitRepo, foGitRepo.EsmPath, baseIsCurrent);
        
        if (baseIsCurrent) {
            foGitRepo.WriteBuildInfo(foGitRepo.TextTreeHash());
        } else {
            BuildEsm(foGitRepo, discardEdits: true);
        }

        ValidateText(foGitRepo);
        Ui.Good("Done.");
        return 0;
    }

    /// Round trips once.
    /// Reason being: GECK/xEdit sometimes write floats as -0 instead of 0, and also add some eof chars to text fields.
    /// Spriggit/Mutagen read these and copy them to text, but don't write them back to the ESM.
    /// We want cleaner diffs, so we try to normalize it all at once.
    static int FirstGenerate(FoGitRepo foGitRepo) {
        foGitRepo.WithTempDir("first", tmpDir => {
            var pass1 = Path.Combine(tmpDir, "pass1");
            foGitRepo.GenerateText(foGitRepo.EsmPath, pass1);
            
            var esm = Path.Combine(tmpDir, foGitRepo.RepoConfig.Master);
            foGitRepo.BuildEsm(pass1, esm);
            foGitRepo.GenerateText(esm, foGitRepo.TextTreeDir);

            BackupEsm(foGitRepo);
            FoGitRepo.CopyWithRetry(esm, foGitRepo.EsmPath);
            foGitRepo.WriteBuildInfo(foGitRepo.TextTreeHash());
        });
        
        ValidateText(foGitRepo);
        Ui.Good($"Generated Spriggit text tree in {foGitRepo.RepoConfig.SpriggitFolder}.");
        return 0;
    }

    static SpriggitText.ChangeReport GenerateText(FoGitRepo foGitRepo, string esmPath, bool baseIsCurrent) {
        Ui.Step("Generating Spriggit text tree from the ESM");
        
        SpriggitText.ChangeReport report = null!;
        foGitRepo.WithTempDir("text", tmp => {
            foGitRepo.GenerateText(esmPath, tmp);
            Ui.Step("Updating " + foGitRepo.RepoConfig.SpriggitFolder);
            report = SpriggitText.Apply(foGitRepo, tmp, baseIsCurrent);
            Ui.Info($"  {report.Changed} changed, {report.Added} added, {report.Removed} removed record files");
            LogDetails("Changed or added record files", report.Files.Order(StringComparer.OrdinalIgnoreCase));
            LogDetails("Removed record files", report.RemovedFiles.Order(StringComparer.OrdinalIgnoreCase));
            ShowNotes(foGitRepo, report.Notes);
        });
        
        return report;
    }

    static void ShowNotes(FoGitRepo foGitRepo, List<string> notes) {
        if (notes.Count == 0) {
            return;
        }

        LogDetails("Merge notes", notes);
        var clashes = notes.Where(n => n.Contains("Used the plugin version")).ToList();
        foreach (var n in clashes) {
            Ui.Warn(n);
        }
    }
}
