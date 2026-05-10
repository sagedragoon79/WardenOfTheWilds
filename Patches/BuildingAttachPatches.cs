using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using WardenOfTheWilds.Components;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Event-driven attachment of WotW enhancement components to vanilla
    /// HunterBuilding / FishingShack / SmokeHouse instances.
    ///
    /// Background (v1.0.10 stutter fix retrospective):
    ///   The original mod attached enhancements via a polling loop in
    ///   <c>WardenOfTheWildsMod.LateInit</c>. Phase 1 polled every 3
    ///   game-seconds for ~95s, Phase 2 polled every 30 game-seconds
    ///   forever. Each poll did <c>FindObjectsOfType</c> for all three
    ///   building types. In late-game saves the scan was expensive
    ///   enough to produce a visible stutter every 30s (or 15s at 2x
    ///   speed because <c>WaitForSeconds</c> uses scaled time).
    ///
    ///   v1.0.10 capped the bleeding by switching Phase 2 to
    ///   <c>WaitForSecondsRealtime(60f)</c> and caching resolved Types,
    ///   but the 60s tick was still perceptible.
    ///
    /// v1.0.11 — final fix:
    ///   Patch <c>Building.Awake()</c> via Harmony. The postfix runs
    ///   once per building when Unity instantiates it (whether from a
    ///   save load or from the player constructing one mid-session) and
    ///   AddComponents the matching enhancement if the building type
    ///   matches and the relevant feature flag is enabled. No polling,
    ///   no FindObjectsOfType in steady-state.
    ///
    ///   We patch the shared <c>Building</c> base class (one patch
    ///   covers all three subtypes) and dispatch in the postfix using
    ///   <c>is</c> checks against the resolved Types. Since Building
    ///   has many subclasses (storage, residence, etc.) the dispatch
    ///   keeps the cost to three reference checks per non-matching
    ///   building — negligible compared to the previous scan cost.
    ///
    /// Catch-up:
    ///   If types resolve too late (e.g. the patch is applied after
    ///   the save scene has already finished its Awake pass) we do a
    ///   one-shot <c>FindObjectsOfType</c> sweep at scene-loaded time
    ///   inside <c>WardenOfTheWildsMod.LateInit</c>. That sweep runs
    ///   exactly once and then exits — no loop.
    /// </summary>
    internal static class BuildingAttachPatches
    {
        private static bool _applied;
        private static Type _hunterType;
        private static Type _fishType;
        private static Type _smokeType;

        /// <summary>
        /// Wires the Building.Awake postfix. Idempotent — safe to call
        /// repeatedly. Resolves the three target Types via assembly walk
        /// once; if any are missing (modded build, name change, etc.)
        /// the corresponding dispatch arm is silently skipped.
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            if (_applied) return;

            ResolveTypes();

            // Patch the Building base class. Awake is called once per
            // instance by Unity, before Start, before the first frame.
            // Patching the base means we get a single Harmony patch that
            // fires for all subclass instances — much lower transpile
            // overhead than patching each subtype separately.
            try
            {
                Type buildingType = ResolveType("Building");
                if (buildingType == null)
                {
                    MelonLogger.Warning(
                        "[WotW] BuildingAttachPatches: Building type not found — " +
                        "falling back to legacy polling.");
                    return;
                }

                MethodInfo awake = buildingType.GetMethod(
                    "Awake",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);

                // Some Unity classes don't define Awake explicitly — try Start.
                MethodInfo target = awake ?? buildingType.GetMethod(
                    "Start",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);

                if (target == null)
                {
                    MelonLogger.Warning(
                        "[WotW] BuildingAttachPatches: no Awake/Start on Building — " +
                        "falling back to legacy polling.");
                    return;
                }

                MethodInfo postfix = typeof(BuildingAttachPatches).GetMethod(
                    nameof(BuildingAwakePostfix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                harmony.Patch(target, postfix: new HarmonyMethod(postfix));

                _applied = true;
                MelonLogger.Msg(
                    $"[WotW] BuildingAttachPatches: patched Building.{target.Name}() — " +
                    $"hunter={_hunterType != null}, fish={_fishType != null}, " +
                    $"smoke={_smokeType != null}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] BuildingAttachPatches.Apply: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix on <c>Building.Awake</c>. Dispatches to the correct
        /// enhancement based on runtime type. Hot path — keep cheap.
        /// </summary>
        private static void BuildingAwakePostfix(Component __instance)
        {
            if (__instance == null) return;

            try
            {
                Type t = __instance.GetType();
                GameObject go = __instance.gameObject;
                if (go == null) return;

                // Hunter
                if (_hunterType != null
                    && _hunterType.IsAssignableFrom(t)
                    && WardenOfTheWildsMod.HunterOverhaulEnabled.Value
                    && go.GetComponent<HunterCabinEnhancement>() == null)
                {
                    go.AddComponent<HunterCabinEnhancement>();
                    return;
                }

                // Fishing
                if (_fishType != null
                    && _fishType.IsAssignableFrom(t)
                    && WardenOfTheWildsMod.FishingOverhaulEnabled.Value
                    && go.GetComponent<FishingShackEnhancement>() == null)
                {
                    go.AddComponent<FishingShackEnhancement>();
                    return;
                }

                // Smokehouse
                if (_smokeType != null
                    && _smokeType.IsAssignableFrom(t)
                    && WardenOfTheWildsMod.SmokehouseOverhaulEnabled.Value
                    && go.GetComponent<SmokehouseEnhancement>() == null)
                {
                    go.AddComponent<SmokehouseEnhancement>();
                    return;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] BuildingAwakePostfix: {ex.Message}");
            }
        }

        // ── Type resolution ────────────────────────────────────────────────
        private static void ResolveTypes()
        {
            _hunterType = ResolveType("HunterBuilding") ?? ResolveType("HunterCabin");
            _fishType   = ResolveType("FishingShack")   ?? ResolveType("FishermanShack");
            _smokeType  = ResolveType("SmokeHouse")     ?? ResolveType("Smokehouse");
        }

        private static Type ResolveType(string name)
        {
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        Type t = asm.GetType(name);
                        if (t != null) return t;
                    }
                    catch { /* swallow ill-behaved assemblies */ }
                }
            }
            catch { }
            return null;
        }
    }
}
