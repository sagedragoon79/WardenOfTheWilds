using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WardenOfTheWilds.Components;

// ─────────────────────────────────────────────────────────────────────────────
//  FishingShackLoadPatches
//
//  Applies the critical data modifications (fishStorageCapacity, fishingRadius,
//  maxWorkers) during the game's LOADING phase, before vanilla initialises
//  fishing areas and before fishermen start working. Mirrors Manifest
//  Delivery's pattern of hooking OnGameFinishedLoadingFinalize.
//
//  Before this patch:
//    • LateInit waits 10s → attaches component → InitializeDelayed →
//      ApplyMode → radius 30→60 → CreateFishingAreas rescan → fishermen
//      go into "looking for shore" for 20-30s while state rebuilds.
//      Net visible delay: 30-45 seconds of dysfunction per load.
//
//  After:
//    • OnGameFinishedLoadingFinalize fires during load → we write the
//      backing fields → vanilla's load logic creates fishing areas at
//      the new radius naturally → fishermen start at the correct radius
//      from the first tick. No "looking for shore" state at all.
//
//  Runtime mode-change still goes through FishingShackEnhancement.ApplyMode
//  and legitimately triggers a rescan (the radius is changing mid-play).
//  At load time, no rescan is needed because the game hasn't created
//  fishing areas yet when we write the field.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    internal static class FishingShackLoadPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static void Register(HarmonyLib.Harmony harmony)
        {
            try
            {
                Type fsType = AccessTools.TypeByName("FishingShack");
                if (fsType == null)
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] FishingShackLoadPatches: FishingShack type not found.");
                    return;
                }

                // Try several method names that typically fire during game-load
                // completion. MD succeeded with OnGameFinishedLoadingFinalize on
                // WagonShop, but FishingShack may use a different name. First
                // match wins.
                string[] candidates = {
                    "OnGameFinishedLoadingFinalize",
                    "OnGameFinishedLoading",
                    "OnLoadedAsync",
                    "OnLoaded",
                    "PostLoad",
                    "FinishLoad",
                    "FinalizeLoading",
                    // FishingShack is a residence — try residence-flavored hooks
                    "OnResidenceLoaded",
                    "OnResidencyLoaded",
                    "OnShelterLoaded",
                    "OnOccupantsLoaded",
                    "FinalizeResidence",
                    // Unity lifecycle — last resort
                    "Start",
                };

                MethodInfo method = null;
                string matchedName = null;
                foreach (var name in candidates)
                {
                    Type walk = fsType;
                    while (walk != null && method == null)
                    {
                        // Skip System.Object / UnityEngine.Object / MonoBehaviour —
                        // methods there (Finalize, ToString, etc.) aren't our target
                        if (walk == typeof(object) ||
                            walk == typeof(UnityEngine.Object) ||
                            walk == typeof(MonoBehaviour) ||
                            walk == typeof(Component))
                        {
                            break;
                        }

                        var m = walk.GetMethod(name, AllInstance | BindingFlags.DeclaredOnly,
                            null, Type.EmptyTypes, null);
                        if (m != null)
                        {
                            method = m;
                            matchedName = $"{walk.Name}.{name}";
                            break;
                        }
                        walk = walk.BaseType;
                    }
                    if (method != null) break;
                }

                if (method == null)
                {
                    // Dump DeclaredOnly methods on FishingShack so we can find
                    // the right hook next pass.
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] FishingShackLoadPatches: no suitable lifecycle hook found. " +
                        "Dumping FishingShack declared-only methods to help identify:");
                    foreach (var m in fsType.GetMethods(AllInstance | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsSpecialName) continue;
                        if (m.GetParameters().Length > 0) continue;
                        WardenOfTheWildsMod.Log.Msg(
                            $"[WotW]   {m.ReturnType.Name} {m.Name}()");
                    }
                    return;
                }

                var postfix = new HarmonyMethod(
                    typeof(FishingShackLoadPatches), nameof(LoadFinalizePostfix));
                harmony.Patch(method, postfix: postfix);

                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] FishingShackLoadPatches: patched {matchedName}");

                // Save/Load patches: persist Creeler mode across save/reload
                RegisterSaveLoadPatches(harmony, fsType);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Error(
                    $"[WotW] FishingShackLoadPatches.Register failed: {ex}");
            }
        }

        /// <summary>
        /// Patches FishingShack.Save (append mode byte) and FishingShack.Load
        /// (read mode byte, populate SavedModes). Backward-compatible with
        /// pre-mod saves: if the reader runs out of data, we catch and default
        /// to Angler.
        /// </summary>
        private static void RegisterSaveLoadPatches(HarmonyLib.Harmony harmony, Type fsType)
        {
            try
            {
                // Use AccessTools.Method with explicit param types — more robust than
                // GetMethod(name, BindingFlags) alone, which can return null for virtual
                // overrides if binding-flag interaction is subtle.
                var saveMethod = AccessTools.Method(fsType, "Save", new[] { typeof(ES2Writer) });
                if (saveMethod != null)
                {
                    harmony.Patch(saveMethod, postfix: new HarmonyMethod(
                        typeof(FishingShackLoadPatches), nameof(SavePostfix)));
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] FishingShackLoadPatches: patched {saveMethod.DeclaringType.Name}.Save (mode persistence)");
                }
                else
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] FishingShackLoadPatches: FishingShack.Save(ES2Writer) not found.");
                }

                var loadMethod = AccessTools.Method(fsType, "Load", new[] { typeof(ES2Reader) });
                if (loadMethod != null)
                {
                    harmony.Patch(loadMethod, postfix: new HarmonyMethod(
                        typeof(FishingShackLoadPatches), nameof(LoadPostfix)));
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] FishingShackLoadPatches: patched {loadMethod.DeclaringType.Name}.Load (mode persistence)");
                }
                else
                {
                    WardenOfTheWildsMod.Log.Warning(
                        "[WotW] FishingShackLoadPatches: FishingShack.Load(ES2Reader) not found.");
                }
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] RegisterSaveLoadPatches: {ex}");
            }
        }

        private static void SavePostfix(object __instance, ES2Writer writer)
        {
            try
            {
                if (!(__instance is Component comp)) return;
                var enh = comp.GetComponent<FishingShackEnhancement>();
                int modeInt = enh != null ? (int)enh.Mode : (int)FishingShackMode.Angler;
                writer.Write(modeInt);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] FishingShackSavePostfix: {ex.Message}");
            }
        }

        private static void LoadPostfix(object __instance, ES2Reader reader)
        {
            try
            {
                if (!(__instance is Component comp)) return;
                int modeInt = reader.Read<int>();

                // Validate: must be a known enum value. Legacy saves (written
                // before the Save patch landed) don't have our appended byte —
                // the reader either throws OR reads garbage from the next
                // bytes in the stream. Validation catches the garbage case.
                if (modeInt != (int)FishingShackMode.Angler &&
                    modeInt != (int)FishingShackMode.Creeler)
                {
                    WardenOfTheWildsMod.Log.Msg(
                        $"[WotW] FishingShackLoadPostfix: '{comp.gameObject.name}' " +
                        $"invalid mode={modeInt} (legacy save), defaulting to Angler. " +
                        $"Saving this game will write proper data for next load.");
                    return;
                }

                var mode = (FishingShackMode)modeInt;
                FishingShackEnhancement.SetSavedModeForPosition(
                    comp.transform.position, mode);
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] FishingShackLoadPostfix: '{comp.gameObject.name}' " +
                    $"restored mode={mode}");
            }
            catch (Exception ex)
            {
                // Reader ran out of bytes — cleanest legacy signal.
                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] FishingShackLoadPostfix: no mode byte in save (legacy), " +
                    $"defaulting to Angler. ({ex.GetType().Name})");
            }
        }

        /// <summary>
        /// Applies WotW's fishing-shack tuning (storage cap, max workers,
        /// tech-gated radius) at the moment the game finishes loading the
        /// building — BEFORE fishing areas are created and fishermen start
        /// working. No rescan needed because we're inside the load flow.
        ///
        /// After applying the radius we also explicitly call CreateFishingAreas().
        /// Vanilla's flow defers that call to OnGameReadyToPlay /
        /// OnGameFinishedLoading which can fire tens of seconds later — producing
        /// a visible 30-45s window where fishermen idle because no areas exist.
        /// Pre-creating here eliminates that wait. Vanilla's later call remains
        /// harmless (it clears + rebuilds with the same values).
        /// </summary>
        private static void LoadFinalizePostfix(object __instance)
        {
            try
            {
                if (!WardenOfTheWildsMod.FishingOverhaulEnabled.Value) return;

                // Only fishing shacks — the method is on Building, applies to many
                if (!(__instance is Building building)) return;
                if (building.GetType().Name != "FishingShack") return;

                // 1. Storage capacity (baseline, always)
                SetStorageCap(building, (uint)WardenOfTheWildsMod.FishingShackStorageCap.Value);

                // 2. Max workers = 2 (baseline, always)
                SetMaxWorkers(building, 2);

                // 3. Tech-gated radius: 60u if Sustainable Fishing researched, else 30u
                WardenOfTheWildsMod.RefreshFishingTechState();
                if (WardenOfTheWildsMod.SustainableFishingResearched)
                {
                    float mult = WardenOfTheWildsMod.CreelerRadiusMult.Value;
                    float newRadius = 30f * mult;
                    SetRadius(building, newRadius);
                }

                // 4. Force fishing-area creation now so fishermen can start the
                //    moment Start fires — don't wait for the deferred event.
                ForceCreateFishingAreas(building);

                WardenOfTheWildsMod.Log.Msg(
                    $"[WotW] FishingShackLoadPatches: applied to '{building.gameObject.name}' " +
                    $"(tech={WardenOfTheWildsMod.SustainableFishingResearched})");
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] FishingShackLoadPatches.Postfix: {ex.Message}");
            }
        }

        private static void ForceCreateFishingAreas(Building building)
        {
            try
            {
                var method = building.GetType().GetMethod("CreateFishingAreas",
                    AllInstance, null, Type.EmptyTypes, null);
                method?.Invoke(building, null);
            }
            catch (Exception ex)
            {
                WardenOfTheWildsMod.Log.Warning(
                    $"[WotW] ForceCreateFishingAreas: {ex.Message}");
            }
        }

        // ── Direct field writes (no property setters, no CreateFishingAreas) ──

        private static void SetStorageCap(Building building, uint target)
        {
            var field = building.GetType().GetField("fishStorageCapacity", AllInstance);
            if (field == null || field.FieldType != typeof(uint)) return;
            uint current = (uint)field.GetValue(building);
            if (current == target) return;
            field.SetValue(building, target);
        }

        private static void SetMaxWorkers(Building building, int target)
        {
            var maxField = FindBackingField(building.GetType(), "maxWorkers");
            if (maxField == null) return;
            int current = (int)maxField.GetValue(building);
            if (current < target)
                maxField.SetValue(building, target);

            if (building.userDefinedMaxWorkers < target)
                building.userDefinedMaxWorkers = target;
        }

        private static void SetRadius(Building building, float target)
        {
            // 1. FishingShack._fishingRadius backing field — drives logic
            var radiusField = building.GetType().GetField("_fishingRadius", AllInstance);
            if (radiusField != null && radiusField.FieldType == typeof(float))
            {
                float current = (float)radiusField.GetValue(building);
                if (!Mathf.Approximately(current, target))
                    radiusField.SetValue(building, target);
            }

            // 2. WorkArea's SelectionCircle.<radius>k__BackingField — drives visual
            var wa = building.GetComponent<WorkArea>();
            if (wa == null) return;
            var scField = typeof(WorkArea).GetField("selectionCircle", AllInstance);
            var sc = scField?.GetValue(wa) as SelectionCircle;
            if (sc == null) return;
            var visualField = sc.GetType().GetField("<radius>k__BackingField", AllInstance);
            if (visualField != null && visualField.FieldType == typeof(float))
            {
                visualField.SetValue(sc, target);
                try { sc.CreateEdgeObjects(); } catch { }
            }
        }

        private static FieldInfo FindBackingField(Type startType, string propertyName)
        {
            string backingName = $"<{propertyName}>k__BackingField";
            Type t = startType;
            while (t != null)
            {
                var field = t.GetField(backingName, AllInstance);
                if (field != null) return field;
                t = t.BaseType;
            }
            return null;
        }
    }
}
