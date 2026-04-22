using HarmonyLib;
using MelonLoader;
using WardenOfTheWilds.Components;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Scales butchering (and any ManufactureWorkOrder) throughput for T2
    /// hunter cabins based on the path. Prefix on ManufactureWorkOrder.AddWorkUnits
    /// multiplies the incoming work-unit count before vanilla processes it.
    ///
    /// Scope:
    ///   T2 HuntingLodge → HuntingLodgeButcherSpeedMult (default 1.25×)
    ///   T2 TrapperLodge → TrapperLodgeButcherSpeedMult (default 1.25×)
    ///   Anything else    → vanilla (1×)
    ///
    /// Combined with vanilla 2-worker parallelism on T2 paths, this pushes
    /// net throughput to ~2.5× T1. Addresses the "T2 underperforms T1" gap —
    /// butchering was the bottleneck, not hunting.
    /// </summary>
    [HarmonyPatch(typeof(ManufactureWorkOrder), "AddWorkUnits")]
    internal static class HunterButcherSpeedPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ManufactureWorkOrder __instance, ref uint amount)
        {
            try
            {
                // Is this work order running at a hunter cabin?
                if (!(__instance.ownerBuilding is HunterBuilding hb)) return;

                var enh = hb.GetComponent<HunterCabinEnhancement>();
                if (enh == null) return;

                float mult = enh.Path switch
                {
                    HunterT2Path.HuntingLodge =>
                        WardenOfTheWildsMod.HuntingLodgeButcherSpeedMult.Value,
                    HunterT2Path.TrapperLodge =>
                        WardenOfTheWildsMod.TrapperLodgeButcherSpeedMult.Value,
                    _ => 1f,
                };

                if (mult <= 0f || System.Math.Abs(mult - 1f) < 0.001f) return;

                // Scale the amount. Rounding up so small work chunks still
                // progress (uint truncation would lose fractional work).
                uint scaled = (uint)System.Math.Max(1,
                    UnityEngine.Mathf.CeilToInt(amount * mult));
                amount = scaled;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterButcherSpeedPatch: {ex.Message}");
            }
        }
    }
}
