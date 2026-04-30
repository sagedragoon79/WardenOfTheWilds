using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

// ─────────────────────────────────────────────────────────────────────────────
//  SmokehouseDiagnostics
//
//  One-shot discovery dump to identify the patch points needed to ENFORCE
//  the Smokehouse work radius (workers refuse out-of-radius sources).
//
//  We need to confirm three things from a real Assembly-CSharp.dll runtime:
//
//  1. Methods on `SmokeHouse` — specifically the work-availability gate.
//     S&S inferred `CheckWorkAvailability` exists; we need to confirm name
//     and return type. If it returns bool, we can prefix-override. If void,
//     we need a different gate (the search entry intercept).
//
//  2. WorkBucketIdentifier enum values. S&S guessed `SmokeHouseNeedsWorker`
//     exists but never confirmed. Knowing the exact enum integer value lets
//     us call the bucket-suppression method by reflection without coupling
//     to a brittle field/method signature.
//
//  3. Search entry classes related to smokehouse / raw meat. Names like
//     `CollectRawMeatSearchEntry` or `SmokeHouseCollectInputSearchEntry`
//     are guesses; the dump will list every type whose name contains
//     "SearchEntry" + ("Smoke" || "Meat" || "Raw" || "Fish") so we can
//     pick the real one.
//
//  Output: log lines under [WotW] [SmokeDiag] prefix. Once we have the
//  dump from a reload we can implement enforcement (Phase 2) without
//  guessing.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Systems
{
    public static class SmokehouseDiagnostics
    {
        private static bool _dumped = false;
        private const string TAG = "[WotW] [SmokeDiag]";

        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public static void OnMapLoaded()
        {
            if (_dumped) return;
            _dumped = true;

            try
            {
                MelonLogger.Msg($"{TAG} ─── BEGIN dump ───────────────────────────────────");

                // ── 1. SmokeHouse type discovery ─────────────────────────────
                Type? smokeType = FindType("SmokeHouse") ?? FindType("Smokehouse");
                if (smokeType != null)
                {
                    MelonLogger.Msg($"{TAG} SmokeHouse type: {smokeType.FullName}");
                    MelonLogger.Msg($"{TAG}   base chain: {DescribeBaseChain(smokeType)}");

                    DumpMethods(smokeType, "SmokeHouse");
                    DumpFields(smokeType, "SmokeHouse");
                }
                else
                {
                    MelonLogger.Warning($"{TAG} SmokeHouse type not found in any loaded assembly.");
                }

                // ── 2. WorkBucketIdentifier enum values ──────────────────────
                Type? wbiType = FindType("WorkBucketIdentifier");
                if (wbiType != null && wbiType.IsEnum)
                {
                    MelonLogger.Msg($"{TAG} WorkBucketIdentifier values:");
                    foreach (var name in Enum.GetNames(wbiType))
                    {
                        // Filter to smoke-relevant enum entries; print all of them
                        // anyway since the list is short and full visibility helps.
                        var val = Enum.Parse(wbiType, name);
                        int    intVal = Convert.ToInt32(val);
                        bool   relevant = name.IndexOf("Smoke", StringComparison.OrdinalIgnoreCase) >= 0
                                        || name.IndexOf("Meat",  StringComparison.OrdinalIgnoreCase) >= 0
                                        || name.IndexOf("Raw",   StringComparison.OrdinalIgnoreCase) >= 0
                                        || name.IndexOf("Fish",  StringComparison.OrdinalIgnoreCase) >= 0;
                        string flag = relevant ? "  ← relevant" : "";
                        MelonLogger.Msg($"{TAG}   {intVal,3} = {name}{flag}");
                    }
                }
                else
                {
                    MelonLogger.Warning($"{TAG} WorkBucketIdentifier enum not found.");
                }

                // ── 3. Search-entry types ────────────────────────────────────
                MelonLogger.Msg($"{TAG} SearchEntry candidates (Smoke / Meat / Raw / Fish):");
                int searchCount = 0;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
                    catch { continue; }

                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        var n = t.Name;
                        if (n == null) continue;
                        if (!n.EndsWith("SearchEntry", StringComparison.OrdinalIgnoreCase)
                            && !n.EndsWith("SubTask",    StringComparison.OrdinalIgnoreCase))
                            continue;

                        bool relevant = ContainsAny(n,
                            "Smoke", "Meat", "Raw", "Fish", "Cure", "Cook");
                        if (!relevant) continue;

                        MelonLogger.Msg($"{TAG}   {t.FullName}");
                        searchCount++;
                    }
                }
                if (searchCount == 0)
                    MelonLogger.Msg($"{TAG}   (none found — widen the filter if needed)");

                // ── 4. Cross-check: SmokeHouse's CheckWorkAvailability shape ─
                if (smokeType != null)
                {
                    var cwa = smokeType.GetMethod("CheckWorkAvailability", AllInstance);
                    if (cwa != null)
                    {
                        var ps = cwa.GetParameters();
                        string sig = $"{cwa.ReturnType.Name} CheckWorkAvailability(" +
                                     string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")";
                        MelonLogger.Msg($"{TAG} Confirmed gate signature: {sig}");
                        MelonLogger.Msg(
                            $"{TAG}   Patch strategy: {(cwa.ReturnType == typeof(bool) ? "PREFIX with __result override (clean)" : "POSTFIX + bucket suppression")}");
                    }
                    else
                    {
                        MelonLogger.Msg($"{TAG} No CheckWorkAvailability — looking for alternates:");
                        foreach (var m in smokeType.GetMethods(AllInstance))
                        {
                            var n = m.Name;
                            if (n.IndexOf("Work",     StringComparison.OrdinalIgnoreCase) >= 0
                             || n.IndexOf("Need",     StringComparison.OrdinalIgnoreCase) >= 0
                             || n.IndexOf("Avail",    StringComparison.OrdinalIgnoreCase) >= 0
                             || n.IndexOf("Update",   StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                MelonLogger.Msg($"{TAG}   candidate: {m.ReturnType.Name} {n}({m.GetParameters().Length} args)");
                            }
                        }
                    }
                }

                // ── 5. Storage helper methods (for source-has-goods checks) ──
                if (smokeType != null)
                {
                    MelonLogger.Msg($"{TAG} Storage-related methods on SmokeHouse:");
                    foreach (var m in smokeType.GetMethods(AllInstance))
                    {
                        var n = m.Name;
                        if (n.IndexOf("Storage",   StringComparison.OrdinalIgnoreCase) >= 0
                         || n.IndexOf("ItemCount", StringComparison.OrdinalIgnoreCase) >= 0
                         || n.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            MelonLogger.Msg(
                                $"{TAG}   {m.ReturnType.Name} {n}({m.GetParameters().Length} args)");
                        }
                    }
                }

                MelonLogger.Msg($"{TAG} ─── END dump ─────────────────────────────────────");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"{TAG} Dump failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static Type? FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(name);
                if (t != null) return t;
            }
            return null;
        }

        private static string DescribeBaseChain(Type t)
        {
            var sb = new StringBuilder();
            Type? cur = t.BaseType;
            while (cur != null && cur != typeof(object))
            {
                if (sb.Length > 0) sb.Append(" → ");
                sb.Append(cur.Name);
                cur = cur.BaseType;
            }
            return sb.ToString();
        }

        private static void DumpMethods(Type t, string label)
        {
            MelonLogger.Msg($"{TAG} {label} declared methods (own type only, excludes inherited):");
            int count = 0;
            foreach (var m in t.GetMethods(AllInstance | BindingFlags.DeclaredOnly))
            {
                // Skip property accessors and Unity boilerplate to reduce noise
                if (m.IsSpecialName) continue;
                int p = m.GetParameters().Length;
                MelonLogger.Msg($"{TAG}   {m.ReturnType.Name} {m.Name}({p} args)");
                count++;
                if (count > 60)
                {
                    MelonLogger.Msg($"{TAG}   ...(truncated at 60 methods)");
                    break;
                }
            }
        }

        private static void DumpFields(Type t, string label)
        {
            MelonLogger.Msg($"{TAG} {label} declared fields (own type only):");
            int count = 0;
            foreach (var f in t.GetFields(AllInstance | BindingFlags.DeclaredOnly))
            {
                MelonLogger.Msg($"{TAG}   {f.FieldType.Name} {f.Name}");
                count++;
                if (count > 80)
                {
                    MelonLogger.Msg($"{TAG}   ...(truncated at 80 fields)");
                    break;
                }
            }
        }

        private static bool ContainsAny(string s, params string[] needles)
        {
            foreach (var n in needles)
                if (s.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
