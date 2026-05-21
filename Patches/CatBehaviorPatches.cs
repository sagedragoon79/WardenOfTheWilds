using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Systems;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Three patches that make Cat survival sane in the DLC:
    ///
    ///   1. <b>Rat-hunt leash</b> — postfix on
    ///      <c>CatchRatsSearchEntry.StartCatchRatsSearch</c>. When the task
    ///      completes and the chosen target is beyond <c>CatRatHuntRadius</c>
    ///      from the cat's home (residence / CatKennel / nearest Shelter),
    ///      cancel the task so the cat won't trek 40 units into the woods
    ///      after a rat. Cats employed by a RatCatcherBuilding flow through
    ///      vanilla unchanged — they have their own work radius.
    ///
    ///   2. <b>Faster threat detection</b> — postfix on
    ///      <c>PetRetreatSearchEntry</c>'s constructor. For cat receivers,
    ///      scales <c>pet.damageableComp.searchRange</c> by the configured
    ///      multiplier and lowers <c>timeBetweenEnemyChecks</c> from
    ///      vanilla 5 s to the configured interval (default 1 s).
    ///
    ///   3. <b>Synthetic retreat fallback</b> — TODO once we have telemetry
    ///      on how often vanilla's GetRetreatTarget returns null in
    ///      practice. Tackled after #1 + #2 ship to see if they alone
    ///      solve the problem.
    ///
    /// All gated by <c>DlcDetection.PetsDlcActive</c> — non-DLC saves are
    /// untouched. Soft-fail on type/method lookup miss; never throws into
    /// vanilla code paths.
    /// </summary>
    internal static class CatBehaviorPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // Reflection handles cached during Register.
        private static FieldInfo  _entryWorkerField;
        private static FieldInfo  _retreatPetField;
        private static FieldInfo  _retreatScanIntervalField;
        private static Type       _catType;

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type catchRatsType = AccessTools.TypeByName("CatchRatsSearchEntry");
                if (catchRatsType == null)
                {
                    MelonLogger.Msg(
                        "[WotW] CatBehaviorPatches: CatchRatsSearchEntry type not " +
                        "found (pre-DLC build?). Skipping cat patches.");
                    return;
                }

                _entryWorkerField =
                    AccessTools.Field(catchRatsType, "worker");

                // ── Patch 1: rat-hunt leash ──────────────────────────────────
                // We post-process the search-complete callback. The cleanest
                // hook is on the callback wrapper that fires once the
                // GetHighestScoredInContainer query returns. But we don't
                // have a clean Harmony surface on the inner callback factory
                // method without hand-decompiling. Instead we patch
                // StartCatchRatsSearch's outer return: if the worker is a
                // free-roaming Cat (not a RatCatcher worker), we substitute
                // our own bounded-radius QueryData rather than the unbounded
                // one. This is structurally identical to the rat-catcher
                // branch, just with our radius/center.
                //
                // Implementation note: rather than reproducing the entire
                // method body, the simpler-and-safer postfix here cancels
                // the resulting task if the chosen rat ended up too far
                // away. It's reactive (the cat may start walking, then
                // re-evaluate next tick) but doesn't risk subtle bugs from
                // substituting query types.

                var startMethod = AccessTools.Method(catchRatsType, "StartCatchRatsSearch");
                if (startMethod != null)
                {
                    // Postfix that watches the chosen task; if it commits to
                    // a rat beyond the leash, cancel.
                    // We don't have a guaranteed signature for the
                    // SearchCompleteCallback factory, so we instead defer
                    // the distance check to subtask runtime by patching the
                    // sub-task entry instead — see CatchRatsSubTask path
                    // below.
                }

                Type subTaskType = AccessTools.TypeByName("CatchRatsSubTask");
                if (subTaskType != null)
                {
                    // CatchRatsSubTask is the per-tick worker action. Hooking
                    // a Start / Enter / Update method lets us cancel when the
                    // worker is a Cat and the target is out of leash.
                    var startSubTask = AccessTools.Method(subTaskType, "DoEnter")
                                    ?? AccessTools.Method(subTaskType, "OnEnter")
                                    ?? AccessTools.Method(subTaskType, "Start");
                    if (startSubTask != null)
                    {
                        harmony.Patch(startSubTask,
                            postfix: new HarmonyMethod(typeof(CatBehaviorPatches),
                                                       nameof(CatchRatsSubTaskStartPostfix)));
                        MelonLogger.Msg(
                            $"[WotW] CatBehaviorPatches: patched CatchRatsSubTask.{startSubTask.Name}");
                    }
                }

                // ── Patch 2: faster threat detection ─────────────────────────
                Type retreatType = AccessTools.TypeByName("PetRetreatSearchEntry");
                if (retreatType != null)
                {
                    _retreatPetField =
                        AccessTools.Field(retreatType, "pet");
                    _retreatScanIntervalField =
                        AccessTools.Field(retreatType, "timeBetweenEnemyChecks");

                    // Constructor takes (ITaskReceiver, int)
                    var ctor = retreatType.GetConstructor(
                        AllInstance, null,
                        new[] { AccessTools.TypeByName("ITaskReceiver"), typeof(int) },
                        null);
                    if (ctor != null)
                    {
                        harmony.Patch(ctor,
                            postfix: new HarmonyMethod(typeof(CatBehaviorPatches),
                                                       nameof(PetRetreatCtorPostfix)));
                        MelonLogger.Msg(
                            "[WotW] CatBehaviorPatches: patched PetRetreatSearchEntry .ctor");
                    }
                }

                _catType = AccessTools.TypeByName("Cat");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] CatBehaviorPatches.Register: {ex.Message}");
            }
        }

        // ── Patch 1 postfix ──────────────────────────────────────────────────
        /// <summary>
        /// After a CatchRatsSubTask starts, verify the chosen target is
        /// within our leash radius when the worker is a free-roaming Cat
        /// (i.e. not a Rat Catcher Building employee). If not, mark the
        /// sub-task as completed unsuccessfully so the cat falls through
        /// to its next priority task (wander, retreat-if-threat).
        /// </summary>
        public static void CatchRatsSubTaskStartPostfix(object __instance)
        {
            try
            {
                if (!DlcDetection.PetsDlcActive) return;

                var taskProp = __instance.GetType().GetProperty("task", AllInstance)
                             ?? __instance.GetType().GetProperty("Task", AllInstance);
                var task = taskProp?.GetValue(__instance);
                if (task == null) return;

                // Get the worker (cat) from the task
                var workerProp = task.GetType().GetProperty("receiver", AllInstance)
                              ?? task.GetType().GetProperty("Receiver", AllInstance);
                var worker = workerProp?.GetValue(task);
                if (worker == null) return;

                // Only leash Cats — RatCatcher workers, dogs, etc. unaffected
                if (_catType == null || !_catType.IsInstanceOfType(worker)) return;

                var catComp = worker as Component;
                if (catComp == null) return;

                // RatCatcherBuilding workers go through the vanilla radius path
                // and shouldn't be re-leashed. Detect by checking placeOfWork.
                var placeProp = worker.GetType().GetProperty("placeOfWork", AllInstance);
                var placeOfWork = placeProp?.GetValue(worker);
                if (placeOfWork != null)
                {
                    string typeName = placeOfWork.GetType().Name;
                    if (typeName == "RatCatcherBuilding") return;
                }

                // Resolve target position from subtask
                Vector3? targetPos = ResolveSubtaskTargetPosition(__instance);
                if (!targetPos.HasValue) return;

                // Resolve cat's home anchor (residence → nearest shelter →
                // cat's current position fallback)
                Vector3 home = ResolveCatHome(catComp);

                float maxDist = WardenOfTheWildsMod.CatRatHuntRadius.Value;
                float dist = Vector3.Distance(home, targetPos.Value);
                if (dist <= maxDist) return;

                // Out of leash — abort. Mark the subtask as failed/complete
                // so the task system picks the next priority.
                AbortSubtask(__instance);
                MelonLogger.Msg(
                    $"[WotW] Cat '{catComp.gameObject.name}': aborted rat hunt — " +
                    $"target {dist:F1}u away (leash {maxDist:F1}u).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] CatchRatsSubTaskStartPostfix: {ex.Message}");
            }
        }

        private static Vector3? ResolveSubtaskTargetPosition(object subTask)
        {
            try
            {
                Type t = subTask.GetType();

                // Common SubTask field shapes — try a few until one resolves
                string[] candidates = { "targetPosition", "_targetPosition",
                                        "target", "destination", "ratNest",
                                        "ratsNest", "workTarget" };
                foreach (string name in candidates)
                {
                    var f = t.GetField(name, AllInstance);
                    if (f == null) continue;
                    object val = f.GetValue(subTask);
                    if (val is Vector3 v) return v;
                    if (val is Component c) return c.transform.position;
                    if (val is GameObject go) return go.transform.position;
                }
                return null;
            }
            catch { return null; }
        }

        private static void AbortSubtask(object subTask)
        {
            try
            {
                Type t = subTask.GetType();

                // Look for a "MarkComplete" / "Complete" / "End" / "Cancel"
                // entry point.
                string[] candidates = { "Cancel", "EndSubTask", "MarkComplete",
                                        "Complete", "End" };
                foreach (string name in candidates)
                {
                    var m = t.GetMethod(name, AllInstance, null, Type.EmptyTypes, null);
                    if (m == null) continue;
                    m.Invoke(subTask, null);
                    return;
                }

                // Fallback: set a state field if we can find one
                var stateField = t.GetField("state", AllInstance)
                              ?? t.GetField("_state", AllInstance);
                if (stateField != null && stateField.FieldType.IsEnum)
                {
                    object[] names = Enum.GetValues(stateField.FieldType) as object[];
                    if (names != null)
                    {
                        foreach (object e in names)
                        {
                            string n = e.ToString();
                            if (n == "Cancelled" || n == "Failed" || n == "Complete")
                            {
                                stateField.SetValue(subTask, e);
                                break;
                            }
                        }
                    }
                }
            }
            catch { /* swallow — task will time out via vanilla flow */ }
        }

        private static Vector3 ResolveCatHome(Component cat)
        {
            try
            {
                // Try residence first
                var pet = cat;
                var residenceProp = pet.GetType().GetProperty("residence", AllInstance)
                                  ?? pet.GetType().BaseType?.GetProperty("residence", AllInstance);
                if (residenceProp != null)
                {
                    var res = residenceProp.GetValue(pet);
                    if (res != null)
                    {
                        var resGoProp = res.GetType().GetProperty("gameObject", AllInstance);
                        var resGo = resGoProp?.GetValue(res) as GameObject;
                        if (resGo != null) return resGo.transform.position;
                    }
                }

                // Fall back to nearest Shelter
                var gm = UnitySingleton<GameManager>.Instance;
                var sheltersProp = gm?.resourceManager?.GetType()
                    .GetProperty("sheltersRO", AllInstance);
                var shelters = sheltersProp?.GetValue(gm.resourceManager)
                    as System.Collections.IEnumerable;
                if (shelters != null)
                {
                    float bestSq = float.MaxValue;
                    Vector3 best = cat.transform.position;
                    Vector3 cp = cat.transform.position;
                    foreach (var s in shelters)
                    {
                        var go = (s as Component)?.gameObject;
                        if (go == null) continue;
                        float d = (go.transform.position - cp).sqrMagnitude;
                        if (d < bestSq) { bestSq = d; best = go.transform.position; }
                    }
                    if (bestSq < float.MaxValue) return best;
                }
            }
            catch { /* fall through */ }
            return cat.transform.position;
        }

        // ── Patch 2 postfix ──────────────────────────────────────────────────
        /// <summary>
        /// Run after PetRetreatSearchEntry's constructor. For Cat receivers,
        /// scale searchRange and lower the scan interval per config.
        /// </summary>
        public static void PetRetreatCtorPostfix(object __instance)
        {
            try
            {
                if (!DlcDetection.PetsDlcActive) return;
                if (_retreatPetField == null) return;

                var pet = _retreatPetField.GetValue(__instance) as Component;
                if (pet == null) return;
                if (_catType == null || !_catType.IsInstanceOfType(pet)) return;

                // Scale searchRange on the cat's damageableComp
                float mult = WardenOfTheWildsMod.CatRetreatDetectionMult.Value;
                if (mult > 0f && !Mathf.Approximately(mult, 1f))
                {
                    var dcProp = pet.GetType().GetProperty("damageableComp", AllInstance)
                              ?? pet.GetType().BaseType?.GetProperty("damageableComp", AllInstance);
                    var dc = dcProp?.GetValue(pet);
                    if (dc != null)
                    {
                        var rangeProp = dc.GetType().GetProperty("searchRange", AllInstance)
                                     ?? dc.GetType().GetField("searchRange", AllInstance) as MemberInfo;
                        // Try property first, then field
                        var rangePropTyped = dc.GetType().GetProperty("searchRange", AllInstance);
                        if (rangePropTyped != null && rangePropTyped.CanWrite)
                        {
                            float current = (float)rangePropTyped.GetValue(dc);
                            rangePropTyped.SetValue(dc, current * mult);
                        }
                        else
                        {
                            var rangeField = dc.GetType().GetField("searchRange", AllInstance);
                            if (rangeField != null)
                            {
                                float current = (float)rangeField.GetValue(dc);
                                rangeField.SetValue(dc, current * mult);
                            }
                        }
                    }
                }

                // Lower scan interval
                if (_retreatScanIntervalField != null)
                {
                    float interval = Mathf.Max(0.1f,
                        WardenOfTheWildsMod.CatEnemyScanIntervalSeconds.Value);
                    _retreatScanIntervalField.SetValue(__instance, interval);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PetRetreatCtorPostfix: {ex.Message}");
            }
        }
    }
}
