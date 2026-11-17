using FoGit;
using FoGit.Repo;
using YamlMerge = FoGit.Text.YamlMerge;

return Cli.Run(args);

static partial class Cli {
    enum EditChoice {
        RegenerateText,
        RebuildEsm,
        Leave,
    }
    
    const string Usage = """
        fo-git commands:
          merge-plugin [file]     Pick a plugin from the plugin folder and merge it into the master
          gen-text                Generate the Spriggit text tree from the ESM
          build-esm               Build the ESM from the Spriggit text tree

          status                  Show whether the ESM matches the Spriggit text
          validate [--text] [--esm] [--round-trip]
                                  Look for FormID collisions, missing records and conflict markers.
                                  --round-trip generates Spriggit text from the ESM and compares it with the input text.
          fix-collisions          Rerun the FormID collision fix for the merge commit at HEAD

          hook <name> [args]      Git hooks (pre-commit, post-merge, post-commit, post-checkout, post-rewrite)
          merge-driver %O %A %B %P

          version                 Print the fo-git version

        --window keeps the window open if there are warnings and closes it otherwise.
        """;

    const string BuildEsmBat = "Build ESM.bat";
    const string GenerateTextBat = "Generate Text.bat";

    static readonly (string key, string value)[] GitSettings = {
        ("core.hooksPath", "Tools/githooks"),
        ("merge.fo-git.name", "fo-git record merge for Spriggit text tree"),
        ("merge.fo-git.driver", "Tools/fo-git.exe merge-driver %O %A %B %P"),
        ("core.longpaths", "true"),
        ("core.safecrlf", "false"),
        // Collision fix needs merge commits with the local branch as the first parent
        ("pull.rebase", "false"),
    };

    public static int Run(string[] args) {
        // This flag is useless on its own, so we remove it from the args after it's parsed
        Ui.Window = args.Contains("--window");
        args = args.Where(a => a != "--window").ToArray();
        
        // Print usage if help or no args are passed
        if (args.Length == 0 || args[0] is "-h" or "--help") {
            Console.WriteLine(Usage);
            return 0;
        }

        if (args[0] is "version") {
            Console.WriteLine(typeof(Cli).Assembly.GetName().Version?.ToString(3));
            return 0;
        }
        
        // Now we parse actual commands
        int exitCode;

        var command = args[0];
        var commandParams = args[1..];
        
        try {
            // Called by Git automatically during merge commits
            if (command == "merge-driver") {
                return YamlMerge.RunDriver(commandParams[0], commandParams[1], commandParams[2], commandParams.ElementAtOrDefault(3) ??
                    commandParams[1]);
            }

            var repo = FoGitRepo.FindRepo();
            if (command != "hook") {
                EnsureGitConfig(repo);
            }

            exitCode = command switch {
                "merge-plugin" => MergePlugin(repo, commandParams),
                "gen-text" => GenerateTextCommand(repo),
                "build-esm" => BuildEsmCommand(repo),
                "status" => Status(repo),
                "validate" => Validate(repo, commandParams),
                "fix-collisions" => FixCollisionsCommand(repo),
                "hook" => Hook(repo, commandParams),
                _ => throw new UserError($"Unknown command {command}.\n\n{Usage}"),
            };
        } catch (UserError e) {
            Ui.Error(e.Message);
            exitCode = 2;
        } catch (Exception e) {
            Ui.Error("fo-git crashed.\n" + e);
            exitCode = 3;
        }
        
        Ui.Finish();
        return exitCode;
    }

    static int Status(FoGitRepo foGitRepo) {
        Ui.Info($"Repo                  {foGitRepo.RootDir}");
        Ui.Info($"Branch                {foGitRepo.Git("branch", "--show-current")}");
        Ui.Info($"Merge plugins folder  {foGitRepo.MergePluginsFolder}");
        
        var info = foGitRepo.ReadBuildInfo();
        Ui.Info($"{foGitRepo.RepoConfig.Master,-14} " + foGitRepo.GetBuildState() switch {
            BuildState.EsmMissing => $"not built.",
            BuildState.EsmNotTracked => $"not tracked. It has not yet been rebuilt with fo-git.",
            BuildState.UpToDate => $"is in sync with the current text tree (built {info!.BuiltAt:g})",
            BuildState.EsmOutOfDate => $"is out of sync with the current text tree. The ESM is assumed to be out of date.",
            BuildState.TextOutOfDate => $"is out of sync the current text tree. The text is assumed to be out of date.",
            _ => "",
        });
        
        Ui.Info($"ESM cache      {foGitRepo.CachedEsmCount} of {foGitRepo.LocalConfig.EsmCacheSize} build(s)");
        
        return 0;
    }

    /// <summary>
    /// Ensure hooks and merge driver are registered in the repo's git config.
    /// </summary>
    static void EnsureGitConfig(FoGitRepo foGitRepo) {
        var changed = false;

        foreach (var (key, value) in GitSettings) {
            var (code, current, _) = foGitRepo.GitRaw("config", "--local", "--get", key);
            if (code == 0 && current.Trim() == value) {
                continue;
            }

            foGitRepo.Git("config", "--local", key, value);
            changed = true;
        }

        if (changed) {
            Ui.Info("Enabled git hooks and merge driver for this local repo.");
        }
    }

    static void Cancelled() => Ui.Info("Cancelled. Nothing changed.");

    static void WriteLog(FoGitRepo foGitRepo, string name, IEnumerable<string> lines) {
        var log = Path.Combine(foGitRepo.CacheDir, "logs", $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, lines);
    }
}
