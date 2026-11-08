using System.Diagnostics;
using FoGit;
using Mutagen.Bethesda.Fallout3;
using Mutagen.Bethesda.Plugins;

static partial class Cli {
    static int MergePlugin(Repo r, string[] args) {
        var pluginPath = args.Length > 0 && !args[0].StartsWith("--") ? PluginFromArg(args[0]) : PickPlugin(r);
        if (pluginPath == null) {
            return 1;
        }

        var name = Path.GetFileName(pluginPath);

        // ESM needs to match the Spriggit text before merging
        if (r.EsmHasEdits()) {
            Ui.Info($"{r.Config.Master} has edits that aren't reflected in the Spriggit text. Regenerating it first!");
            GenerateText(r);
        } else if (r.GetBuildState() != Repo.BuildState.UpToDate) {
            BuildEsm(r);
        }

        Ui.Step($"Merging {name} into {r.Config.Master}");
        var sw = Stopwatch.StartNew();
        var master = PluginOps.LoadMutable(r.EsmPath);
        var plugin = PluginOps.LoadMutable(pluginPath);
        var idMap = MergeMap.Load(r, name);
        var res = PluginOps.MergeInto(master, plugin, idMap);
        ReportMerge(res, name, mergedBefore: idMap.Count > 0);

        r.WithTempDir("merge", tmp => {
            var tmpEsm = Path.Combine(tmp, r.Config.Master);
            master.WriteToBinary(tmpEsm, PluginOps.WriteParams(LoadOrder(r)));
            // Plugin might be based on an older master
            GenerateTextFromEsm(r, tmpEsm, baseIsCurrent: false);
        });

        WriteLog(r, $"merge-{Path.GetFileNameWithoutExtension(name)}", res.Details.Concat(res.Warnings.Select(w => "warning " + w)));
        foreach (var (k, v) in res.IdMap) {
            idMap[k] = v;
        }
        MergeMap.Save(r, name, idMap);

        ValidateText(r);
        BuildEsm(r, discardEdits: true);
        Ui.Good($"Merged {name} in {sw.Elapsed:m\\:ss}.");
        return 0;
    }

    static IEnumerable<ModKey> LoadOrder(Repo r) => r.Config.Masters.Append(r.Config.Master).Select(x => ModKey.FromFileName(x));

    static string PluginFromArg(string arg) {
        var path = Path.GetFullPath(arg);
        if (!File.Exists(path)) {
            throw new UserError($"Could not read file at {path}");
        }

        return path;
    }

    static void ReportMerge(PluginMergeResult res, string name, bool mergedBefore) {
        foreach (var e in res.Errors) {
            Ui.Error(e);
        }

        if (res.Errors.Count > 0) {
            throw new UserError($"Cancelled merging {name}. Nothing changed.");
        }

        foreach (var w in res.Warnings) {
            Ui.Warn(w);
        }

        Ui.Info($"  {res.Overrides} edited, {res.NewRecords} new, {res.Deleted} deleted record(s)");
        if (res.Renumbered > 0) {
            Ui.Info($"  {res.Renumbered} new form(s) had their FormIDs renumbered.");
        }

        if (mergedBefore) {
            Ui.Info($"  {name} was merged before. Reused the FormIDs.");
        }
    }

    static string? PickPlugin(Repo r) {
        var folder = r.PluginFolder;
        if (!Directory.Exists(folder)) {
            throw new UserError($"The directory {folder} doesn't exist. Set \"pluginFolder\" in fo-git.local.json to wherever your new GECK plugins are saved.");
        }

        var plugins = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).Equals(r.Config.Master, StringComparison.OrdinalIgnoreCase))
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
                ? $"Select a plugin to merge into {r.Config.Master}, left/right arrows to switch page, or press Esc to cancel. "
                : $"Select a plugin to merge into {r.Config.Master}, or Esc to cancel. ");

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
        for (int i = 0; i < count; i++) {
            var fi = new FileInfo(plugins[first + i]);
            Console.WriteLine($"  {i + 1}   {fi.Name,-45} {fi.Length / 1024.0,8:0} KB");
        }
        Console.WriteLine();
    }
}
