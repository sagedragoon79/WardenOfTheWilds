using System.Linq;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Extends vanilla's control group system to include hunter villagers.
    ///
    /// Vanilla's SoldierControls.SetControlGroup(int) adds currently-selected
    /// SOLDIERS to the target group (via AddSelectedSoldier, which type-gates
    /// on VillagerOccupationSoldier). Our postfix runs after vanilla's
    /// SetControlGroup and adds any currently-selected HUNTERS to the same
    /// group via the civilian-friendly AddToControlGroup(ISelectable, int).
    ///
    /// Why this works:
    ///   ControlGroup.members is List&lt;ISelectable&gt; — no type filter.
    ///   SelectControlGroup(int) iterates members via SelectSelectable — again no filter.
    ///   So once a hunter is in a group's members list, the vanilla recall key
    ///   (bare 1-9) selects them alongside any soldiers in the same group.
    ///
    /// Result: mixed hunter + soldier control groups work natively. Press Ctrl+N
    /// to assign, N to recall, right-click to move. No custom hotkey needed
    /// for the recall side — vanilla does it.
    /// </summary>
    [HarmonyPatch(typeof(SoldierControls), "SetControlGroup")]
    internal static class HunterControlGroupPatch
    {
        [HarmonyPostfix]
        public static void Postfix(SoldierControls __instance, int groupNumber)
        {
            try
            {
                var gm = UnitySingleton<GameManager>.Instance;
                var im = gm?.inputManager;
                if (im?.selectedObjs == null) return;

                int added = 0;
                foreach (var selectable in im.selectedObjs.ToList())
                {
                    if (selectable == null) continue;

                    // Only hunters — other civilian types aren't a WotW concern.
                    // Soldiers already got added by vanilla's SetControlGroup.
                    var component = selectable as Component;
                    var villager = component?.GetComponent<Villager>()
                                   ?? component?.GetComponentInParent<Villager>();
                    if (villager == null) continue;
                    if (!(villager.occupation is VillagerOccupationHunter)) continue;

                    __instance.AddToControlGroup(selectable, groupNumber);
                    added++;
                }

                if (added > 0)
                    MelonLogger.Msg(
                        $"[WotW] Control group {groupNumber + 1}: " +
                        $"added {added} hunter(s) alongside vanilla soldiers.");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterControlGroupPatch: {ex.Message}");
            }
        }
    }
}
