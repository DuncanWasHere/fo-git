using FoGit;
using FoGit.Repo;
using FoGit.Text;
using Mutagen.Bethesda.Fallout3;
using Mutagen.Bethesda.Plugins;

static partial class Cli {
    static int MergePlugin(FoGitRepo foGitRepo, string[] args) {
        // pluginPath is selected using PickPlugin() if not specified in the args
        var pluginPath = args.Length > 0 ? PluginFromArg(args[0]) : PickPlugin(foGitRepo);
        if (pluginPath == null) {
            return 1;
        }

        var pluginName = Path.GetFileName(pluginPath);
        var buildState = foGitRepo.GetBuildState();
        
        // ESM needs to be synced with the text tree before merging
        switch(buildState) {
            case BuildState.EsmMissing:
                Ui.Warn($"{foGitRepo.RepoConfig.Master} is missing. Build it before trying to merge plugins.");
                return 1;
            case BuildState.EsmNotTracked:
                Ui.Warn($"{foGitRepo.RepoConfig.Master} is not tracked. Generate the Spriggit text tree if missing or rebuild the ESM before trying to merge plugins.");
                return 1;
            case BuildState.EsmOutOfDate:
                Ui.Warn($"{foGitRepo.RepoConfig.Master} is out of sync with the text tree. The ESM is assumed to be out of date.");
                BuildEsm(foGitRepo);
                break;
            case BuildState.TextOutOfDate:
                Ui.Warn($"{foGitRepo.RepoConfig.Master} is out of sync with the text tree. The text is assumed to be out of date.");
                Console.Write("Press 1 to regenerate the text, and rebuild the ESM with any merged changes.");
                Console.Write("Press 2 to discard the changes to the ESM and rebuild it from the current text tree.");
                Console.Write("Press Esc to abort. ");

                var input = false;
                while (!input) {
                    var k = Console.ReadKey(true);

                    switch (k.Key) {
                        case ConsoleKey.Escape:
                            Console.WriteLine();
                            Cancelled();
                            return 1;
                        case ConsoleKey.D1:
                            GenerateTextCommand(foGitRepo);
                            Console.WriteLine(k.Key);
                            input = true;
                            break;
                        case ConsoleKey.D2:
                            BuildEsm(foGitRepo, true);
                            Console.WriteLine(k.Key);
                            input = true;
                            break;
                    }
                }

                break;
        }
        
        // Merge the plugin into the ESM using Mutagen
        Ui.Step($"Merging {pluginName} into {foGitRepo.RepoConfig.Master}");
        
        var master = PluginOps.LoadMutable(foGitRepo.EsmPath);
        var plugin = PluginOps.LoadMutable(pluginPath);
        var formIdMap = MergeMap.Load(foGitRepo, pluginName);
        var masterScriptVariables = PluginOps.ScriptVariables(master);
        
        var mergeResult = PluginOps.MergeInto(master, plugin, formIdMap);
        PrintMergeResult(mergeResult, pluginName, mergedBefore: formIdMap.Count > 0);
        
        // Now convert the merged ESM into text
        var pluginScriptVariables = PluginOps.ScriptVariables(plugin);

        SpriggitText.ChangeReport applied = null!;
        foGitRepo.WithTempDir("merge", tmpDir => {
            var tmpMaster = Path.Combine(tmpDir, foGitRepo.RepoConfig.Master);
            var loadOrder = foGitRepo.RepoConfig.Masters.Append(foGitRepo.RepoConfig.Master).Select(x => ModKey.FromFileName(x));
            
            master.WriteToBinary(tmpMaster, PluginOps.WriteParams(loadOrder));
            // Plugin might be based on an older master
            applied = GenerateText(foGitRepo, tmpMaster, baseIsCurrent: false);
        });

        Ui.Step("Checking script variables");
        var vars = ScriptVars.Fix(foGitRepo.TextTreeDir, TextOps.Scan(foGitRepo.TextTreeDir).Vars, (file, script) =>
            applied.Files.Contains(file) && pluginScriptVariables.TryGetValue(script, out var p) ? p
            : masterScriptVariables.TryGetValue(script, out var m) ? m : null);
        foreach (var f in vars.Fixed) {
            Ui.Info("  " + f);
        }
        foreach (var w in vars.Warnings) {
            Ui.Warn(w);
        }
        if (vars.Fixed.Count == 0 && vars.Warnings.Count == 0) {
            Ui.Info("  Nothing changed.");
        }

        LogDetails($"Records merged from {pluginName}", mergeResult.Details);
        LogDetails("Script variable fixes", vars.Fixed);
        foreach (var (k, v) in mergeResult.IdMap) {
            formIdMap[k] = v;
        }
        MergeMap.Save(foGitRepo, pluginName, formIdMap);

        // Finally, rebuild the master from the merged text
        ValidateText(foGitRepo);
        BuildEsm(foGitRepo);
        
        Ui.Good($"Successfully merged {pluginName} into {foGitRepo.RepoConfig.Master}");
        return 0;
    }
    
    static string PluginFromArg(string arg) {
        var path = Path.GetFullPath(arg);
        if (!File.Exists(path)) {
            throw new UserError($"Could not read file at {path}");
        }

        return path;
    }

    static void PrintMergeResult(PluginMergeResult mergeResult, string name, bool mergedBefore) {
        foreach (var e in mergeResult.Errors) {
            Ui.Error(e);
        }

        if (mergeResult.Errors.Count > 0) {
            throw new UserError($"Cancelled merging {name}. Nothing changed.");
        }

        foreach (var w in mergeResult.Warnings) {
            Ui.Warn(w);
        }

        Ui.Info($"  {mergeResult.Overrides} edited, {mergeResult.NewRecords} new, {mergeResult.Deleted} deleted record(s)");
        if (mergeResult.Renumbered > 0) {
            Ui.Info($"  {mergeResult.Renumbered} new form(s) had their FormIDs renumbered.");
        }

        if (mergedBefore) {
            Ui.Info($"  {name} was merged before. Reused the previous FormIDs.");
        }
    }

    static string? PickPlugin(FoGitRepo r) {
        var folder = r.MergePluginsFolder;
        if (!Directory.Exists(folder)) {
            throw new UserError($"The directory {folder} doesn't exist. Set \"pluginFolder\" in fo-git.local.json to wherever your new GECK plugins are saved.");
        }

        var plugins = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Equals(r.RepoConfig.Master, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        if (plugins.Count == 0) {
            throw new UserError($"No plugin files in {folder}.");
        }

        if (Console.IsInputRedirected) {
            throw new UserError("No plugin was specified.");
        }

        // So each plugin maps to a single key press
        const int pageSize = 9;
        
        int pages = (plugins.Count + pageSize - 1) / pageSize;
        int page = 0;
        
        while (true) {
            int first = page * pageSize;
            int count = Math.Min(pageSize, plugins.Count - first);
            ShowPluginPage(plugins, first, count, pages > 1 ? $"Plugins in {folder} (page {page + 1} of {pages})" : $"Plugins in {folder}");
            Console.Write(pages > 1
                ? $"Select a plugin to merge into {r.RepoConfig.Master}, left/right arrows to switch page, or press Esc to cancel. "
                : $"Select a plugin to merge into {r.RepoConfig.Master}, or Esc to cancel. ");

            int? turn = null;
            while (turn == null) {
                var k = Console.ReadKey(true);
                
                if (k.Key == ConsoleKey.Escape) {
                    Console.WriteLine();
                    Cancelled();
                    return null;
                }
                
                if (k.KeyChar >= '1' && k.KeyChar < '1' + count) {
                    Console.WriteLine(k.KeyChar);
                    return plugins[first + k.KeyChar - '1'];
                }
                
                if (pages > 1) {
                    turn = k.Key switch {
                        ConsoleKey.RightArrow or ConsoleKey.DownArrow or ConsoleKey.PageDown => 1,
                        ConsoleKey.LeftArrow or ConsoleKey.UpArrow or ConsoleKey.PageUp => -1,
                        _ => null,
                    };
                }
            }
            
            page = (page + turn.Value + pages) % pages;
            Console.WriteLine();
            Console.WriteLine();
        }
    }

    static void ShowPluginPage(List<string> plugins, int first, int count, string title) {
        Console.WriteLine(title);
        Console.WriteLine();
        
        for (var i = 0; i < count; i++) {
            var fi = new FileInfo(plugins[first + i]);
            Console.WriteLine($"  {i + 1}   {fi.Name,-45} {fi.Length / 1024.0,8:0} KB");
        }
        
        Console.WriteLine();
    }
}
