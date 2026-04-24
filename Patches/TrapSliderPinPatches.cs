using System;
using System.Reflection;
using HarmonyLib;
using WardenOfTheWilds.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  TrapSliderPinPatches
//
//  Pins the vanilla trap-count slider to a mode-appropriate value on T2
//  hunter buildings that have a HunterCabinEnhancement:
//
//      HuntingLodge (Big Game Hunter) → slider forced to 0  (no traps)
//      TrapperLodge (Trap Master)     → slider forced to max (all traps)
//
//  Any attempt to change the slider (player drags it) is intercepted by the
//  setter prefix and clamped to the mode's expected value. When our own
//  ApplyPath code calls the setter, it already passes the correct value, so
//  no functional change there.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    internal static class TrapSliderPinPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type hbType = AccessTools.TypeByName("HunterBuilding");
                if (hbType == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] TrapSliderPin: HunterBuilding type not found.");
                    return;
                }

                var setter = hbType.GetMethod(
                    "set_userDefinedMaxDeployedTraps", AllInstance);
                if (setter == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] TrapSliderPin: set_userDefinedMaxDeployedTraps not found.");
                    return;
                }

                var prefix = new HarmonyMethod(
                    typeof(TrapSliderPinPatches), nameof(SliderSetterPrefix));
                harmony.Patch(setter, prefix: prefix);

                WardenOfTheWildsMod.Log.Msg(
                    "[WotW] TrapSliderPin: patched HunterBuilding.set_userDefinedMaxDeployedTraps");
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Error($"[WotW] TrapSliderPin.Register failed: {ex}");
            }
        }

        /// <summary>
        /// Prefix on HunterBuilding.userDefinedMaxDeployedTraps setter.
        /// Clamps the incoming value to match the building's current mode.
        /// T1 and non-modded hunters pass through unchanged.
        /// </summary>
        private static void SliderSetterPrefix(HunterBuilding __instance, ref int value)
        {
            try
            {
                if (__instance == null) return;
                if (__instance.tier < 2) return;

                var enh = __instance.GetComponent<HunterCabinEnhancement>();
                if (enh == null) return;

                // Normalise legacy Vanilla → HuntingLodge (matches ApplyPath)
                var effective = enh.Path == HunterT2Path.Vanilla
                    ? HunterT2Path.HuntingLodge
                    : enh.Path;

                int target;
                switch (effective)
                {
                    case HunterT2Path.HuntingLodge:
                        target = 0;
                        break;
                    case HunterT2Path.TrapperLodge:
                        target = __instance.maxDeployedTraps;
                        break;
                    default:
                        return; // unknown mode — let the original value pass
                }

                // Log EVERY fire so we can tell whether UI drags reach us
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] TrapSliderPin FIRED: '{__instance.gameObject.name}' " +
                    $"incoming={value} mode={effective} target={target}");

                if (value != target)
                {
                    value = target;
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] TrapSliderPin prefix error: {ex.Message}");
            }
        }
    }
}
