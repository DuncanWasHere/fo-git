using System.Buffers.Binary;

namespace FoGit;

/// <summary>
/// This is a monument to Mutagen's bugs for FNV.
/// Rough and dirty fixer to do the following:
/// Use xEdit group and form order (also fixing PNAMs)
/// Include groups in plugin record count (idk if this actually matters but we'll be consistent with xEdit)
/// </summary>
public static class EsmSorter {
    // xEdit order
    static readonly string[] TopGroupOrder = [
        "GMST", "TXST", "MICN", "GLOB", "CLAS", "FACT", "HDPT", "HAIR", "EYES", "RACE", "SOUN", "ASPC", "MGEF", "SCPT", "LTEX",
        "ENCH", "SPEL", "ACTI", "TACT", "TERM", "ARMO", "BOOK", "CONT", "DOOR", "INGR", "LIGH", "MISC", "CHIP", "STAT", "SCOL",
        "MSTT", "PWAT", "GRAS", "TREE", "FURN", "WEAP", "AMMO", "NPC_", "CREA", "LVLC", "LVLN", "KEYM", "ALCH", "IDLM", "NOTE",
        "COBJ", "PROJ", "LVLI", "WTHR", "CLMT", "REGN", "NAVI", "CELL", "WRLD", "DIAL", "QUST", "IDLE", "PACK", "CSTY", "LSCR",
        "ANIO", "WATR", "EFSH", "EXPL", "DEBR", "IMGS", "IMAD", "FLST", "PERK", "BPTD", "ADDN", "AVIF", "RADS", "CAMS", "CPTH",
        "VTYP", "IPCT", "IPDS", "ARMA", "ECZN", "MESG", "RGDL", "DOBJ", "LGTM", "MUSC", "IMOD", "REPU", "RCPE", "RCCT", "CSNO",
        "LSCT", "MSET", "ALOC", "CHAL", "AMEF", "CCRD", "CMNY", "CDCK", "DEHY", "HUNG", "SLPD",
    ];

    const uint Group = 0x50555247; // "GRUP"
    const uint CompressedFlag = 0x00040000;

    sealed class Node {
        public required byte[] Data;
        public uint Sig;
        public uint FormId;               // records
        public uint Label;                // groups
        public int GroupType = -1;        // groups
        public List<Node>? Children;      // groups
        public bool IsGroup => Children != null;
    }

    // A record with a child group (CELL, WRLD, DIAL)
    sealed record Unit(Node First, Node? Second) {
        public IEnumerable<Node> Nodes => Second == null ? [First] : [First, Second];
    }

    public static void SortFile(string path) {
        var sorted = Sort(File.ReadAllBytes(path));
        File.WriteAllBytes(path, sorted);
    }

    public static byte[] Sort(byte[] data) {
        var root = Parse(data, 0, data.Length);
        if (root.Count == 0 || root[0].IsGroup || Sig(root[0].Sig) != "TES4") {
            throw new InvalidDataException("Not a plugin file");
        }

        var tes4 = root[0];
        var groups = root.Skip(1).ToList();
        groups = groups.OrderBy(g => TopIndex(g)).ToList();
        foreach (var g in groups) {
            SortChildren(g);
        }

        FixRecordCount(tes4, CountAll(groups));

        using var ms = new MemoryStream(data.Length);
        ms.Write(tes4.Data);
        foreach (var g in groups) {
            Write(ms, g);
        }

        if (ms.Length != data.Length) {
            throw new InvalidDataException("Sorting changed the plugin size");
        }

        return ms.ToArray();
    }

    static int TopIndex(Node g) {
        var i = Array.IndexOf(TopGroupOrder, Sig(g.Label));
        return i < 0 ? TopGroupOrder.Length : i;
    }

    static List<Node> Parse(byte[] d, int off, int end) {
        var list = new List<Node>();
        while (off < end) {
            var sig = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 4));
            if (sig == Group) {
                list.Add(new Node {
                    Data = d[off..(off + 24)],
                    Sig = sig,
                    Label = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 8)),
                    GroupType = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off + 12)),
                    Children = Parse(d, off + 24, off + size),
                });
                off += size;
            } else {
                list.Add(new Node {
                    Data = d[off..(off + 24 + size)],
                    Sig = sig,
                    FormId = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(off + 12)),
                });
                off += 24 + size;
            }
        }
        return list;
    }

    static void SortChildren(Node g) {
        var kids = g.Children!;
        foreach (var k in kids.Where(k => k.IsGroup)) {
            SortChildren(k);
        }

        IEnumerable<Node> sorted = g.GroupType switch {
            // Top group, interior sub-block, exterior sub-block
            0 or 3 or 5 => Units(kids).OrderBy(u => u.First.IsGroup ? 1 : 0).ThenBy(u => u.First.FormId).SelectMany(u => u.Nodes),
            // World children
            1 => Units(kids).OrderBy(u => u.First.IsGroup ? 1 : 0).ThenBy(u => u.First.IsGroup ? u.First.Label : u.First.FormId).SelectMany(u => u.Nodes),
            // Interior and exterior blocks
            2 or 4 => kids.OrderBy(k => k.Label),
            // Cell children
            6 => kids.OrderBy(k => k.GroupType),
            // Topic children
            7 => InfoOrder(kids),
            // Cell contents
            8 or 9 or 10 => kids.OrderBy(k => Sig(k.Sig) switch { "LAND" => 0, "PGRD" => 1, "NAVM" => 2, _ => 3 }).ThenBy(k => k.FormId),
            _ => kids,
        };
        g.Children = sorted.ToList();
    }

    static List<Unit> Units(List<Node> kids) {
        var units = new List<Unit>();
        for (int i = 0; i < kids.Count; i++) {
            var k = kids[i];
            var next = i + 1 < kids.Count ? kids[i + 1] : null;
            if (!k.IsGroup && next is { IsGroup: true, GroupType: 1 or 6 or 7 } && next.Label == k.FormId) {
                units.Add(new Unit(k, next));
                i++;
            } else {
                units.Add(new Unit(k, null));
            }
        }
        return units;
    }

    /// INFOs are ordered according to PNAM
    static IEnumerable<Node> InfoOrder(List<Node> infos) {
        var byId = infos.Where(i => !i.IsGroup).ToDictionary(i => i.FormId);
        var prev = new Dictionary<uint, uint?>();
        foreach (var i in byId.Values) {
            var p = PreviousInfo(i);
            if (p == null && (BinaryPrimitives.ReadUInt32LittleEndian(i.Data.AsSpan(8)) & CompressedFlag) != 0) {
                // Can't read compressed records without decompressing, keep the order Mutagen wrote
                return infos;
            }
            prev[i.FormId] = p;
        }

        var after = new Dictionary<uint, List<Node>>();
        var heads = new List<Node>();
        foreach (var i in byId.Values) {
            if (prev[i.FormId] is { } p && p != i.FormId && byId.ContainsKey(p)) {
                (after.TryGetValue(p, out var l) ? l : after[p] = new()).Add(i);
            } else {
                heads.Add(i);
            }
        }

        var result = new List<Node>(infos.Count);
        var seen = new HashSet<uint>();
        void Visit(Node n) {
            if (!seen.Add(n.FormId)) {
                return;
            }
            result.Add(n);
            if (after.TryGetValue(n.FormId, out var next)) {
                foreach (var c in next.OrderBy(c => c.FormId)) {
                    Visit(c);
                }
            }
        }
        foreach (var h in heads.OrderBy(h => h.FormId)) {
            Visit(h);
        }
        // Cycle loops
        foreach (var i in infos.Where(i => !i.IsGroup && !seen.Contains(i.FormId))) {
            Visit(i);
        }
        result.AddRange(infos.Where(i => i.IsGroup));
        return result;
    }

    static uint? PreviousInfo(Node info) {
        var d = info.Data;
        if ((BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8)) & CompressedFlag) != 0) {
            return null;
        }

        int o = 24;
        while (o + 6 <= d.Length) {
            var sig = Sig(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o)));
            var len = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o + 4));
            if (sig == "PNAM" && len == 4) {
                return BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o + 6));
            }
            o += 6 + len;
        }
        return 0;
    }

    static int CountAll(IEnumerable<Node> nodes) => nodes.Sum(n => 1 + (n.Children == null ? 0 : CountAll(n.Children)));

    /// HEDR count is every record and group except TES4
    static void FixRecordCount(Node tes4, int count) {
        var d = tes4.Data;
        if (Sig(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(24))) == "HEDR" && BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28)) >= 8) {
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(34), count);
        }
    }

    static void Write(Stream s, Node n) {
        s.Write(n.Data);
        if (n.Children != null) {
            foreach (var c in n.Children) {
                Write(s, c);
            }
        }
    }

    static string Sig(uint v) => string.Create(4, v, (span, x) => {
        for (int i = 0; i < 4; i++) {
            span[i] = (char)((x >> (8 * i)) & 0xFF);
        }
    });
}
