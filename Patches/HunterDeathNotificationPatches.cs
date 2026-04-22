using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Customizes the event log message when a hunter dies.
    ///
    /// Vanilla's UIEventLogWindow.OnVillagerDiedEvent posts a generic
    /// "The hunter Alice has died due to mauling." message. We replace it
    /// with more dramatic, hunter-specific wording to reinforce the weight
    /// of losing a hunter (they carry bows, take risks, deserve the send-off).
    ///
    /// Tone examples (randomized from a pool):
    ///   "Alice, a hunter, has perished in the wilds. (slain by Bear)"
    ///   "A hunter has fallen in the hunt — Alice, taken by Wolf."
    ///   "Alice, hunter of our people, was cut down by Boar."
    ///
    /// Strategy: Harmony PREFIX on OnVillagerDiedEvent. If the dead villager
    /// is a hunter (VillagerOccupationHunter), call AddEventToLog with our
    /// custom string and return false to skip vanilla's message. Otherwise
    /// return true and let vanilla handle the usual death notification.
    /// </summary>
    [HarmonyPatch(typeof(UIEventLogWindow), "OnVillagerDiedEvent")]
    internal static class HunterDeathNotificationPatch
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly System.Random Rng = new System.Random();

        // Flavor lines — {0}=name, {1}=cause (lowercased death reason)
        private static readonly string[] FlavorLines = new[]
        {
            "{0}, a hunter of our people, has perished in the wilds ({1}).",
            "A hunter has fallen in the hunt — {0}, taken by {1}.",
            "{0}, hunter of the frontier, was cut down by {1}.",
            "The hunter {0} has met their end. Cause: {1}.",
            "{0}, a hunter, has perished. The wilds took them ({1}).",
        };

        [HarmonyPrefix]
        public static bool Prefix(UIEventLogWindow __instance, VillagerDiedEvent receivedEvent)
        {
            try
            {
                if (receivedEvent?.villager == null) return true;

                // Emigration is handled by vanilla's early-out; match that.
                if (string.Equals(
                        receivedEvent.deathReason, "emigration",
                        StringComparison.OrdinalIgnoreCase))
                    return true;

                // Only intercept if this was a hunter at time of death.
                var hunter = receivedEvent.villager.occupation as VillagerOccupationHunter;
                if (hunter == null) return true;

                // Pick a flavor line and format it
                string line = FlavorLines[Rng.Next(FlavorLines.Length)];
                string name = receivedEvent.villager.villagerName ?? "A hunter";
                string cause = (receivedEvent.deathReason ?? "unknown causes").ToLower();
                string summary = string.Format(line, name, cause);

                // Call the vanilla AddEventToLog(summary, extendedDescription)
                // via reflection so we post into the same event feed.
                var addMethod = typeof(UIEventLogWindow).GetMethod(
                    "AddEventToLog", AllInstance);
                if (addMethod != null)
                {
                    addMethod.Invoke(__instance, new object[] { summary, null });
                    MelonLogger.Msg($"[WotW] Hunter death — {summary}");
                    return false;  // skip vanilla message for this event
                }

                // Fallback: if we can't post (method signature differed),
                // let vanilla handle so the player still sees SOMETHING.
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterDeathNotification: {ex.Message}");
                return true;  // safe default: don't block vanilla
            }
        }
    }
}
