using FoGit;

return Cli.Run(args);

static partial class Cli {
    const string Usage = """
        fo-git commands:
          merge-plugin [file]     Pick a plugin from the plugin folder and merge it into the master
          gen-spriggit            Generate the Spriggit text from the ESM
          build-esm               Build the ESM from the Spriggit text
          setup                   Download Spriggit and build the ESM

          status                  Show whether the ESM matches the Spriggit text
          validate [--text] [--esm] [--round-trip] [--accept-current]
                                  Look for FormID collisions, missing records and conflict markers.
                                  --round-trip generates Spriggit text from the ESM and compares it with the input text.
                                  --accept-current adds found issues to the known issues file.
          fix-collisions          Rerun the FormID collision fix for the merge commit at HEAD

          hook <name> [args]      Git hooks (pre-commit, post-merge, post-commit, post-checkout, post-rewrite)
          merge-driver %O %A %B %P

          version                 Print the fo-git version

        --window keeps the window open if there are warnings and closes it otherwise.
        """;

    const string BuildBat = "Build ESM.bat";
    const string GenerateBat = "Generate Spriggit Text.bat";

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

        var cmd = args[0];
        var rest = args[1..];
        int code;
        try {
            if (cmd == "merge-driver") {
                return YamlMerge.RunDriver(rest[0], rest[1], rest[2], rest.ElementAtOrDefault(3) ?? rest[1]);
            }

            var repo = Repo.Find();
            if (cmd != "hook") {
                EnsureGitConfig(repo);
            }

            code = cmd switch {
                "merge-plugin" => MergePlugin(repo, rest),
                "gen-spriggit" => GenerateText(repo),
                "build-esm" => BuildEsmCommand(repo),
                "setup" => Setup(repo),
                "status" => Status(repo),
                "validate" => Validate(repo, rest),
                "fix-collisions" => FixCollisionsCommand(repo),
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

    static readonly (string key, string value)[] GitSettings = {
        ("core.hooksPath", "Tools/githooks"),
        ("merge.fo-git.name", "fo-git record merge for Spriggit text"),
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
            Ui.Warn($"{r.Config.Master} has edits that aren't reflected in the Spriggit text. Run \"{GenerateBat}\" to keep them, or \"{BuildBat}\" to discard them.");
        } else {
            BuildEsm(r, discardEdits: true);
        }

        Ui.Good("Setup complete.");
        return 0;
    }

    static int Status(Repo r) {
        Ui.Info($"Repo           {r.Root}");
        Ui.Info($"Branch         {r.Git("branch", "--show-current")}");
        Ui.Info($"Plugin folder  {r.PluginFolder}");
        var info = r.ReadBuildInfo();
        Ui.Info($"{r.Config.Master,-14} " + r.GetBuildState() switch {
            Repo.BuildState.Missing => $"not built by fo-git. Run {BuildBat}.",
            Repo.BuildState.UpToDate => $"matches the Spriggit text (built {info!.BuiltAt:g})",
            Repo.BuildState.TextChanged => $"older than the Spriggit text. Run {BuildBat}.",
            Repo.BuildState.EsmEdited => $"has edits that aren't reflected in the Spriggit text. Run {GenerateBat}.",
            _ => "",
        });
        return 0;
    }

    static void Cancelled() => Ui.Info("Cancelled. Nothing changed.");

    static string WriteLog(Repo r, string name, IEnumerable<string> lines) {
        var log = Path.Combine(r.StateDir, "logs", $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, lines);
        return log;
    }
}
