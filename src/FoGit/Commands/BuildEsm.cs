using FoGit;
using FoGit.Repo;

static partial class Cli {
    static int BuildEsmCommand(FoGitRepo foGitRepo) {
        var buildState = foGitRepo.GetBuildState();
        
        if (buildState == BuildState.TextOutOfDate) {
            Ui.Warn($"{foGitRepo.RepoConfig.Master} is out of sync with the text tree. Rebuilding will discard any changes not in the text.");
            Console.Write("Press Y to rebuild anyways (a backup will be created in .fo-git/backup), or any other key to cancel. ");

            if (Console.IsInputRedirected) {
                throw new UserError("Won't rebuild without confirmation.");
            }
            
            var key = Console.ReadKey(true);
            Console.WriteLine();
            
            if (key.Key != ConsoleKey.Y) {
                Cancelled();
                return 1;
            }
        }

        BuildEsm(foGitRepo, true);
        
        return 0;
    }
    
    static void BuildEsm(FoGitRepo foGitRepo, bool discardEdits = false) {
        if (!Directory.Exists(foGitRepo.TextTreeDir)) {
            throw new UserError($"No Spriggit files in {foGitRepo.TextTreeDir}.");
        }
        
        if (foGitRepo.GetBuildState() == BuildState.TextOutOfDate) {
            if (!discardEdits) {
                throw new UserError($"{foGitRepo.RepoConfig.Master} is out of sync with the text tree. The text is assumed to be out of date. Run {GenerateTextBat} to regenerate the text or {BuildEsmBat} and discard the changes.");
            }

            BackupEsm(foGitRepo);
        }
        
        var textTreeHash = foGitRepo.TextTreeHash();
        if (foGitRepo.RestoreCachedEsm(textTreeHash)) {
            Ui.Step($"Restored {foGitRepo.RepoConfig.Master} from a previous build.");
        } else {
            Ui.Step($"Building {foGitRepo.RepoConfig.Master} from the current text tree.");
            foGitRepo.BuildEsm(foGitRepo.TextTreeDir, foGitRepo.EsmPath);
        }
        
        foGitRepo.WriteBuildInfo(textTreeHash);
        Ui.Good($"{foGitRepo.RepoConfig.Master} is in sync with the text tree.");
    }

    static void BackupEsm(FoGitRepo repo) {
        var backup = Path.Combine(repo.CacheDir, "backup", $"{Path.GetFileNameWithoutExtension(repo.RepoConfig.Master)}-{DateTime.Now:yyyyMMdd-HHmmss}.esm");
        
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(repo.EsmPath, backup);
        
        Ui.Info($"{repo.RepoConfig.Master} backup created in {Path.GetRelativePath(repo.RootDir, backup)}.");
    }
}
