using Mutagen.Bethesda;
using Mutagen.Bethesda.Fallout3;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;

namespace FoGit;

public sealed class PluginMergeResult {
    public int Overrides;
    public int NewRecords;
    public int Renumbered;
    public int Deleted;
    public readonly Dictionary<uint, uint> IdMap = new();
    public readonly List<string> Warnings = new();
    public readonly List<string> Errors = new();
    public readonly List<string> Details = new();
}

public static class PluginOps {
    private static readonly Cell.TranslationMask CellFieldsOnly = new(true) {
        Persistent = false,
        Temporary = false,
        Landscape = false,
        VisibleWhenDistant = false,
        NavigationMeshes = false,
    };
    private static readonly Worldspace.TranslationMask WorldspaceFieldsOnly = new(true) {
        SubCells = false,
        TopCell = false,
    };
    private static readonly DialogTopic.TranslationMask TopicFieldsOnly = new(true) {
        Responses = false,
    };

    public const uint FirstFreeId = 0x800;

    private static bool IsContainer(IMajorRecordGetter r) => r is ICellGetter or IWorldspaceGetter or IDialogTopicGetter;

    public static Fallout3Mod LoadMutable(string path) =>
        Fallout3Mod.CreateFromBinary(path, Fallout3Release.FalloutNV);

    public static IFallout3ModDisposableGetter LoadOverlay(string path) =>
        Fallout3Mod.CreateFromBinaryOverlay(path, Fallout3Release.FalloutNV);

    public static BinaryWriteParameters WriteParams(IEnumerable<ModKey> loadOrder) => new() {
        MastersListOrdering = new MastersListOrderingByLoadOrder(loadOrder),
        OverriddenFormsOption = OverriddenFormsOption.Iterate,
        FormIDCompaction = FormIDCompactionOption.NoCheck,
    };

    // Merges the plugin into the master in memory
    public static PluginMergeResult MergeInto(Fallout3Mod master, Fallout3Mod plugin, IReadOnlyDictionary<uint, uint> previousIds) {
        var res = new PluginMergeResult();
        var masterKey = master.ModKey;
        var pluginKey = plugin.ModKey;
        var validOwners = master.ModHeader.MasterReferences.Select(m => m.Master).Append(masterKey).ToHashSet();

        // Can't merge records from a plugin that isn't one of the master's masters
        foreach (var r in plugin.EnumerateMajorRecords()) {
            var owner = r.FormKey.ModKey;
            if (owner != pluginKey && !validOwners.Contains(owner)) {
                res.Errors.Add($"{Describe(r)} is added by {owner}, and {masterKey} can't have {owner} as a master.");
            }
        }
        if (res.Errors.Count > 0) {
            return res;
        }

        // Skip new deleted records
        var createdAndDeleted = plugin.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == pluginKey && r.IsDeleted).Select(r => r.FormKey).ToHashSet();
        if (createdAndDeleted.Count > 0) {
            plugin.Remove(createdAndDeleted);
        }

        // Renumber formIDs for plugin-added forms
        var masterTypes = new Dictionary<uint, Type>();
        foreach (var r in master.EnumerateMajorRecords()) {
            if (r.FormKey.ModKey == masterKey) {
                masterTypes[r.FormKey.ID] = r.GetType();
            }
        }

        var usedIds = masterTypes.Keys.ToHashSet();
        uint nextId = usedIds.DefaultIfEmpty(FirstFreeId - 1).Max() + 1;
        var map = new Dictionary<FormKey, FormKey>();
        var remerged = new HashSet<FormKey>();
        var ownRecords = plugin.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == pluginKey).OrderBy(r => r.FormKey.ID).ToList();
        var assigned = new HashSet<uint>();
        foreach (var r in ownRecords) {
            var id = r.FormKey.ID;
            uint target;
            if (previousIds.TryGetValue(id, out var prev) && masterTypes.TryGetValue(prev, out var t) && t == r.GetType() && assigned.Add(prev)) {
                target = prev;
                remerged.Add(new FormKey(masterKey, prev));
            } else if (id >= FirstFreeId && !usedIds.Contains(id)) {
                target = id;
            } else {
                while (usedIds.Contains(nextId)) {
                    nextId++;
                }

                target = nextId++;
                res.Renumbered++;
                res.Details.Add($"renumbered {r.FormKey} to {target:X6} ({r.EditorID ?? r.GetType().Name})");
            }
            usedIds.Add(target);
            assigned.Add(target);
            map[r.FormKey] = new FormKey(masterKey, target);
            res.IdMap[id] = target;
        }
        if (map.Count > 0) {
            Rekey(plugin, ownRecords, map);
            plugin.RemapLinks(map);
        }

        // Links to plugins that aren't masters would break the master
        foreach (var r in plugin.EnumerateMajorRecords()) {
            foreach (var l in r.EnumerateFormLinks()) {
                if (!l.IsNull && !validOwners.Contains(l.FormKey.ModKey)) {
                    res.Errors.Add($"{Describe(r)} references {l.FormKey}, but {l.FormKey.ModKey} isn't a master of {masterKey}.");
                }
            }
        }

        if (res.Errors.Count > 0) {
            return res;
        }

        MergeInfoMaps(master, plugin, res);

        // Copy records into the master
        var existing = master.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
        var newKeys = map.Values.Where(k => !remerged.Contains(k)).ToHashSet();
        var cache = plugin.ToImmutableLinkCache();
        var contexts = plugin.EnumerateMajorRecordContexts<IMajorRecord, IMajorRecordGetter>(cache).ToList();

        var replace = new HashSet<FormKey>();
        var delete = new HashSet<FormKey>();
        foreach (var ctx in contexts) {
            var rec = ctx.Record;
            if (rec.IsDeleted && rec.FormKey.ModKey == masterKey) {
                delete.Add(rec.FormKey);
                continue;
            }
            if (!IsContainer(rec) && existing.Contains(rec.FormKey)) {
                replace.Add(rec.FormKey);
            }
        }
        // Remove replaced records first
        var remove = new HashSet<FormKey>(replace);
        remove.UnionWith(delete);
        if (remove.Count > 0) {
            master.Remove(remove);
        }

        foreach (var k in delete.Where(existing.Contains)) {
            res.Deleted++;
            res.Details.Add($"deleted {k}");
        }

        foreach (var ctx in contexts) {
            var rec = ctx.Record;
            if (delete.Contains(rec.FormKey)) {
                continue;
            }

            var target = ctx.GetOrAddAsOverride(master);
            if (IsContainer(rec) && existing.Contains(rec.FormKey)) {
                switch (target, rec) {
                    case (ICell c, ICellGetter g):
                        c.DeepCopyIn(g, CellFieldsOnly);
                        break;
                    case (IWorldspace w, IWorldspaceGetter g):
                        w.DeepCopyIn(g, WorldspaceFieldsOnly);
                        break;
                    case (IDialogTopic d, IDialogTopicGetter g):
                        d.DeepCopyIn(g, TopicFieldsOnly);
                        break;
                }
            }
            if (newKeys.Contains(rec.FormKey)) {
                res.NewRecords++;
                res.Details.Add($"new {rec.FormKey} ({rec.EditorID ?? rec.GetType().Name})");
            } else {
                res.Overrides++;
                if (rec.FormKey.ModKey == masterKey && !existing.Contains(rec.FormKey)) {
                    res.Warnings.Add($"{Describe(rec)} was deleted from the master, but it's overridden in {pluginKey}. The form was undeleted, manual intervention may be required.");
                }
            }
        }

        FixCellBlocks(master);
        return res;
    }

    // Per-field merge for infomaps
    private static void MergeInfoMaps(Fallout3Mod master, Fallout3Mod plugin, PluginMergeResult res) {
        var maps = plugin.NavigationMeshInfoMaps.Records.ToList();
        var target = master.NavigationMeshInfoMaps.Records.FirstOrDefault();
        if (maps.Count == 0 || target == null) {
            return;
        }

        var deletedNavmeshes = plugin.EnumerateMajorRecords().OfType<INavigationMeshGetter>()
            .Where(n => n.IsDeleted).Select(n => n.FormKey).ToHashSet();
        var infos = target.Infos.Where(i => !deletedNavmeshes.Contains(i.NavigationMesh.FormKey))
            .GroupBy(i => i.NavigationMesh.FormKey).ToDictionary(g => g.Key, g => g.Last());
        var connections = target.Connections.Where(c => !deletedNavmeshes.Contains(c.NavigationMesh.FormKey))
            .GroupBy(c => c.NavigationMesh.FormKey).ToDictionary(g => g.Key, g => g.Last());
        var updated = new HashSet<FormKey>();
        foreach (var map in maps) {
            foreach (var i in map.Infos) {
                infos[i.NavigationMesh.FormKey] = i;
                updated.Add(i.NavigationMesh.FormKey);
            }
            foreach (var c in map.Connections) {
                connections[c.NavigationMesh.FormKey] = c;
                updated.Add(c.NavigationMesh.FormKey);
            }
        }

        // Sorted by FormID
        var modOrder = master.ModHeader.MasterReferences.Select(m => m.Master).Append(master.ModKey).ToList();
        (int, uint) Order(FormKey k) {
            var idx = modOrder.IndexOf(k.ModKey);
            return (idx < 0 ? int.MaxValue : idx, k.ID);
        }
        target.Infos.SetTo(infos.OrderBy(kv => Order(kv.Key)).Select(kv => kv.Value));
        target.Connections.SetTo(connections.OrderBy(kv => Order(kv.Key)).Select(kv => kv.Value));

        plugin.Remove(maps.Select(m => m.FormKey).ToHashSet());
        res.Overrides++;
        res.Details.Add($"merged navmesh info for {updated.Count} navmeshes into {target.FormKey}");
    }

    private static void Rekey(Fallout3Mod mod, List<IMajorRecord> recs, Dictionary<FormKey, FormKey> map) {
        var topLevel = new List<(IMajorRecord rec, IGroup group)>();
        foreach (var rec in recs) {
            var group = mod.TryGetTopLevelGroup(rec.GetType());
            if (group != null && group.ContainsKey(rec.FormKey)) {
                topLevel.Add((rec, group));
            }
        }
        if (topLevel.Count > 0) {
            mod.Remove(topLevel.Select(x => x.rec.FormKey).ToHashSet());
        }

        foreach (var rec in recs) {
            ((IMajorRecordInternal)rec).FormKey = map[rec.FormKey];
        }

        foreach (var (rec, group) in topLevel) {
            group.SetUntyped(rec);
        }
    }

    public static void FixCellBlocks(Fallout3Mod mod) {
        var misplaced = new List<(Cell cell, CellBlock block, CellSubBlock sub)>();
        foreach (var b in mod.Cells.Records) {
            foreach (var s in b.SubBlocks) {
                foreach (var c in s.Cells) {
                    var id = c.FormKey.ID;
                    if (b.BlockNumber != (int)(id % 10) || s.BlockNumber != (int)(id / 10 % 10)) {
                        misplaced.Add((c, b, s));
                    }
                }
            }
        }

        foreach (var (cell, oldBlock, oldSub) in misplaced) {
            var id = cell.FormKey.ID;
            int blockNum = (int)(id % 10), subNum = (int)(id / 10 % 10);
            oldSub.Cells.Remove(cell);
            var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockNum);
            if (block == null) {
                block = new CellBlock { BlockNumber = blockNum, GroupType = oldBlock.GroupType };
                mod.Cells.Records.Add(block);
            }
            var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subNum);
            if (sub == null) {
                sub = new CellSubBlock { BlockNumber = subNum, GroupType = oldSub.GroupType };
                block.SubBlocks.Add(sub);
            }
            sub.Cells.Add(cell);
            if (oldSub.Cells.Count == 0) {
                oldBlock.SubBlocks.Remove(oldSub);
            }

            if (oldBlock.SubBlocks.Count == 0) {
                mod.Cells.Records.Remove(oldBlock);
            }
        }
    }

    public sealed class ValidationReport {
        public readonly List<string> Errors = new();
        public readonly List<string> Warnings = new();
    }

    public static ValidationReport ValidateEsm(IFallout3ModGetter master, IReadOnlyList<string> masterSearchPaths) {
        var rep = new ValidationReport();
        var masterKey = master.ModKey;
        var declared = master.ModHeader.MasterReferences.Select(m => m.Master).ToHashSet();

        var counts = new Dictionary<FormKey, int>();
        foreach (var r in master.EnumerateMajorRecords()) {
            counts[r.FormKey] = counts.GetValueOrDefault(r.FormKey) + 1;
        }

        foreach (var (k, n) in counts.Where(x => x.Value > 1)) {
            rep.Errors.Add($"Duplicate FormID {k} appears {n} times.");
        }

        var own = counts.Keys.ToHashSet();

        var baseGameKeys = new Dictionary<ModKey, HashSet<FormKey>>();
        foreach (var m in declared) {
            var p = masterSearchPaths.Select(d => Path.Combine(d, m.FileName)).FirstOrDefault(File.Exists);
            if (p == null) {
                continue;
            }

            using var bm = LoadOverlay(p);
            baseGameKeys[m] = bm.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
        }
        if (baseGameKeys.Count < declared.Count) {
            rep.Warnings.Add($"Couldn't find base game masters ({string.Join(", ", declared.Where(d => !baseGameKeys.ContainsKey(d)))}). " +
                             "Add your FalloutNV Data folder to masterSearchPaths in fo-git.local.json");
        }

        bool Exists(FormKey fk) {
            if (own.Contains(fk)) {
                return true;
            }

            if (fk.ModKey == masterKey) {
                return false;
            }

            if (fk.ID < FirstFreeId) {
                return true; // engine forms like PlayerREF
            }

            if (baseGameKeys.TryGetValue(fk.ModKey, out var set)) {
                return set.Contains(fk);
            }

            return true; // master isn't loaded LOL
        }

        // One error per missing record. Containers also list their children's links
        var missing = new Dictionary<FormKey, (List<string> referrers, string type)>();
        foreach (var r in master.EnumerateMajorRecords()) {
            bool container = IsContainer(r);
            foreach (var l in r.EnumerateFormLinks()) {
                if (l.IsNull) {
                    continue;
                }

                var fk = l.FormKey;
                if (fk.ModKey != masterKey && !declared.Contains(fk.ModKey)) {
                    rep.Errors.Add($"{Describe(r)} references {fk} in {fk.ModKey}, which isn't a master.");
                    continue;
                }
                if (Exists(fk)) {
                    continue;
                }

                if (!missing.TryGetValue(fk, out var entry)) {
                    missing[fk] = entry = (new List<string>(), l.Type.Name.TrimStart('I').Replace("Getter", ""));
                }

                if (!container) {
                    entry.referrers.Insert(0, Describe(r));
                } else {
                    entry.referrers.Add(Describe(r));
                }
            }
        }
        foreach (var (fk, (refs, type)) in missing.Take(500)) {
            rep.Errors.Add($"Dangling reference. {fk} ({type}) doesn't exist, but it's referenced by {string.Join(", ", refs.Distinct().Take(3))}{(refs.Count > 3 ? " and others" : "")}.");
        }

        if (missing.Count > 500) {
            rep.Errors.Add($"There are {missing.Count - 500} more missing records.");
        }

        foreach (var g in master.EnumerateMajorRecords()
                     .Where(r => r.FormKey.ModKey == masterKey && !string.IsNullOrEmpty(r.EditorID))
                     .GroupBy(r => r.EditorID!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1)) {
            rep.Warnings.Add($"EditorID {g.Key} is used by multiple records ({string.Join(", ", g.Select(x => x.FormKey.ToString()))}).");
        }
        return rep;
    }

    public static string Describe(IMajorRecordGetter r) =>
        $"{r.GetType().Name} {r.FormKey}{(string.IsNullOrEmpty(r.EditorID) ? "" : $" ({r.EditorID})")}";
}
