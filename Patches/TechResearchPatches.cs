using System;
using System.Reflection;
using HarmonyLib;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Hooks TechTreeManager.ActivateTechOrRank (the confirmed vanilla method
    /// that increments node ranks and applies effects) so we fire
    /// WardenOfTheWildsMod.OnSustainableFishingResearched the moment a player
    /// spends a point on that node. The setter gates on transition so multiple
    /// activations (onLoad=true plus the in-session activation) are idempotent.
    ///
    /// Replaces the earlier candidate-list approach which missed the real
    /// method name (the candidates IncrementRank/Research/ResearchNode/... do
    /// not exist in vanilla v1.1.0).
    /// </summary>
    internal static class TechResearchPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type managerType = AccessTools.TypeByName("TechTreeManager");
                if (managerType == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] TechResearchPatches: TechTreeManager not found.");
                    return;
                }

                // ActivateTechOrRank(int id, int knowledgePointCost, bool onLoad)
                var method = AccessTools.Method(managerType, "ActivateTechOrRank",
                    new[] { typeof(int), typeof(int), typeof(bool) });
                if (method == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] TechResearchPatches: TechTreeManager.ActivateTechOrRank " +
                        "(int,int,bool) not found. Slider will only refresh on info-window reopen.");
                    return;
                }

                harmony.Patch(method, postfix: new HarmonyMethod(
                    typeof(TechResearchPatches), nameof(ActivatePostfix)));
                WardenOfTheWildsMod.Log.Msg(
                    "[WotW] TechResearchPatches: patched TechTreeManager.ActivateTechOrRank");
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Error(
                    $"[WotW] TechResearchPatches.Register failed: {ex}");
            }
        }

        /// <summary>
        /// Postfix on ActivateTechOrRank. Refreshes the Sustainable Fishing
        /// research state — the setter fires the event only on the false→true
        /// transition, so activating other nodes is a no-op for us.
        /// </summary>
        public static void ActivatePostfix()
        {
            try { WardenOfTheWildsMod.RefreshFishingTechState(); }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] TechResearchPatches.ActivatePostfix: {ex.Message}");
            }
        }
    }
}
