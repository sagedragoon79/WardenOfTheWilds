using HarmonyLib;
using MelonLoader;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  HunterHotkeyPatches
//  Allows hunters to be assigned to Ctrl+N control groups while they hold
//  the hunter job. When reassigned to any other job they lose eligibility
//  automatically — no barracks, no upkeep, no military status side effects.
//
//  DESIGN CONSTRAINT:
//    Hunters must remain civilians. Do NOT set isMilitary or any equivalent
//    flag — military status may carry housing requirements (barracks), gold
//    upkeep, or conscription rules that would break civilian reassignment.
//    The player needs hunters to be auto-reassignable to other jobs freely.
//
//  APPROACH — preferred order:
//
//    1. DIRECT CHECK PATCH (cleanest, no flag touching):
//       Patch the method in the control group system that decides whether a
//       villager is eligible to be added to a group. Add a secondary condition:
//       "OR the villager's assigned building is a HunterBuilding."
//       isMilitary is never written. The hunter stays a full civilian in every
//       other system.
//
//    2. JOB-SCOPED FLAG FALLBACK (if direct check can't be found):
//       Patch HunterBuilding.AssignWorker  → set a non-military grouping flag
//       Patch HunterBuilding.RemoveWorker  → clear that same flag
//       The flag is only true while the villager is employed as a hunter.
//       On reassignment the building fires RemoveWorker first, clearing it.
//       We specifically avoid isMilitary and prefer a purpose-built flag name
//       like canBeGrouped or isRangedUnit if one exists.
//
//  TRACKING:
//    We maintain a static HashSet of currently-employed hunter worker objects
//    (by instance hash) as a fast lookup for the direct check patch.
//    Populated on assign, cleared on unassign and on map load.
//
//  CONFIRMED TYPES (awaiting combat dump):
//    ? ControlGroupManager / ControlGroup / UnitGroupManager
//    ? SelectionManager  — which method checks eligibility
//    ? Villager.assignedBuilding / currentBuilding / workplace
//    ? HunterBuilding.AssignWorker / RemoveWorker method names
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    public static class HunterHotkeyPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // ── Active hunter worker registry ─────────────────────────────────────
        // Fast O(1) lookup for the control group eligibility check.
        // Key = RuntimeHelpers.GetHashCode(workerObject).
        private static readonly HashSet<int> ActiveHunterWorkers =
            new HashSet<int>();

        public static void OnMapLoaded()
        {
            ActiveHunterWorkers.Clear();
        }

        public static bool IsActiveHunterWorker(object worker) =>
            ActiveHunterWorkers.Contains(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(worker));

        // ── Manual patch application ──────────────────────────────────────────
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!WardenOfTheWildsMod.HunterOverhaulEnabled.Value) return;

            // ── Step 1: Hook HunterBuilding assign/unassign to maintain registry ─
            bool registryHooked = HookHunterWorkerRegistry(harmony);
            if (registryHooked)
                MelonLogger.Msg("[WotW] HunterHotkeyPatches: hunter worker registry hooked.");
            else
                MelonLogger.Msg("[WotW] HunterHotkeyPatches: worker registry hook pending " +
                                "— assign/unassign method names needed from combat dump.");

            // ── Step 2: Patch the control group eligibility check ─────────────
            // Try known class/method names. All are checked; first success wins.
            bool cgPatched = false;

            cgPatched |= TryPatchEligibilityCheck(harmony, "ControlGroupManager",
                new[] { "CanAddToGroup", "IsEligible", "IsGroupable",
                        "CanJoinGroup", "IsValidGroupMember", "FilterUnits" });

            cgPatched |= TryPatchEligibilityCheck(harmony, "ControlGroup",
                new[] { "CanAddToGroup", "IsEligible", "CanAdd", "Add" });

            cgPatched |= TryPatchEligibilityCheck(harmony, "UnitGroupManager",
                new[] { "CanAddToGroup", "IsEligible", "IsGroupable" });

            cgPatched |= TryPatchEligibilityCheck(harmony, "SelectionManager",
                new[] { "CanAddToControlGroup", "IsGroupEligible",
                        "FilterForGroup", "AssignToGroup" });

            cgPatched |= TryPatchEligibilityCheck(harmony, "SelectionController",
                new[] { "CanAddToControlGroup", "IsGroupEligible" });

            if (cgPatched)
                MelonLogger.Msg("[WotW] HunterHotkeyPatches: control group eligibility patched.");
            else
                MelonLogger.Msg("[WotW] HunterHotkeyPatches: control group class/method not " +
                                "found yet — awaiting combat dump.");
        }

        // ── Hook HunterBuilding assign / unassign ─────────────────────────────
        private static bool HookHunterWorkerRegistry(HarmonyLib.Harmony harmony)
        {
            Type? hunterType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                hunterType = asm.GetType("HunterBuilding");
                if (hunterType != null) break;
            }
            if (hunterType == null) return false;

            bool assignPatched = false;
            bool removePatched = false;

            // Assign — when a villager takes the hunter job
            string[] assignCandidates = {
                "AssignWorker", "AddWorker", "OnWorkerAssigned",
                "AssignVillager", "SetWorker", "OnVillagerAssigned",
            };
            foreach (string name in assignCandidates)
            {
                var m = hunterType.GetMethod(name, AllInstance);
                if (m == null) continue;
                try
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(
                        typeof(HunterHotkeyPatches).GetMethod(
                            nameof(OnHunterWorkerAssignedPostfix), AllStatic)));
                    MelonLogger.Msg($"[WotW] HunterHotkeyPatches: patched HunterBuilding.{name} (assign)");
                    assignPatched = true;
                    break;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[WotW] HunterHotkeyPatches assign {name}: {ex.Message}");
                }
            }

            // Unassign — when a villager leaves the hunter job (reassignment or dismissal)
            string[] removeCandidates = {
                "RemoveWorker", "UnassignWorker", "OnWorkerRemoved",
                "RemoveVillager", "UnassignVillager", "OnVillagerUnassigned",
                "ClearWorker", "DismissWorker",
            };
            foreach (string name in removeCandidates)
            {
                var m = hunterType.GetMethod(name, AllInstance);
                if (m == null) continue;
                try
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(
                        typeof(HunterHotkeyPatches).GetMethod(
                            nameof(OnHunterWorkerRemovedPostfix), AllStatic)));
                    MelonLogger.Msg($"[WotW] HunterHotkeyPatches: patched HunterBuilding.{name} (unassign)");
                    removePatched = true;
                    break;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[WotW] HunterHotkeyPatches unassign {name}: {ex.Message}");
                }
            }

            return assignPatched || removePatched;
        }

        // ── Worker registry postfixes ─────────────────────────────────────────

        /// <summary>
        /// Called after a villager is assigned to a HunterBuilding.
        /// Uses Harmony's __args[0] (first original-method argument) rather
        /// than a named parameter, so we work regardless of how vanilla
        /// names the arg (resident, worker, v, etc.). Named params cause
        /// "IL Compile Error" if they don't exactly match.
        /// </summary>
        public static void OnHunterWorkerAssignedPostfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return;
                var worker = __args[0];
                if (worker == null) return;

                int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(worker);
                ActiveHunterWorkers.Add(id);
                MelonLogger.Msg(
                    $"[WotW] Hunter worker registered for hotkey groups: " +
                    $"'{(worker as Component)?.gameObject.name}' (id={id})");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnHunterWorkerAssignedPostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Called after a villager is removed from a HunterBuilding.
        /// Clears them from ActiveHunterWorkers — they are no longer eligible
        /// for control groups as they have left the hunter job.
        /// </summary>
        public static void OnHunterWorkerRemovedPostfix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return;
                var worker = __args[0];
                if (worker == null) return;

                int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(worker);
                ActiveHunterWorkers.Remove(id);
                MelonLogger.Msg(
                    $"[WotW] Hunter worker removed from hotkey groups: " +
                    $"'{(worker as Component)?.gameObject.name}' (id={id})");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnHunterWorkerRemovedPostfix: {ex.Message}");
            }
        }

        // ── Control group eligibility check patch ─────────────────────────────
        private static bool TryPatchEligibilityCheck(
            HarmonyLib.Harmony harmony, string className, string[] methodNames)
        {
            Type? cgType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                cgType = asm.GetType(className);
                if (cgType != null) break;
            }
            if (cgType == null) return false;

            foreach (string methodName in methodNames)
            {
                var method = cgType.GetMethod(methodName, AllInstance)
                          ?? cgType.GetMethod(methodName, AllStatic);
                if (method == null) continue;

                try
                {
                    // Use postfix to widen the result, or prefix to prevent false rejections.
                    // If the method returns bool (eligibility check), postfix flips false→true
                    // for registered hunter workers.
                    // If the method takes a list and filters it, prefix approach is needed.
                    // Both stubs are present; comment out whichever doesn't apply once
                    // signature is confirmed from the dump.
                    if (method.ReturnType == typeof(bool))
                    {
                        harmony.Patch(method, postfix: new HarmonyMethod(
                            typeof(HunterHotkeyPatches).GetMethod(
                                nameof(EligibilityCheckPostfix), AllStatic)));
                        MelonLogger.Msg(
                            $"[WotW] HunterHotkeyPatches: patched {className}.{methodName} " +
                            $"(bool eligibility postfix)");
                    }
                    else
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(
                            typeof(HunterHotkeyPatches).GetMethod(
                                nameof(GroupAssignPrefix), AllStatic)));
                        MelonLogger.Msg(
                            $"[WotW] HunterHotkeyPatches: patched {className}.{methodName} " +
                            $"(list assign prefix)");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[WotW] HunterHotkeyPatches {className}.{methodName}: {ex.Message}");
                }
            }
            return false;
        }

        // ── Eligibility postfix (bool-returning check methods) ────────────────
        /// <summary>
        /// Postfix on control group eligibility check methods that return bool.
        /// If the vanilla result is false but the villager is an active hunter
        /// worker, override to true.
        ///
        /// Critically: we never widen eligibility based on isMilitary status —
        /// only based on "is this villager currently employed as a hunter."
        /// </summary>
        public static void EligibilityCheckPostfix(object __instance, object villager,
                                                   ref bool __result)
        {
            try
            {
                if (__result) return; // Already eligible — nothing to do
                if (villager == null) return;
                if (IsActiveHunterWorker(villager))
                {
                    __result = true;
                    MelonLogger.Msg(
                        $"[WotW] Hunter '{(villager as Component)?.gameObject.name}' " +
                        $"allowed into control group (job-scoped eligibility).");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] EligibilityCheckPostfix: {ex.Message}");
            }
        }

        // ── List-filter prefix (methods that accept a collection of villagers) ─
        /// <summary>
        /// Prefix on control group assignment methods that receive a list/collection
        /// of villagers and filter it before storing the group.
        ///
        /// TODO: Fill in once method signature is confirmed from combat dump.
        /// If the parameter is List&lt;Villager&gt;, iterate it and ensure any
        /// active hunter workers that were filtered out are re-inserted.
        /// </summary>
        public static void GroupAssignPrefix(object __instance)
        {
            // TODO: implement once parameter type is confirmed.
            // Pattern will be:
            //   foreach unit in selection that was about to be excluded:
            //     if IsActiveHunterWorker(unit): add it back to the list
        }
    }
}
