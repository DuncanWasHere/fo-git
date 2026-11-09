using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Mutagen.Bethesda.Fallout3;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace FoGit;

public static class Conditions {
    public static bool IsForm(int function, int firstParam, bool second) {
        if (function == (int)Condition.FunctionEnum.GetVATSValue) {
            // Weapon, weapon list, target, target list, critical effect and critical effect list
            return second && firstParam is 0 or 1 or 2 or 3 or 9 or 10;
        }
        return ConditionParams.Functions.TryGetValue(function, out var p) && (second ? p.Second : p.First);
    }

    public static bool IsKnown(int function) => ConditionParams.Functions.ContainsKey(function);

    public static bool IsVariable(int function) =>
        function is (int)Condition.FunctionEnum.GetQuestVariable or (int)Condition.FunctionEnum.GetScriptVariable;

    public static int? FunctionIndex(string name) =>
        Enum.TryParse<Condition.FunctionEnum>(name, out var f) ? (int)f : null;

    public static FormKey? Decode(int raw, IReadOnlyList<ModKey> masters, ModKey self) {
        var index = (int)((uint)raw >> 24);
        if (raw == 0 || index > masters.Count) {
            return null;
        }

        return new FormKey(index < masters.Count ? masters[index] : self, (uint)raw & 0xFFFFFF);
    }

    public static int? Encode(FormKey fk, IReadOnlyList<ModKey> masters, ModKey self) {
        int index = fk.ModKey == self ? masters.Count : masters.ToList().IndexOf(fk.ModKey);
        if (index < 0) {
            return null;
        }

        return (int)(((uint)index << 24) | fk.ID);
    }

    public static List<Condition> In(IMajorRecordGetter record) {
        var found = new List<Condition>();
        Walk(record, found, 0);
        return found;
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    private static void Walk(object o, List<Condition> found, int depth) {
        if (o is Condition c) {
            found.Add(c);
            return;
        }
        if (depth > 0 && o is IMajorRecordGetter || depth > 30) {
            return;
        }

        if (o is IEnumerable e) {
            foreach (var x in e) {
                if (x != null && Relevant(x.GetType())) {
                    Walk(x, found, depth + 1);
                }
            }
            return;
        }

        foreach (var p in Props.GetOrAdd(o.GetType(), PropsOf)) {
            object? v;
            try {
                v = p.GetValue(o);
            } catch {
                continue;
            }
            if (v != null && (v is IEnumerable || Relevant(v.GetType()))) {
                Walk(v, found, depth + 1);
            }
        }
    }

    private static bool Relevant(Type t) => !t.IsValueType && t.Namespace == typeof(Condition).Namespace;

    private static PropertyInfo[] PropsOf(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && !p.PropertyType.IsValueType && p.PropertyType != typeof(string))
            .ToArray();
}
