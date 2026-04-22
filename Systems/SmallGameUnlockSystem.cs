using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Restores foxes and groundhogs to the game world by overriding the
    /// vanilla spawn delays (1500 days each = effectively never) with
    /// playable values.
    ///
    /// Vanilla state (per AnimalManager decompile):
    ///   foxSpawnDelayInDays = 1500
    ///   groundhogSpawnDelayInDays = 1500
    ///   foxKillCountToDespawn = 1500   (kept — controls end-of-season)
    ///
    /// Both species have COMPLETE behaviour implementations in game code:
    ///   - Fox: extends AggressiveAnimal, wander + crop pursuit + retreat,
    ///     attackTime = 9999f so they never attack villagers
    ///   - FoxWanderSearchEntry + FoxWanderSubTask exist with full AI
    ///   - Groundhog: similar unrated passive-wander class
    ///
    /// Crate gated them off via the delay knobs, not by removing logic —
    /// which means overriding the delays is all it takes to enable them.
    /// </summary>
    public static class SmallGameUnlockSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static void OnMapLoaded()
        {
            try
            {
                var animalManager = UnitySingleton<GameManager>.Instance?.animalManager;
                if (animalManager == null)
                {
                    MelonLogger.Warning(
                        "[WotW] SmallGameUnlock: animalManager not available — skipping.");
                    return;
                }

                Type amType = animalManager.GetType();

                if (WardenOfTheWildsMod.UnlockFoxSpawns.Value)
                    OverrideIntField(amType, animalManager,
                        "foxSpawnDelayInDays",
                        WardenOfTheWildsMod.FoxSpawnDelayDays.Value,
                        "fox");

                if (WardenOfTheWildsMod.UnlockGroundhogSpawns.Value)
                    OverrideIntField(amType, animalManager,
                        "groundhogSpawnDelayInDays",
                        WardenOfTheWildsMod.GroundhogSpawnDelayDays.Value,
                        "groundhog");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] SmallGameUnlockSystem: {ex.Message}");
            }
        }

        private static void OverrideIntField(
            Type type, object instance, string fieldName, int newValue, string label)
        {
            try
            {
                var field = type.GetField(fieldName, AllInstance);
                if (field == null)
                {
                    MelonLogger.Warning(
                        $"[WotW] SmallGameUnlock: field '{fieldName}' not found — " +
                        "vanilla field name may have changed.");
                    return;
                }

                int previous = (int)field.GetValue(instance);
                if (previous == newValue)
                {
                    MelonLogger.Msg(
                        $"[WotW] {label} spawn delay already {newValue}d (no change).");
                    return;
                }

                field.SetValue(instance, newValue);
                MelonLogger.Msg(
                    $"[WotW] Unlocked {label}: spawn delay {previous}d → {newValue}d.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] OverrideIntField({fieldName}): {ex.Message}");
            }
        }
    }
}
