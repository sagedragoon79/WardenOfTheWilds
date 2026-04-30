using HarmonyLib;
using MelonLoader;
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using WardenOfTheWilds.Components;
using WardenOfTheWilds.Systems;

// ─────────────────────────────────────────────────────────────────────────────
//  HunterCombatPatches
//  Harmony patches for the hunter shooting / kiting / combat system.
//
//  METHOD NAMES CONFIRMED (from dump 26-4-19):
//    Villager.OnCombatTargetSet(IDamageable, IDamageable, TargetSourceIdentifier)
//    Villager.OnPerformedAttack(GameObject, TeamDefinition)
//    Villager.OnAttacked(Single, GameObject, DamageType)
//    Villager.OnCombatDeath(Single, GameObject, DamageType)   ← via Character base
//    VillagerHealth.get_health()                              ← 0–1 normalized HP
//    Villager.get_villagerHealth()                            ← returns VillagerHealth
//    HunterBuilding.isAmmoMissingFromStorages                 ← ammo stock flag
//    CombatManager._hunterMoveSpeedBonus                      ← field for speed injection
//    All animals: Single attackTime                           ← reload/attack interval
//
//  MULTI-PREDATOR RETREAT (all hunters, all tiers):
//    OnAttacked → track per-hunter rolling window of unique dangerous attackers.
//    If 2+ distinct Wolf/Boar/Bear instances have hit the same hunter within the
//    AttackerWindowSeconds window → trigger a full retreat away from their centroid.
//    One predator = normal hunting. Two predators = overwhelmed, run.
//
//  SINGLE-PREDATOR KITING (post-shot, all hunters):
//    OnPerformedAttack → after firing, back up slightly during the reload window
//    to maintain bow range. Only when the target is actively charging.
//
//  HEALTH CHECK (universal):
//    OnCombatTargetSet → if hunter HP < threshold when acquiring a dangerous target,
//    refuse engagement and retreat regardless of predator count.
//
//  DISCOVERY DUMP:
//    DumpCombatMethods() fires once per session at OnMapLoaded.
//    Disable by setting _dumpDone = true after all needed fields are confirmed.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Patches
{
    public static class HunterCombatPatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // ── Per-hunter retreat rate limiting ──────────────────────────────────
        // Key = RuntimeHelpers.GetHashCode(villager component)
        // Value = Time.time of last retreat trigger
        // Prevents hammering SetDestination when the hunter is already backing away.
        private static readonly Dictionary<int, float> _lastKiteTimes =
            new Dictionary<int, float>();
        private const float KiteRateLimit = 3f;

        // ── Per-hunter proactive engagement rate limiting ─────────────────────
        // Separate from KiteRateLimit — proactive scan fires every 0.5s and must
        // not be gated by the 3s retreat rate limit.
        // 0.4s limit: lets every other tick through (0.5s scan / 0.4s gate ≈ 1 fire/s)
        // while still avoiding per-frame spam if the coroutine runs hot.
        private static readonly Dictionary<int, float> _lastProactiveTimes =
            new Dictionary<int, float>();
        private const float ProactiveRateLimit = 0.4f;

        // ── Per-building worker registry ──────────────────────────────────────
        // Key = RuntimeHelpers.GetHashCode(HunterBuilding component)
        // Value = set of hunter Components assigned to that building
        // Populated by OnResidentAdded / pruned by OnResidentRemoved patches.
        // Used by the wounded-escort feature to find nearby cabin-mates.
        private static readonly Dictionary<int, HashSet<Component>> _buildingWorkers =
            new Dictionary<int, HashSet<Component>>();

        // Reverse map: hunter hash → building hash (for fast cabin-mate lookup)
        private static readonly Dictionary<int, int> _hunterBuildingKey =
            new Dictionary<int, int>();

        // Building hash → building Component (for work radius read in proactive scan)
        private static readonly Dictionary<int, Component> _buildingComponents =
            new Dictionary<int, Component>();

        // Max distance a cabin-mate must be within to respond to a Wounded call.
        // Hunters further than this are busy on their own patrol — don't interrupt.
        private const float WoundedEscortSearchRadius = 80f;

        // ── Building caches ────────────────────────────────────────────────────
        // IsAnyHunter() and IsHuntingLodgeHunter() are called from postfixes that
        // fire for EVERY villager. Without caching, FindObjectsOfType runs hundreds
        // of times per second → freeze.
        //
        // Two-level cache:
        //   L1: per-villager bool result (TTL = 10s)
        //   L2: HunterBuilding list      (TTL =  5s)
        // Parallel Dicts instead of ValueTuple (not in net46).

        // Per-villager assigned-building cache (villagerHash → HunterBuilding or null).
        // Populated by FindAssignedHunterBuilding via villager.residence. TTL 10s.
        private static readonly Dictionary<int, Component?> _assignedBuildingCache =
            new Dictionary<int, Component?>();
        private static readonly Dictionary<int, float> _assignedBuildingCacheExpiry =
            new Dictionary<int, float>();

        // L1 — IsHuntingLodgeHunter (T2 HuntingLodge path only)
        private static readonly Dictionary<int, bool>  _hunterLodgeCacheResult =
            new Dictionary<int, bool>();
        private static readonly Dictionary<int, float> _hunterLodgeCacheExpiry =
            new Dictionary<int, float>();
        private static float _lodgeBuildingCacheExpiry = -1f;
        private static readonly List<CachedBuilding> _cachedLodgeBuildings =
            new List<CachedBuilding>();

        private struct CachedBuilding
        {
            public Component Building;
            public float WorkRadius;
            public CachedBuilding(Component building, float workRadius)
            {
                Building   = building;
                WorkRadius = workRadius;
            }
        }

        private const float HunterCacheTTL   = 10f;
        private const float BuildingCacheTTL =  5f;

        // ── Single-predator LOW HP grace window ──────────────────────────────
        // When a single hit drops the hunter below the HP threshold with only
        // one predator present, we suppress the retreat and record the time.
        // A second hit below threshold within LowHpGraceSeconds triggers retreat.
        // Prevents a single lucky bear swipe from instantly sending a full-life
        // hunter home. With 2+ predators (OVERWHELMED), grace does not apply.
        private static readonly Dictionary<int, float> _hunterFirstLowHpTime =
            new Dictionary<int, float>();
        private const float LowHpGraceSeconds = 8f;

        // ── Scratch list — reused to avoid per-hit GC allocations ────────────
        // RecordEngagementAndCheckRetreat prunes stale attacker entries on every
        // wolf hit. Allocating a new List<int> each time creates constant GC
        // pressure that manifests as periodic lag spikes every few seconds.
        private static readonly List<int> _staleKeyScratch = new List<int>();

        // ── Multi-predator attacker tracking ──────────────────────────────────
        // Outer key = hunter hash (RuntimeHelpers.GetHashCode)
        // Inner key = predator hash
        // Inner value = Time.time of last hit from that predator
        //
        // When 2+ distinct dangerous animals (Wolf/Boar/Bear) have entries within
        // AttackerWindowSeconds, the hunter is overwhelmed → trigger full retreat.
        //
        // Design rationale:
        //   One predator  = normal encounter, hunter can handle it, don't interfere.
        //   Two predators = hunter walked into 2 aggro zones simultaneously; retreat.
        //   Wolf packs: each wolf only aggros independently when the hunter enters
        //   ITS range — so 2 hits from different wolves means the hunter is deep
        //   in the pack, not just passing one wolf near another.
        private static readonly Dictionary<int, Dictionary<int, float>> _hunterAttackers =
            new Dictionary<int, Dictionary<int, float>>();
        // Parallel dict storing live Component refs for centroid calculation.
        // Keyed identically to _hunterAttackers (hunter hash → predator hash → Component).
        private static readonly Dictionary<int, Dictionary<int, Component>> _hunterAttackerRefs =
            new Dictionary<int, Dictionary<int, Component>>();
        private const float AttackerWindowSeconds = 5f;

        // ── Health via VillagerHealth component ────────────────────────────────
        // CONFIRMED from dump (26-4-19):
        //   Villager.get_villagerHealth() → VillagerHealth component
        //   VillagerHealth.get_health()   → Single, already 0–1 normalized
        // No maxHealth field exists — the value IS the fraction.
        // Property refs cached after first lookup.
        private static PropertyInfo? _villagerHealthProp = null;  // Villager.villagerHealth
        private static PropertyInfo? _healthValueProp    = null;  // VillagerHealth.health
        private static bool          _healthPropSearchDone = false;

        // ── Arrow / ammo tracking ──────────────────────────────────────────────
        // CONFIRMED from dump (26-4-19): HunterBuilding.isAmmoMissingFromStorages
        // Field references cached after first lookup — no repeated GetField walks.
        private static FieldInfo? _ammoMissingField   = null;
        private static FieldInfo? _weaponMissingField = null;
        private static bool       _ammoFieldSearchDone = false;

        // ── forceRetreatNextCheck field cache ─────────────────────────────────
        // Cached after first successful lookup — avoids repeated GetField in
        // SetMovementTarget which fires on every retreat.
        private static FieldInfo? _forceRetreatField     = null;
        private static bool       _forceRetreatFieldDone = false;

        // ── Wounded escort state tracking ─────────────────────────────────────
        // CONFIRMED enum values (dump 26-4-19):
        //   WaitingOnEscort    = 0  ← villager IS wounded, waiting for escort to arrive
        //   BeingEscorted      = 1  ← escort has arrived and is walking with them
        //   ArrivedAtDestination = 2  ← reached healer / cabin
        //
        // The setter fires when a villager BECOMES wounded (set to WaitingOnEscort=0).
        // It is NOT called on healthy villagers — only when the wound event fires.
        //
        // Trigger: setter called with WaitingOnEscort (0) AND either:
        //   a) villager not yet in tracker (first wound this session) — sentinel = -1
        //   b) previous state was non-zero (re-wound after prior escort cycle)
        //
        // Uses -1 as the "never set" sentinel since dict default int (0) collides
        // with WaitingOnEscort and would suppress the first-wound trigger.
        private static readonly Dictionary<int, int> _hunterWoundedStateTracker =
            new Dictionary<int, int>();

        // ── Vanilla retreat threshold — lazy read ─────────────────────────────
        // VillagerRetreatSearchEntry instances aren't available at OnMapLoaded
        // (villagers not yet spawned). Read on first combat event instead.
        private static bool _vanillaThresholdRead = false;

        private static void TryReadVanillaRetreatThreshold(Component hunter)
        {
            if (_vanillaThresholdRead) return;
            _vanillaThresholdRead = true;
            try
            {
                Type? retreatType = FindTypeForDump("VillagerRetreatSearchEntry");
                if (retreatType == null) { MelonLogger.Msg("[WotW] VillagerRetreatSearchEntry type not found"); return; }

                // Try to get instance from this hunter's task components
                var taskComp = hunter.GetComponents<Component>();
                object? inst = null;
                foreach (var c in taskComp)
                    if (c != null && retreatType.IsInstanceOfType(c)) { inst = c; break; }

                // Fallback: scene-wide search (hunter is loaded at this point)
                if (inst == null)
                {
                    var found = UnityEngine.Object.FindObjectsOfType(retreatType);
                    if (found != null && found.Length > 0) inst = found[0];
                }

                if (inst == null) { MelonLogger.Msg("[WotW] VillagerRetreatSearchEntry: no instance found at first combat event"); return; }

                var lifeF   = retreatType.GetField("lifePercToRetreat",          BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var injuredF= retreatType.GetField("onlyRetreatWhenInjured",     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var timeF   = retreatType.GetField("timeSinceLastAttackToRetreat",BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                MelonLogger.Msg("[WotW] ── Vanilla VillagerRetreatSearchEntry live values ──");
                MelonLogger.Msg($"[WotW]   lifePercToRetreat          = {lifeF?.GetValue(inst) ?? "N/A"}");
                MelonLogger.Msg($"[WotW]   onlyRetreatWhenInjured     = {injuredF?.GetValue(inst) ?? "N/A"}");
                MelonLogger.Msg($"[WotW]   timeSinceLastAttackToRetreat = {timeF?.GetValue(inst) ?? "N/A"}");
                MelonLogger.Msg($"[WotW]   (our threshold: {WardenOfTheWildsMod.HunterLowHealthThreshold.Value:P0})");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[WotW] Vanilla threshold read failed: {ex.Message}");
            }
        }

        // ── Dump one-shot flag ─────────────────────────────────────────────────
        // Dump runs once per game session (first OnMapLoaded).
        // Moved out of ApplyPatches (init phase) because MelonLoader throttles
        // rapid log output during OnInitializeMelon — messages are silently dropped.
        // OnMapLoaded fires at scene load where logging is reliable.
        //
        // SET TO FALSE TO RE-ENABLE — flip when new types are added to DumpCombatMethods.
        // Re-set to true once task system + VillagerHealth fields are confirmed.
        private static bool _dumpDone = true; // ← DONE: HuntingAttackTargetTask/Task/HuntSubTask ctors confirmed

        public static void OnMapLoaded()
        {
            _lastKiteTimes.Clear();
            _assignedBuildingCache.Clear();
            _assignedBuildingCacheExpiry.Clear();
            _hunterLodgeCacheResult.Clear();
            _hunterLodgeCacheExpiry.Clear();
            _cachedLodgeBuildings.Clear();
            _lodgeBuildingCacheExpiry = -1f;
            _hunterAttackers.Clear();
            _hunterAttackerRefs.Clear();
            _buildingWorkers.Clear();
            _hunterBuildingKey.Clear();
            _buildingComponents.Clear();
            _lastProactiveTimes.Clear();
            _hunterWoundedStateTracker.Clear();
            // Health property refs intentionally NOT cleared — discovered type
            // hierarchy doesn't change between map loads.

            // Run discovery dump once per session at scene load (not init).
            // Init-time logging is throttled by MelonLoader — this is the
            // first point where MelonLogger.Msg reliably reaches the log file.
            if (!_dumpDone)
            {
                _dumpDone = true;
                DumpCombatMethods();
            }
        }

        // ── Danger proximity watcher — REMOVED ────────────────────────────────
        /// <summary>
        /// Previously ran a proximity scan every 2 seconds looking for any dangerous
        /// animal within HunterDangerRadius. Replaced by the multi-predator attacker
        /// window in OnAttackedPostfix — retreat now only fires when 2+ distinct
        /// dangerous animals have hit the same hunter within AttackerWindowSeconds.
        ///
        /// Reason for removal: the proximity check was too sensitive. It fired
        /// continuously while hunters worked near any predator, even during clean
        /// 1v1 encounters. Wolf AI also does NOT chain-aggro — each wolf only aggros
        /// independently when the hunter enters its own range — so a hunter hitting
        /// 2 aggro zones simultaneously is the genuine danger signal.
        ///
        /// Kept as stub so Plugin.cs coroutine start doesn't need changes.
        /// The coroutine exits immediately.
        /// </summary>
        public static System.Collections.IEnumerator DangerProximityWatcher()
        {
            // Replaced by multi-predator attacker window in OnAttackedPostfix.
            // Stub retained so Plugin.cs coroutine wiring doesn't need changes.
            yield break;
        }

        // ── Manual patch application ──────────────────────────────────────────
        public static void ApplyPatches(HarmonyLib.Harmony harmony)
        {
            if (!WardenOfTheWildsMod.HunterOverhaulEnabled.Value) return;
            if (!WardenOfTheWildsMod.HunterCombatEnabled.Value)
            {
                WardenOfTheWildsMod.Log.Msg(
                    "[WotW] HunterCombatPatches SKIPPED (HunterCombatEnabled=false)");
                return;
            }

            // ── Phase 0: Discovery dump ───────────────────────────────────────
            // Dump is triggered in OnMapLoaded (not here). MelonLoader silently
            // drops rapid log messages during the OnInitializeMelon init phase,
            // so running it here produces zero output in the log.

            // ── Patch 1: Combat death tracking ───────────────────────────────
            // Correct method name (discovered 2026-04-21) is OnDeath, not
            // OnCombatDeath. It lives on Character (protected virtual) and
            // Villager overrides it. Signature matches our postfix:
            // (float damageTaken, GameObject damageCauser, DamageType damageType)
            PatchMethodIfFound(harmony, "Villager", "OnDeath",
                nameof(OnCombatDeathPostfix), isPostfix: true);

            // ── Patch 2: Kiting intercept — target acquisition ────────────────
            // OnCombatTargetSet(IDamageable, IDamageable, TargetSourceIdentifier)
            // Fires when a villager acquires a new combat target.
            // We intercept here to redirect HuntingLodge hunters to a stand when
            // they target Wolf/Boar/Bear, instead of charging into melee range.
            if (WardenOfTheWildsMod.HuntingLodgeKitingEnabled.Value)
            {
                PatchMethodIfFound(harmony, "Villager", "OnCombatTargetSet",
                    nameof(OnCombatTargetSetPostfix), isPostfix: true);
            }

            // ── Patch 3: Post-shot hook — dog decoy + post-shot retreat ───────
            // OnPerformedAttack(GameObject, TeamDefinition)
            // Fires after the hunter releases an arrow/bolt.
            PatchMethodIfFound(harmony, "Villager", "OnPerformedAttack",
                nameof(OnPerformedAttackPostfix), isPostfix: true);

            // ── Patch 3b: BGH speed bonus reapply on occupation init ──────────
            // Vanilla VillagerOccupationHunter.Init() sets curOccupationalSpeedBonus
            // = combatManager.hunterMoveSpeedBonus (= 0.2), which clobbers our
            // BGH bonus whenever the hunter's occupation re-initializes. Postfix
            // re-applies the BGH multiplier so wolves can't catch up to T2
            // hunters mid-chase.
            PatchMethodIfFound(harmony, "VillagerOccupationHunter", "Init",
                nameof(HunterInitPostfix), isPostfix: true);

            // ── Patch 4a: Multi-predator retreat — AGGRO trigger ─────────────
            // OnCombatTargetSet is declared on AggressiveAnimal and inherited by
            // Wolf/Boar/Bear. Patching each subclass as well would chain the same
            // postfix 4× per aggro event — we patch only the declaring class.
            PatchMethodIfFound(harmony, "AggressiveAnimal", "OnCombatTargetSet",
                nameof(OnAnimalAggroPostfix), isPostfix: true);

            // ── Patch 4b: Multi-predator retreat — HIT fallback ───────────────
            // OnAttacked fires when the villager actually takes damage.
            // Feeds the same attacker window as the aggro patch — handles any
            // cases where OnCombatTargetSet fires on an unknown base type, plus
            // the low-HP check (wounded hunter flees even from a single predator).
            PatchMethodIfFound(harmony, "Villager", "OnAttacked",
                nameof(OnAttackedPostfix), isPostfix: true);

            // ── Patch 5: Worker registry + stale icon fix ─────────────────────
            // CONFIRMED from dump: HunterBuilding.OnResidentAdded/OnResidentRemoved
            // OnResidentAdded   → populate _buildingWorkers registry (cabin-mate lookup)
            // OnResidentRemoved → prune registry + fix stale occupation icon (vanilla bug)
            PatchMethodIfFound(harmony, "HunterBuilding", "OnResidentAdded",
                nameof(OnResidentAddedPostfix), isPostfix: true);
            PatchMethodIfFound(harmony, "HunterBuilding", "OnResidentRemoved",
                nameof(OnResidentRemovedPostfix), isPostfix: true);

            // ── Patch 6: Hunter buddy escort ──────────────────────────────────
            // CONFIRMED from dump: Villager.set_woundedEscortState(WoundedEscortState)
            // Fires when a villager's wounded-escort lifecycle state changes.
            //
            // We detect the 0 → non-zero transition (villager just became wounded).
            // If the villager is a hunter, find the nearest cabin-mate within
            // WoundedEscortSearchRadius and route them to escort the wounded hunter
            // back to the cabin, rather than waiting for a vanilla laborer escort
            // (which is blocked when threats are nearby).
            //
            // NOTE: WoundedEscortState is likely a nested enum type (e.g. Villager+
            //   WoundedEscortState). We don't match by name — we use 0 vs non-zero.
            PatchMethodIfFound(harmony, "Villager", "set_woundedEscortState",
                nameof(OnWoundedEscortStateSetPostfix), isPostfix: true);

            // ── Proactive threat scan ─────────────────────────────────────────
            // Injected at HuntSubTask.IsSubTaskValidToContinue — the exact moment
            // the hunt wander loop checks for prey. When a predator is in work
            // radius and the return would be true (keep wandering), we instead set
            // _villagerTarget = bear and return false (prey found) so the parent
            // HuntingAttackTargetTask transitions to attack mode.
            PatchMethodIfFound(harmony, "HuntSubTask", "IsSubTaskValidToContinue",
                nameof(HuntSubTaskIsValidContinuePostfix), isPostfix: true);

            // ── Patch: T1 melee discipline ────────────────────────────────────
            // Villager.OnIsMeleeAttack is invoked when deciding whether an attack
            // resolves as melee vs ranged. Postfix can unconditionally override
            // __result — more reliable than our prior delegate-subscribe path
            // (vanilla's multicast delegate could add handlers that returned
            // after ours, clobbering the gate decision).
            PatchMethodIfFound(harmony, "Villager", "OnIsMeleeAttack",
                nameof(OnIsMeleeAttackPostfix), isPostfix: true);

            MelonLogger.Msg("[WotW] HunterCombatPatches applied.");
        }

        // ── Generic patch helper ──────────────────────────────────────────────
        private static void PatchMethodIfFound(
            HarmonyLib.Harmony harmony,
            string className,
            string methodName,
            string postfixHandlerName,
            bool isPostfix)
        {
            Type? classType = FindType(className);
            if (classType == null)
            {
                MelonLogger.Msg($"[WotW] HunterCombatPatches: type '{className}' not found — skipping {methodName}.");
                return;
            }

            // Search for the method by name (any parameter signature)
            MethodInfo? method = null;
            foreach (var m in classType.GetMethods(AllInstance | AllStatic))
            {
                if (m.Name == methodName && m.DeclaringType == classType)
                {
                    method = m;
                    break;
                }
            }
            // Also try base-declared methods if not found on the type itself
            if (method == null)
                method = classType.GetMethod(methodName, AllInstance)
                      ?? classType.GetMethod(methodName, AllStatic);

            if (method == null)
            {
                MelonLogger.Msg($"[WotW] HunterCombatPatches: {className}.{methodName} not found — skipping.");
                return;
            }

            try
            {
                var handler = new HarmonyMethod(
                    typeof(HunterCombatPatches).GetMethod(postfixHandlerName, AllStatic));

                if (isPostfix)
                    harmony.Patch(method, postfix: handler);
                else
                    harmony.Patch(method, prefix: handler);

                MelonLogger.Msg(
                    $"[WotW] HunterCombatPatches: patched {className}.{methodName} " +
                    $"({(isPostfix ? "postfix" : "prefix")}: {postfixHandlerName})");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[WotW] HunterCombatPatches: failed to patch {className}.{methodName}: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  PATCH HANDLERS
        // ════════════════════════════════════════════════════════════════════════

        // ── 1. OnCombatDeath postfix ──────────────────────────────────────────
        /// <summary>
        /// Fires when a Character (villager, hunter) is killed in combat.
        /// Logs position and suspected attacker. Marks the area as a danger zone
        /// so the player can be informed.
        /// </summary>
        public static void OnCombatDeathPostfix(
            object __instance,
            float damageTaken,
            GameObject damageCauser,
            object damageType)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null) return;

                Vector3 pos = comp.transform.position;
                bool isHuntingLodge = IsHuntingLodgeHunter(comp);

                string causerName = damageCauser != null
                    ? damageCauser.name
                    : "unknown";

                MelonLogger.Msg(
                    $"[WotW] Combat death: '{comp.gameObject.name}' at {pos:F1} " +
                    $"(damage={damageTaken:F1}, cause='{causerName}', " +
                    $"HuntingLodge={isHuntingLodge})");

                // Post a hunter-themed notification to the event log directly.
                // We can't rely on vanilla's VillagerDiedEvent dispatch — combat
                // deaths appear to bypass that event, so neither our prefix on
                // UIEventLogWindow.OnVillagerDiedEvent nor vanilla's own "has
                // died" message fires.
                if (IsAnyHunter(comp))
                    PostHunterDeathNotification(comp, causerName);

                // Purge this villager's entries from all per-hunter registries
                // so long sessions don't accumulate stale dict entries for
                // villagers that no longer exist.
                PurgeDeadHunterFromRegistries(comp);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnCombatDeathPostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes the dead villager's hash key from every per-hunter dict we
        /// maintain, and also removes their Component reference from any
        /// building worker-set (since OnResidentRemoved may not fire for combat
        /// deaths that bypass the normal residence-release flow).
        /// </summary>
        private static void PurgeDeadHunterFromRegistries(Component hunter)
        {
            try
            {
                int vKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(hunter);

                _lastKiteTimes.Remove(vKey);
                _lastProactiveTimes.Remove(vKey);
                _hunterFirstLowHpTime.Remove(vKey);
                _hunterAttackers.Remove(vKey);
                _hunterAttackerRefs.Remove(vKey);
                _hunterWoundedStateTracker.Remove(vKey);
                _assignedBuildingCache.Remove(vKey);
                _assignedBuildingCacheExpiry.Remove(vKey);
                _hunterLodgeCacheResult.Remove(vKey);
                _hunterLodgeCacheExpiry.Remove(vKey);
                _kiteEndTime.Remove(vKey);
                _kiteTarget.Remove(vKey);
                _lastChaseBreakTimes.Remove(vKey);

                // Remove from building worker set (tied by building key) — also
                // drop the reverse lookup entry.
                if (_hunterBuildingKey.TryGetValue(vKey, out int bKey))
                {
                    if (_buildingWorkers.TryGetValue(bKey, out var workers))
                        workers.Remove(hunter);
                    _hunterBuildingKey.Remove(vKey);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PurgeDeadHunterFromRegistries: {ex.Message}");
            }
        }

        /// <summary>
        /// Posts a dramatic hunter-death entry to the UIEventLogWindow directly.
        /// Combat deaths don't trigger vanilla's VillagerDiedEvent dispatch, so
        /// our UIEventLogWindow.OnVillagerDiedEvent prefix never fires for this
        /// path. We post here from our OnDeath postfix instead.
        /// </summary>
        private static readonly string[] _hunterDeathFlavor = new[]
        {
            "{0}, a hunter of our people, has perished in the wilds (slain by {1}).",
            "A hunter has fallen in the hunt — {0}, taken by {1}.",
            "{0}, hunter of the frontier, was cut down by {1}.",
            "The hunter {0} has met their end. Cause: {1}.",
            "{0}, a hunter, has perished. The wilds took them ({1}).",
        };
        private static readonly System.Random _hunterDeathRng = new System.Random();

        private static void PostHunterDeathNotification(Component hunter, string causerName)
        {
            try
            {
                var window = UnityEngine.Object.FindObjectOfType<UIEventLogWindow>();
                if (window == null) return;

                var addMethod = typeof(UIEventLogWindow).GetMethod(
                    "AddEventToLog", AllInstance);
                if (addMethod == null) return;

                string name = hunter.gameObject.name;
                // Strip "Villager_Base(Clone) " prefix and #N suffix for readability
                var vilComp = hunter as Villager;
                if (vilComp != null && !string.IsNullOrEmpty(vilComp.villagerName))
                    name = vilComp.villagerName;

                string line = _hunterDeathFlavor[_hunterDeathRng.Next(_hunterDeathFlavor.Length)];
                string summary = string.Format(line, name, causerName);

                addMethod.Invoke(window, new object[] { summary, null });
                MelonLogger.Msg($"[WotW] Hunter death notified — {summary}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PostHunterDeathNotification: {ex.Message}");
            }
        }

        // ── 2. OnCombatTargetSet postfix (KITING CORE) ───────────────────────
        /// <summary>
        /// Fires when a villager acquires a new combat target.
        ///
        /// Kiting intercept:
        ///   1. Check: is this a HuntingLodge hunter?
        ///   2. Check: is the new target a dangerous animal (Wolf/Boar/Bear)?
        ///   3. Find nearest Hunting Stand/Blind within work radius.
        ///   4. If found, redirect hunter's movement to the stand BEFORE engaging.
        ///
        /// The hunter will still shoot at the animal once they arrive — vanilla AI
        /// handles that. The stand just ensures they engage from an advantageous
        /// position instead of charging into melee range.
        ///
        /// Rate-limited per hunter to avoid fighting the AI every tick.
        /// </summary>
        public static void OnCombatTargetSetPostfix(
            object __instance,
            object previousTarget,
            object newTarget,
            object sourceIdentifier)
        {
            if (newTarget == null) return;

            try
            {
                var hunter = __instance as Component;
                if (hunter == null) return;

                // Must be any hunter at all (any tier / path)
                if (!IsAnyHunter(hunter)) return;

                // Rate limit — don't fight the AI every time it re-evaluates
                int hunterKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(hunter);
                if (_lastKiteTimes.TryGetValue(hunterKey, out float last) &&
                    Time.time - last < KiteRateLimit)
                    return;

                Component? animalComp = ExtractComponent(newTarget);
                bool isDangerous = animalComp != null && IsDangerousAnimal(animalComp);

                // ── Health check (universal — all tiers / all paths) ──────────
                // A wounded hunter should disengage from any predator immediately,
                // not just back up for a reload window. Full 4× retreat distance.
                float hpThreshold = WardenOfTheWildsMod.HunterLowHealthThreshold.Value;
                if (hpThreshold > 0f && isDangerous)
                {
                    float hp = GetHunterHealthPercent(hunter);
                    if (hp < hpThreshold)
                    {
                        Vector3 hPos = hunter.transform.position;
                        Vector3 dir  = (hPos - animalComp!.transform.position).normalized;
                        if (dir == Vector3.zero) dir = Vector3.back;

                        float reload = AnimalBehaviorSystem.KitingCalculator
                                           .GetEffectiveReloadSeconds(hunter)
                                       / WardenOfTheWildsMod.HuntingLodgeBigGameShootMult.Value;
                        float speed  = AnimalBehaviorSystem.KitingCalculator.GetEffectiveHunterSpeed(0f);
                        float dist   = AnimalBehaviorSystem.KitingCalculator
                                           .OptimalRetreatDistance(reload, speed) * 4f;

                        SetMovementTarget(hunter, hPos + dir * dist);
                        _lastKiteTimes[hunterKey] = Time.time;

                        MelonLogger.Msg(
                            $"[WotW] Low HP ({hp:P0}) — '{hunter.gameObject.name}' " +
                            $"refusing engagement with '{animalComp.gameObject.name}', " +
                            $"retreating {dist:F1}u");
                        return;
                    }
                }

                // ── Ammo / weapon stock check — DISABLED ─────────────────────
                // isAmmoMissingFromStorages reflects the BUILDING's arrow reserves,
                // not the hunter's carried quiver. Fires true even when the hunter
                // has a full load (building gave out its last batch to this hunter).
                // Result: hunter redirects to cabin on every target acquisition.
                // TODO: replace with carried-arrow count check via hunter's
                //   equipmentManager → _equipmentStorage → GetItemCount(Arrow=35)
                //   once field names are confirmed from dump.

                // ── BGH targeting priority (HuntingLodge path only) ──────────
                // Bear(3) > Wolf(2) > Boar(1) > Deer(0).
                // When a BGH hunter acquires a low-priority target, scan for a
                // higher-priority animal within work radius and redirect toward it.
                // Falls through to kiting on the NEXT target acquisition.
                if (WardenOfTheWildsMod.HuntingLodgeKitingEnabled.Value &&
                    isDangerous && IsHuntingLodgeHunter(hunter))
                {
                    float bghRadius = 100f * WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value;
                    int   curPri    = GetAnimalPriority(animalComp!);
                    if (curPri < 3) // Already targeting Bear — nothing higher
                    {
                        var betterTarget = FindHigherPriorityAnimal(
                            hunter.transform.position, animalComp!, curPri, bghRadius);
                        if (betterTarget != null)
                        {
                            int newPri = GetAnimalPriority(betterTarget);
                            SetMovementTarget(hunter, betterTarget.transform.position);
                            _lastKiteTimes[hunterKey] = Time.time;
                            MelonLogger.Msg(
                                $"[WotW] BGH priority: '{hunter.gameObject.name}' " +
                                $"{animalComp!.GetType().Name}(pri={curPri}) → " +
                                $"{betterTarget.GetType().Name}(pri={newPri})");
                            return;
                        }
                    }
                }

                // ── Kiting intercept (HuntingLodge path only) ─────────────────
                if (!WardenOfTheWildsMod.HuntingLodgeKitingEnabled.Value) return;
                if (!isDangerous) return;
                if (!IsHuntingLodgeHunter(hunter)) return;

                // Find the nearest Hunting Stand or Blind within work radius
                Component? cabin = FindCabinForHunter(hunter);
                float workRadius = 100f * WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value;
                Vector3 cabinPos = cabin != null
                    ? cabin.transform.position
                    : hunter.transform.position;

                Vector3? kiteDest = GetKitingDestination(
                    hunter.transform.position, cabinPos, workRadius);

                if (!kiteDest.HasValue) return;

                // Redirect hunter to the stand
                SetMovementTarget(hunter, kiteDest.Value);
                _lastKiteTimes[hunterKey] = Time.time;

                MelonLogger.Msg(
                    $"[WotW] Kite → '{hunter.gameObject.name}' targeting " +
                    $"'{animalComp!.gameObject.name}' ({animalComp.GetType().Name}), " +
                    $"redirecting to stand at {kiteDest.Value:F1}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnCombatTargetSetPostfix: {ex.Message}");
            }
        }

        // ── 3b. VillagerOccupationHunter.Init postfix (BGH speed re-apply) ────
        /// <summary>
        /// Vanilla's VillagerOccupationHunter.Init sets
        /// `curOccupationalSpeedBonus = combatManager.hunterMoveSpeedBonus`
        /// (≈ 0.2 default) every time a hunter's occupation initializes —
        /// worker rotation, occupation refresh, save load, etc. That overwrites
        /// our HuntingLodge × 1.20 bonus, leaving BGH hunters at vanilla
        /// baseline so wolves catch up mid-chase. Postfix re-applies the
        /// BGH bonus immediately after vanilla resets, scoped per villager.
        /// </summary>
        public static void HunterInitPostfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                // Get villager from the occupation. Field name 'villager' is
                // confirmed: VillagerOccupation has `public Villager villager`.
                var villagerField = __instance.GetType().GetField("villager",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (villagerField == null) return;
                var villager = villagerField.GetValue(__instance) as Villager;
                if (villager == null) return;

                if (!(villager.residence is HunterBuilding hb)) return;
                var enh = hb.GetComponent<HunterCabinEnhancement>();
                enh?.ApplySpeedBonusToVillager(villager);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterInitPostfix: {ex.Message}");
            }
        }

        // ── 3. OnPerformedAttack postfix ──────────────────────────────────────
        /// <summary>
        /// Fires after the hunter releases an arrow or bolt.
        ///
        /// Schedules a post-shot retreat: after firing, during the reload
        /// window, the hunter should back away from the animal. We do a
        /// lightweight retreat by pushing the hunter back a few units from
        /// the animal's current position, keeping them at bow range.
        /// </summary>
        public static void OnPerformedAttackPostfix(
            object __instance,
            GameObject attackTarget,
            object targetTeamDef)
        {
            try
            {
                var hunter = __instance as Component;
                if (hunter == null || attackTarget == null) return;
                // Post-shot retreat applies to ALL hunters — any tier, any path.
                // Backing up during the reload window is baseline survival behavior.
                if (!IsAnyHunter(hunter)) return;

                // Use GetGameComponent (not GetComponent<Component>) — the latter
                // returns Transform first, not the actual Wolf/Deer/etc.
                var animalComp = GetGameComponent(attackTarget);

                // Post-shot retreat — only when the target is dangerous AND charging.
                //
                //   Deer  → never dangerous, always flees → CHASE, never retreat
                //   Wolf  → always charging               → always retreat
                //   Boar  healthy → flees                 → CHASE (retreat when it turns)
                //   Boar  wounded → charges               → retreat
                //   Bear  healthy → charges               → retreat
                //   Bear  wounded → flees                 → CHASE for kill shots
                //   Low HP hunter → always retreat from any dangerous animal,
                //                   even fleeing boar/bear — can't take another hit
                //
                // The proximity watcher handles the separate case of a hunter chasing
                // deer who wanders into predator territory — it scans for Wolf/Boar/Bear
                // near the hunter regardless of current target, so a deer-chase that
                // strays near a wolf den will still trigger the predator proximity retreat.
                if (WardenOfTheWildsMod.HuntingLodgeKitingEnabled.Value && animalComp != null)
                {
                    float hpThreshold = WardenOfTheWildsMod.HunterLowHealthThreshold.Value;
                    bool  isLowHP     = hpThreshold > 0f &&
                                        GetHunterHealthPercent(hunter) < hpThreshold;

                    bool shouldRetreat =
                        IsDangerousAnimal(animalComp) &&
                        (isLowHP ||               // Wounded: always back away
                         IsWolf(animalComp) ||    // Wolf: always charging, always retreat
                         IsAnimalApproaching(animalComp, hunter.transform.position));
                        // Boar/Bear (healthy hunter): only retreat when charging
                        // Deer/Groundhog: IsDangerousAnimal=false → never retreat

                    if (shouldRetreat)
                        ApplyPostShotRetreat(hunter, attackTarget.transform.position);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnPerformedAttackPostfix: {ex.Message}");
            }
        }

        // ── 4a. Animal OnCombatTargetSet postfix (aggro trigger) ────────────
        /// <summary>
        /// Fires on Wolf/Boar/Bear when they acquire a new combat target.
        /// If newTarget is a hunter AND a second predator is already tracking
        /// them within AttackerWindowSeconds, trigger retreat immediately —
        /// before the second predator lands its first hit.
        ///
        /// CONFIRMED from animal dump: Void OnCombatTargetSet(IDamageable prevTarget,
        ///   IDamageable newTarget, TargetSourceIdentifier targetSourceIdentifier)
        /// </summary>
        public static void OnAnimalAggroPostfix(
            object __instance,
            object prevTarget,        // AggressiveAnimal uses "prevTarget" not "previousTarget"
            object newTarget,
            object targetSourceIdentifier)
        {
            if (newTarget == null) return;
            try
            {
                var predator = __instance as Component;
                if (predator == null || !IsDangerousAnimal(predator)) return;

                // newTarget is the entity being attacked — extract as Component
                var hunter = ExtractComponent(newTarget);
                if (hunter == null || !IsAnyHunter(hunter)) return;

                RecordEngagementAndCheckRetreat(hunter, predator, "aggro");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnAnimalAggroPostfix: {ex.Message}");
            }
        }

        // ── 4b. OnAttacked postfix (hit fallback) ────────────────────────────
        /// <summary>
        /// Fires when the villager takes a hit. Feeds the same attacker window as
        /// the aggro patch — handles any cases where OnCombatTargetSet fires on an
        /// unknown base type, and provides the low-HP single-predator retreat.
        /// </summary>
        public static void OnAttackedPostfix(
            object __instance,
            float damageAmount,
            GameObject damageCauser,
            object damageType)
        {
            try
            {
                var hunter = __instance as Component;
                if (hunter == null || damageCauser == null) return;
                if (!IsAnyHunter(hunter)) return;

                // Read vanilla retreat threshold once on first confirmed hunter hit
                TryReadVanillaRetreatThreshold(hunter);

                // Use GetGameComponent (not GetComponent<Component>) — the latter
                // returns Transform first, making IsDangerousAnimal always false.
                var causerComp = GetGameComponent(damageCauser);
                if (causerComp == null || !IsDangerousAnimal(causerComp)) return;

                RecordEngagementAndCheckRetreat(hunter, causerComp,
                    $"hit ({damageAmount:F1} dmg)", damageAmount);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnAttackedPostfix: {ex.Message}");
            }
        }

        // ── Shared: record engagement and check retreat ───────────────────────
        /// <summary>
        /// Records a predator engagement (aggro or hit) for the given hunter in the
        /// rolling AttackerWindowSeconds window, then checks retreat conditions.
        ///
        /// Retreat fires when:
        ///   • 2+ distinct dangerous animals have entries in the window (OVERWHELMED), OR
        ///   • Hunter HP is below HunterLowHealthThreshold AND damageAmount > 0
        ///
        /// The damageAmount > 0 guard is critical: wolves running past a hunter while
        /// chasing a different target deal 0 damage but still fire OnAttacked. Without
        /// the guard, bystander hunters at ~65% HP would retreat even though no wolf
        /// is actually targeting them.
        ///
        /// OVERWHELMED still fires on 0-damage hits — the wolf IS in the area and the
        /// attacker count still matters for pack detection.
        ///
        /// Rate-limited by KiteRateLimit so SetMovementTarget isn't spammed.
        /// Stores attacker Component refs in _hunterAttackerRefs for centroid calc.
        /// </summary>
        private static void RecordEngagementAndCheckRetreat(
            Component hunter, Component predator, string eventLabel,
            float damageAmount = 0f)
        {
            int hunterKey   = System.Runtime.CompilerServices
                .RuntimeHelpers.GetHashCode(hunter);
            // Key by ROOT gameObject — AggressiveAnimal is on a child called "Active",
            // so __instance hash != damageCauser hash even for the same wolf.
            // Normalize to the actual Wolf/Bear/Boar component identity.
            // The bear's attack can originate from a hitbox that is a SEPARATE
            // root GameObject from the one AggressiveAnimal lives on — so
            // transform.root.gameObject still gives different hashes per code path.
            // Walking up the hierarchy to find the first IsDangerousAnimal component
            // gives a stable identity regardless of which sub-object fired.
            var normalizedPredator = NormalizePredatorComponent(predator) ?? predator;
            int predatorKey = System.Runtime.CompilerServices
                .RuntimeHelpers.GetHashCode(normalizedPredator);
            float now = Time.time;

            // ── Update attacker window ────────────────────────────────────────
            if (!_hunterAttackers.TryGetValue(hunterKey, out var attackers))
            {
                attackers = new Dictionary<int, float>();
                _hunterAttackers[hunterKey] = attackers;
            }
            attackers[predatorKey] = now;

            // Also store the live Component ref keyed by predator hash so we can
            // compute a real centroid rather than falling back to one position.
            if (!_hunterAttackerRefs.TryGetValue(hunterKey, out var refs))
            {
                refs = new Dictionary<int, Component>();
                _hunterAttackerRefs[hunterKey] = refs;
            }
            refs[predatorKey] = predator;

            // Prune stale entries — reuse scratch list to avoid per-hit allocation
            _staleKeyScratch.Clear();
            foreach (var kv in attackers)
                if (now - kv.Value > AttackerWindowSeconds)
                    _staleKeyScratch.Add(kv.Key);
            foreach (var k in _staleKeyScratch) { attackers.Remove(k); refs.Remove(k); }

            // Count only LIVE predators — dead wolves remain in the window until
            // the TTL expires but their Component returns null after destruction.
            // Using attackers.Count would trigger OVERWHELMED on a half-dead pack
            // where wolf #2 just died but hasn't been pruned yet.
            int liveCount = 0;
            foreach (var kv in refs)
                if (kv.Value != null) liveCount++;

            // ── Retreat conditions ────────────────────────────────────────────
            float hpThreshold = WardenOfTheWildsMod.HunterLowHealthThreshold.Value;
            // LOW HP retreat requires actual damage dealt (damageAmount > 0).
            // A 0-damage hit means a wolf grazed past while chasing someone else —
            // not enough reason for THIS hunter to abandon their engagement.
            // The attacker is still recorded above for OVERWHELMED (2+ predator) tracking.
            bool  isLowHP     = damageAmount > 0f && hpThreshold > 0f &&
                                GetHunterHealthPercent(hunter) < hpThreshold;

            // ── Single-predator LOW HP grace window ───────────────────────────
            // When only one predator is present, suppress the retreat on the
            // FIRST hit below threshold. A second hit within LowHpGraceSeconds
            // confirms the hunter is genuinely in trouble and triggers retreat.
            // ── Melee range override ──────────────────────────────────────────
            // If the predator is within melee range (~8u) and landing hits, the
            // hunter is already in a melee fight — skip grace and retreat NOW.
            // Grace only applies to range exchanges where a single big hit drops HP.
            const float MeleeRange = 8f;
            bool inMeleeRange = damageAmount > 0f &&
                                Vector3.Distance(hunter.transform.position,
                                                 predator.transform.position) <= MeleeRange;

            // With 2+ predators (OVERWHELMED) or in melee range, skip grace —
            // retreat immediately.
            bool hpAboveThreshold = !isLowHP; // capture BEFORE grace suppression
            if (isLowHP && liveCount < 2 && !inMeleeRange)
            {
                if (!_hunterFirstLowHpTime.TryGetValue(hunterKey, out float firstWound)
                    || now - firstWound > LowHpGraceSeconds)
                {
                    // First time below threshold (or grace expired) — record and suppress
                    _hunterFirstLowHpTime[hunterKey] = now;
                    isLowHP = false;
                }
                // else: second hit within grace window — isLowHP stays true, retreat fires
            }
            // Only clear the grace entry when HP is genuinely above threshold.
            if (hpAboveThreshold)
                _hunterFirstLowHpTime.Remove(hunterKey);

            bool shouldRetreat = liveCount >= 2 || isLowHP;
            if (!shouldRetreat) return;

            // Rate-limit — hunter is probably already retreating
            if (_lastKiteTimes.TryGetValue(hunterKey, out float lastRetreat) &&
                now - lastRetreat < KiteRateLimit)
                return;

            // ── Retreat direction: away from centroid of active predators ─────
            Vector3 hPos     = hunter.transform.position;
            Vector3 centroid = Vector3.zero;
            int     counted  = 0;
            foreach (var kv in refs)
            {
                if (kv.Value == null) continue;
                centroid += kv.Value.transform.position;
                counted++;
            }
            if (counted == 0) centroid = predator.transform.position;
            else              centroid /= counted;

            Vector3 dir = (hPos - centroid).normalized;
            if (dir == Vector3.zero) dir = Vector3.back;

            float reload      = AnimalBehaviorSystem.KitingCalculator
                                    .GetEffectiveReloadSeconds(hunter)
                                / WardenOfTheWildsMod.HuntingLodgeBigGameShootMult.Value;
            float speed       = AnimalBehaviorSystem.KitingCalculator
                                    .GetEffectiveHunterSpeed(0f);
            float retreatDist = AnimalBehaviorSystem.KitingCalculator
                                    .OptimalRetreatDistance(reload, speed) * 3f;

            // ── Prefer cabin as retreat destination (cabin-as-blind) ──────────
            // If the hunter's cabin is in roughly the "away from predator" direction
            // AND is far enough from the predator to be safe, retreat there instead
            // of a raw directional point. Once at the cabin the hunter can fire from
            // the doorstep rather than fleeing into open terrain.
            Vector3 retreatTarget = hPos + dir * retreatDist;
            var cabin = FindAssignedHunterBuilding(hunter);
            if (cabin != null)
            {
                Vector3 cabinPos  = cabin.transform.position;
                Vector3 cabinDir  = (cabinPos - hPos).normalized;
                float   cabinDist = Vector3.Distance(cabinPos, centroid);

                // Use cabin if it is:
                //   a) in roughly the away-from-predator direction (dot > 0), AND
                //   b) at least 20u from the predator centroid (safe to stand at)
                if (Vector3.Dot(cabinDir, dir) > 0f && cabinDist >= 20f)
                    retreatTarget = cabinPos;
            }

            SetMovementTarget(hunter, retreatTarget);
            _lastKiteTimes[hunterKey] = now;

            string reason = isLowHP && liveCount < 2
                ? $"LOW HP ({GetHunterHealthPercent(hunter):P0})"
                : $"OVERWHELMED ({liveCount} live predators)";

            MelonLogger.Msg(
                $"[WotW] Retreat [{reason}] ({eventLabel}): " +
                $"'{hunter.gameObject.name}' ← '{normalizedPredator.gameObject.name}', " +
                $"retreating {retreatDist:F1}u");
        }

        // ── Hunter shelter / melee-discipline state ───────────────────────────
        //
        // These track per-villager state applied when they join a hunter cabin
        // and restored when they leave. Keyed by RuntimeHelpers.GetHashCode(villager).
        private static readonly Dictionary<int, float> _originalShelterRadius =
            new Dictionary<int, float>();
        private static readonly Dictionary<int, Func<CombatComponent, bool, CombatAction, bool>>
            _meleeGateDelegates =
            new Dictionary<int, Func<CombatComponent, bool, CombatAction, bool>>();

        // Cached field lookup for Villager.enemySearchDistanceInBuilding. The
        // property accessors exist but the setter may be protected — fall back
        // to the backing field when needed.
        private static FieldInfo? _enemySearchDistField = null;
        private static bool       _enemySearchDistFieldSearched = false;

        private static FieldInfo? ResolveEnemySearchDistField(Component villager)
        {
            if (_enemySearchDistFieldSearched) return _enemySearchDistField;
            _enemySearchDistFieldSearched = true;
            Type? t = villager.GetType();
            while (t != null && t.Name != "MonoBehaviour" && t.Name != "Object")
            {
                // Try backing field first, then direct field
                var f = t.GetField("_enemySearchDistanceInBuilding", AllInstance)
                     ?? t.GetField("enemySearchDistanceInBuilding", AllInstance);
                if (f != null && f.FieldType == typeof(float))
                {
                    _enemySearchDistField = f;
                    return f;
                }
                t = t.BaseType;
            }
            return null;
        }

        // ── 5a. OnResidentAdded postfix (worker registry) ────────────────────
        /// <summary>
        /// Fires when a villager is assigned to a HunterBuilding.
        /// Registers them in _buildingWorkers so cabin-mates can be found
        /// quickly for the wounded-escort feature. Also extends their
        /// cabin emergence radius and attaches the T1 melee-discipline gate.
        /// </summary>
        public static void OnResidentAddedPostfix(
            object __instance,
            object resident)
        {
            try
            {
                var building = __instance as Component;
                if (building == null) return;

                var villager = ExtractVillagerFromResident(resident);
                if (villager == null) return;

                int bKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(building);
                int vKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(villager);

                if (!_buildingWorkers.TryGetValue(bKey, out var workers))
                {
                    workers = new HashSet<Component>();
                    _buildingWorkers[bKey] = workers;
                }
                workers.Add(villager);
                _hunterBuildingKey[vKey] = bKey;
                _buildingComponents[bKey] = building;

                // ── Fix 1: Extend cabin emergence radius ─────────────────────
                ApplyHunterShelterRadius(villager, vKey);

                // ── Fix 2: T1 melee-discipline gate ───────────────────────────
                // Handled via Harmony Postfix on Villager.OnIsMeleeAttack
                // (see OnIsMeleeAttackPostfix). The old delegate-subscribe
                // path (ApplyHunterMeleeGate) was unreliable because vanilla
                // could add handlers after ours and win the multicast return.

                // ── Fix 3: BGH speed bonus — apply to newly-assigned hunter ──
                try
                {
                    var enh = building.GetComponent<HunterCabinEnhancement>();
                    enh?.RefreshSpeedBonusOnNewResident();
                }
                catch { }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnResidentAddedPostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// Raise the villager's shelter enemy-scan radius so they stay inside
        /// the cabin until threats are genuinely out of range. Caches the
        /// pre-mod value for restoration on unassignment.
        /// </summary>
        private static void ApplyHunterShelterRadius(Component villager, int vKey)
        {
            try
            {
                var field = ResolveEnemySearchDistField(villager);
                if (field == null) return;

                float targetRadius = WardenOfTheWildsMod.HunterShelterSearchRadius.Value;
                if (targetRadius <= 0f) return;

                float currentRadius = (float)field.GetValue(villager);

                // Cache original once; don't overwrite if we've already buffed this villager
                if (!_originalShelterRadius.ContainsKey(vKey))
                    _originalShelterRadius[vKey] = currentRadius;

                if (currentRadius < targetRadius)
                {
                    field.SetValue(villager, targetRadius);
                    MelonLogger.Msg(
                        $"[WotW] Hunter emergence radius {currentRadius:F0}u → {targetRadius:F0}u " +
                        $"for '{villager.gameObject.name}'");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ApplyHunterShelterRadius: {ex.Message}");
            }
        }

        /// <summary>
        /// Subscribes our melee gate to the villager's CombatComponent. Stored
        /// per-villager so we can unsubscribe the exact delegate instance later
        /// (Delegate.Remove uses reference equality on the MethodInfo + target).
        /// </summary>
        private static void ApplyHunterMeleeGate(Component villager, int vKey)
        {
            try
            {
                var combatComp = villager.GetComponent<CombatComponent>();
                if (combatComp == null) return;

                // Don't double-attach
                if (_meleeGateDelegates.ContainsKey(vKey)) return;

                Func<CombatComponent, bool, CombatAction, bool> gate = HunterMeleeGate;
                combatComp.onIsMeleeAttack = (Func<CombatComponent, bool, CombatAction, bool>)
                    Delegate.Combine(combatComp.onIsMeleeAttack, gate);
                _meleeGateDelegates[vKey] = gate;

                MelonLogger.Msg(
                    $"[WotW] Melee gate attached for '{villager.gameObject.name}'");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] ApplyHunterMeleeGate: {ex.Message}");
            }
        }

        /// <summary>
        /// Harmony Postfix on Villager.OnIsMeleeAttack. Unconditionally
        /// overrides the melee-vs-ranged decision for T1 hunters so a wolf
        /// biting at close range doesn't cause the hunter to drop the bow
        /// and punch back.
        ///
        /// T2 paths (HuntingLodge, TrapperLodge) are passed through — their
        /// combat identity is more flexible and BGH hunters may legitimately
        /// want a melee finisher.
        /// </summary>
        // Throttle "melee blocked" log — otherwise every attack decision
        // tick (dozens per second) would spam the log with the same message.
        private static readonly Dictionary<int, float> _lastMeleeBlockLog =
            new Dictionary<int, float>();
        private const float MeleeBlockLogCooldown = 3f;

        public static void OnIsMeleeAttackPostfix(
            Villager __instance,
            CombatComponent ownerCombatComp,
            bool defaultIsMeleeAttack,
            CombatAction intendedAction,
            ref bool __result)
        {
            try
            {
                if (__instance == null) return;

                // Only touch hunter decisions
                if (!IsAnyHunter(__instance)) return;

                // Determine path
                HunterT2Path path = HunterT2Path.Vanilla;
                try
                {
                    var building = FindAssignedHunterBuilding(__instance);
                    var enh = building?.GetComponent<HunterCabinEnhancement>();
                    if (enh != null) path = enh.Path;
                }
                catch { }

                if (path != HunterT2Path.Vanilla) return;  // T2 paths: defer

                float threshold = WardenOfTheWildsMod.T1MeleeThreshold.Value;
                if (threshold >= 1f) return;  // gate disabled
                if (threshold <= 0f) { __result = false; return; }

                // Gate: only allow melee if target is near death
                var target = ownerCombatComp?.myTargetDamageable;
                if (target == null) { __result = false; return; }

                float hpFrac = GetTargetHpFraction(target);
                bool allowMelee = hpFrac >= 0f && hpFrac < threshold;

                if (__result && !allowMelee)
                {
                    __result = false;

                    // Throttle log — one line per hunter per 3 seconds
                    int key = System.Runtime.CompilerServices
                        .RuntimeHelpers.GetHashCode(__instance);
                    if (!_lastMeleeBlockLog.TryGetValue(key, out float last)
                        || Time.time - last > MeleeBlockLogCooldown)
                    {
                        _lastMeleeBlockLog[key] = Time.time;
                        MelonLogger.Msg(
                            $"[WotW] T1 melee blocked: '{__instance.gameObject.name}' " +
                            $"→ target HP {hpFrac:P0} ≥ {threshold:P0} threshold, forcing ranged");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnIsMeleeAttackPostfix: {ex.Message}");
            }
        }

        /// <summary>
        /// (Legacy) Delegate-based melee gate. Kept but inert — the Harmony
        /// Postfix above is the authoritative path now. Still referenced by
        /// the OnResidentAdded hookup; the delegate subscription is harmless
        /// noise that returns defaultIsMelee so it can't interfere with the
        /// Postfix decision.
        /// </summary>
        private static bool HunterMeleeGate(
            CombatComponent ownerComp, bool defaultIsMelee, CombatAction action)
        {
            try
            {
                var hunter = ownerComp as Component;
                if (hunter == null) return defaultIsMelee;

                // Only gate hunters — bail out for non-hunter CombatComponents
                // that somehow end up with our delegate (shouldn't happen but
                // defensive check).
                if (!IsAnyHunter(hunter)) return defaultIsMelee;

                // Path dispatch: T2 paths keep vanilla behavior
                HunterT2Path path = HunterT2Path.Vanilla;
                try
                {
                    var residenceProp = hunter.GetType().GetProperty("residence", AllInstance);
                    var residence = residenceProp?.GetValue(hunter) as Component;
                    var enh = residence?.GetComponent<HunterCabinEnhancement>();
                    if (enh != null) path = enh.Path;
                }
                catch { }

                if (path != HunterT2Path.Vanilla) return defaultIsMelee;

                // T1 gate — only allow melee if target is near death
                float threshold = WardenOfTheWildsMod.T1MeleeThreshold.Value;
                if (threshold >= 1f) return defaultIsMelee;  // disabled
                if (threshold <= 0f) return false;           // never melee

                var target = ownerComp.myTargetDamageable;
                if (target == null) return false;  // no target = no melee

                // IDamageable exposes a life fraction via life / maxLife. Use
                // reflection to stay resilient to small API changes.
                float hpFrac = GetTargetHpFraction(target);
                bool allowMelee = hpFrac >= 0f && hpFrac < threshold;

                return allowMelee;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterMeleeGate: {ex.Message}");
                return defaultIsMelee;
            }
        }

        /// <summary>Restores the villager's pre-mod shelter search distance.</summary>
        private static void RestoreHunterShelterRadius(Component villager, int vKey)
        {
            try
            {
                if (!_originalShelterRadius.TryGetValue(vKey, out float original)) return;

                var field = ResolveEnemySearchDistField(villager);
                if (field != null)
                    field.SetValue(villager, original);

                _originalShelterRadius.Remove(vKey);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] RestoreHunterShelterRadius: {ex.Message}");
            }
        }

        /// <summary>Removes our melee gate delegate from the villager's CombatComponent.</summary>
        private static void DetachHunterMeleeGate(Component villager, int vKey)
        {
            try
            {
                if (!_meleeGateDelegates.TryGetValue(vKey, out var gate)) return;

                var combatComp = villager.GetComponent<CombatComponent>();
                if (combatComp != null)
                {
                    combatComp.onIsMeleeAttack = (Func<CombatComponent, bool, CombatAction, bool>)
                        Delegate.Remove(combatComp.onIsMeleeAttack, gate);
                }

                _meleeGateDelegates.Remove(vKey);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] DetachHunterMeleeGate: {ex.Message}");
            }
        }

        /// <summary>Reads the target's normalized HP fraction (0-1). Returns
        /// -1 if we can't determine it, in which case callers should refuse
        /// melee to be safe.</summary>
        private static float GetTargetHpFraction(IDamageable target)
        {
            try
            {
                var t = target.GetType();
                // Prefer explicit percent / fraction
                var percProp = t.GetProperty("lifePercentage", AllInstance)
                            ?? t.GetProperty("healthPercentage", AllInstance)
                            ?? t.GetProperty("lifeFraction", AllInstance);
                if (percProp != null && percProp.PropertyType == typeof(float))
                    return (float)percProp.GetValue(target);

                // Fall back to life / maxLife
                var lifeProp = t.GetProperty("life", AllInstance);
                var maxLifeProp = t.GetProperty("maxLife", AllInstance);
                if (lifeProp != null && maxLifeProp != null)
                {
                    float life = System.Convert.ToSingle(lifeProp.GetValue(target));
                    float maxLife = System.Convert.ToSingle(maxLifeProp.GetValue(target));
                    if (maxLife > 0f) return Mathf.Clamp01(life / maxLife);
                }
            }
            catch { }
            return -1f;
        }

        // ── 5b. OnResidentRemoved postfix (registry prune + stale icon fix) ──
        /// <summary>
        /// Fires when a villager is removed from a HunterBuilding.
        /// Prunes them from _buildingWorkers, then fixes the stale occupation icon
        /// (vanilla bug: Hunter badge persists after unassignment).
        /// </summary>
        public static void OnResidentRemovedPostfix(
            object __instance,
            object resident)
        {
            try
            {
                var building = __instance as Component;
                var villager = ExtractVillagerFromResident(resident);
                if (villager == null) return;

                // ── Prune worker registry ─────────────────────────────────────
                int vKeyPrune = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(villager);
                if (building != null)
                {
                    int bKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(building);
                    if (_buildingWorkers.TryGetValue(bKey, out var workers))
                        workers.Remove(villager);
                    _hunterBuildingKey.Remove(vKeyPrune);
                }

                // ── Restore vanilla shelter emergence radius ─────────────────
                RestoreHunterShelterRadius(villager, vKeyPrune);

                // ── Detach T1 melee-discipline gate ──────────────────────────
                DetachHunterMeleeGate(villager, vKeyPrune);

                // ── Fix stale occupation icon ─────────────────────────────────
                // CONFIRMED: Villager.OnOccupationChanged() (void, no params)
                Type? check = villager.GetType();
                while (check != null && check.Name != "MonoBehaviour" && check.Name != "Object")
                {
                    var m = check.GetMethod("OnOccupationChanged", AllInstance,
                        null, Type.EmptyTypes, null);
                    if (m != null)
                    {
                        m.Invoke(villager, null);
                        MelonLogger.Msg(
                            $"[WotW] Icon refresh: '{villager.gameObject.name}' " +
                            $"removed from '{building?.gameObject.name}'");
                        return;
                    }
                    check = check.BaseType;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnResidentRemovedPostfix: {ex.Message}");
            }
        }

        // ── Shared resident extraction ────────────────────────────────────────
        // IEntersStructures resident → Component. Used by both Added/Removed.
        private static Component? ExtractVillagerFromResident(object resident)
        {
            if (resident == null) return null;
            if (resident is Component c) return c;
            var goProp = resident.GetType()
                .GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
            var go = goProp?.GetValue(resident) as GameObject;
            return go?.GetComponent<Component>();
        }

        // ── Find nearest available cabin-mate within escort radius ────────────
        /// <summary>
        /// Given a wounded hunter, returns the nearest other hunter from the same
        /// HunterBuilding who is within WoundedEscortSearchRadius. Returns null if
        /// no cabin-mate is close enough to respond.
        /// </summary>
        public static Component? FindNearbyEscort(Component woundedHunter)
        {
            try
            {
                int vKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(woundedHunter);
                if (!_hunterBuildingKey.TryGetValue(vKey, out int bKey)) return null;
                if (!_buildingWorkers.TryGetValue(bKey, out var workers)) return null;

                Vector3 wPos = woundedHunter.transform.position;
                float   bestDist = WoundedEscortSearchRadius;
                Component? best = null;

                foreach (var worker in workers)
                {
                    if (worker == null || worker == woundedHunter) continue;
                    float d = Vector3.Distance(worker.transform.position, wPos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = worker;
                    }
                }
                return best;
            }
            catch { }
            return null;
        }

        // ── 6. WoundedEscortState setter postfix (hunter buddy escort) ───────
        /// <summary>
        /// Fires when Villager.set_woundedEscortState is called.
        /// Detects the 0 → non-zero transition (villager just became wounded) and,
        /// if the villager is a hunter, routes the nearest cabin-mate to escort them
        /// back to the cabin rather than waiting for the vanilla laborer system
        /// (which is blocked when predators are still nearby).
        ///
        /// State tracking is purely integer — enum value names are unknown until
        /// the first in-game wound. The logged value.ToString() will reveal the names
        /// for future reference.
        /// </summary>
        public static void OnWoundedEscortStateSetPostfix(
            object __instance,
            object value)
        {
            if (value == null) return;
            try
            {
                var hunter = __instance as Component;
                if (hunter == null) return;

                int newStateInt = Convert.ToInt32(value);
                int hunterKey   = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(hunter);

                // Track previous state.
                // Use -1 as "never set" sentinel — dict default int (0) collides
                // with WaitingOnEscort=0 and would suppress the first-wound trigger.
                if (!_hunterWoundedStateTracker.TryGetValue(hunterKey, out int prevStateInt))
                    prevStateInt = -1;
                _hunterWoundedStateTracker[hunterKey] = newStateInt;

                // Always log transitions for enum value discovery on first session
                if (newStateInt != prevStateInt)
                {
                    MelonLogger.Msg(
                        $"[WotW] WoundedEscortState: '{hunter.gameObject.name}' " +
                        $"{prevStateInt} → {newStateInt} ({value})");
                }

                // Trigger escort when entering WaitingOnEscort (0) from any other state.
                // Confirmed enum values (dump 26-4-19):
                //   WaitingOnEscort    = 0  ← villager IS wounded, needs escort
                //   BeingEscorted      = 1  ← escort has arrived
                //   ArrivedAtDestination = 2  ← reached healer/cabin
                //
                // Sentinel -1 means "never set this session" — first wound triggers.
                // Re-wound after a prior escort cycle (1→0 or 2→0) also triggers.
                if (newStateInt != 0 || prevStateInt == 0) return;

                // Guard against false triggers during map initialization
                // (villager data is set up in the first few seconds of scene load).
                if (Time.time < 10f) return;

                // Only care about hunters assigned to a HunterBuilding
                if (!IsAnyHunter(hunter)) return;

                // Skip escort if any cabin-mate is actively in combat.
                // Pulling hunters off a fight to escort a wounded buddy causes cascade
                // retreats — all three abandon a nearly-dead bear because one got hit.
                // If cabin-mates are engaged, the wounded hunter retreats alone.
                int woundedKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(hunter);
                if (_hunterBuildingKey.TryGetValue(woundedKey, out int bldgKey) &&
                    _buildingWorkers.TryGetValue(bldgKey, out var workers))
                {
                    foreach (var worker in workers)
                    {
                        if (worker == null) continue;
                        int workerKey = System.Runtime.CompilerServices
                            .RuntimeHelpers.GetHashCode(worker);
                        if (workerKey == woundedKey) continue;
                        if (_hunterAttackers.TryGetValue(workerKey, out var atk)
                            && atk.Count > 0)
                        {
                            MelonLogger.Msg(
                                $"[WotW] Wounded hunter '{hunter.gameObject.name}' — " +
                                $"cabin-mates in combat, skipping escort");
                            return;
                        }
                    }
                }

                // Look for a nearby cabin-mate who can respond
                var escort = FindNearbyEscort(hunter);
                if (escort == null)
                {
                    MelonLogger.Msg(
                        $"[WotW] Wounded hunter '{hunter.gameObject.name}' — " +
                        $"no cabin-mate within {WoundedEscortSearchRadius}u to escort");
                    return;
                }

                MelonLogger.Msg(
                    $"[WotW] Wounded hunter '{hunter.gameObject.name}' — " +
                    $"routing cabin-mate '{escort.gameObject.name}' to escort");

                // Redirect the escort toward the wounded hunter's current position
                SetMovementTarget(escort, hunter.transform.position);

                // Start monitoring: when escort arrives, route both to the cabin
                MelonCoroutines.Start(WoundedEscortCoroutine(hunter, escort));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] OnWoundedEscortStateSetPostfix: {ex.Message}");
            }
        }

        // ── 6a. Wounded escort coroutine ──────────────────────────────────────
        /// <summary>
        /// Monitors a pending hunter escort. Every second, nudges the escort hunter
        /// toward the wounded hunter. When they get within ArrivalDistance, routes
        /// both toward the assigned cabin. Gives up after TimeoutSeconds.
        ///
        /// Called via MelonCoroutines.Start() — runs on the Unity main thread.
        /// </summary>
        private static IEnumerator WoundedEscortCoroutine(
            Component woundedHunter, Component escortHunter)
        {
            const float ArrivalDistance = 6f;
            const float TimeoutSeconds  = 45f;
            const float TickInterval    = 1f;

            float deadline = Time.time + TimeoutSeconds;

            while (Time.time < deadline)
            {
                yield return new WaitForSeconds(TickInterval);

                // Abort if either hunter was destroyed (e.g. died while walking)
                if (woundedHunter == null || escortHunter == null) yield break;

                float dist = Vector3.Distance(
                    woundedHunter.transform.position,
                    escortHunter.transform.position);

                if (dist <= ArrivalDistance)
                {
                    // Escort has arrived — route both to the cabin
                    var cabin = FindAssignedHunterBuilding(woundedHunter)
                             ?? FindAssignedHunterBuilding(escortHunter);

                    if (cabin != null)
                    {
                        SetMovementTarget(woundedHunter, cabin.transform.position);
                        SetMovementTarget(escortHunter,  cabin.transform.position);
                        MelonLogger.Msg(
                            $"[WotW] Escort arrived — routing wounded " +
                            $"'{woundedHunter.gameObject.name}' + " +
                            $"'{escortHunter.gameObject.name}' to " +
                            $"'{cabin.gameObject.name}'");
                    }
                    else
                    {
                        MelonLogger.Msg(
                            $"[WotW] Escort arrived but cabin not found for " +
                            $"'{woundedHunter.gameObject.name}'");
                    }
                    yield break;
                }

                // Escort hasn't arrived yet — keep nudging toward the wounded hunter
                // (the AI may try to override our destination each tick)
                SetMovementTarget(escortHunter, woundedHunter.transform.position);
            }

            // Timeout — log and give up
            MelonLogger.Msg(
                $"[WotW] Escort timeout — '{escortHunter?.gameObject.name}' " +
                $"couldn't reach '{woundedHunter?.gameObject.name}' in {TimeoutSeconds}s");
        }

        // ════════════════════════════════════════════════════════════════════════
        //  KITING HELPERS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if the given Component appears to be assigned to a
        /// HuntingLodge — checked by proximity to any HuntingLodge building.
        ///
        /// PERFORMANCE NOTE: This is called from postfixes that fire for every
        /// villager in the scene. Uses a two-level time-based cache:
        ///   L1 — per-villager result, TTL=10s (avoids repeated proximity math)
        ///   L2 — HunterBuilding scan, TTL=5s  (avoids repeated FindObjectsOfType)
        /// </summary>
        private static bool IsHuntingLodgeHunter(Component villager)
        {
            try
            {
                int vKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(villager);

                // L1: per-villager cache
                if (_hunterLodgeCacheResult.TryGetValue(vKey, out bool cachedResult) &&
                    _hunterLodgeCacheExpiry.TryGetValue(vKey, out float cachedExpiry) &&
                    Time.time < cachedExpiry)
                    return cachedResult;

                // L2: refresh building list if stale
                if (Time.time >= _lodgeBuildingCacheExpiry)
                {
                    _cachedLodgeBuildings.Clear();
                    Type? hunterType = FindType("HunterBuilding");
                    if (hunterType != null)
                    {
                        float checkRadius = 200f * WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value;
                        foreach (UnityEngine.Object obj in
                            UnityEngine.Object.FindObjectsOfType(hunterType))
                        {
                            var b = obj as Component;
                            if (b == null) continue;
                            var enh = b.GetComponent<HunterCabinEnhancement>();
                            if (enh?.Path == HunterT2Path.HuntingLodge)
                                _cachedLodgeBuildings.Add(new CachedBuilding(b, checkRadius));
                        }
                    }
                    _lodgeBuildingCacheExpiry = Time.time + BuildingCacheTTL;
                }

                // Proximity check against cached buildings
                Vector3 vPos = villager.transform.position;
                bool result = false;
                foreach (var entry in _cachedLodgeBuildings)
                {
                    if (entry.Building == null) continue;
                    if (Vector3.Distance(entry.Building.transform.position, vPos) < entry.WorkRadius)
                    {
                        result = true;
                        break;
                    }
                }

                // Store in L1 cache
                _hunterLodgeCacheResult[vKey] = result;
                _hunterLodgeCacheExpiry[vKey]  = Time.time + HunterCacheTTL;
                return result;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Returns the HunterBuilding this villager is assigned to, or null if
        /// they are not a hunter. Reads <c>Villager.residence</c> directly — the
        /// same field vanilla's own hunter logic uses (see
        /// HuntSearchEntry.ProcessNewTask and
        /// VillagerOccupationHunter.OnHuntSubTaskValidToContinue).
        ///
        /// Previously this did a reflection probe for "_placeOfWork" and fell back
        /// to a FindObjectsOfType + proximity scan on every call when the probe
        /// failed. With 14 hunters × 5Hz hunt-task ticks = 70 calls/sec, that
        /// proximity fallback was the dominant combat-era stutter source.
        ///
        /// Cached 10s per villager.
        /// </summary>
        private static Component? FindAssignedHunterBuilding(Component villager)
        {
            if (villager == null) return null;
            try
            {
                int vKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(villager);

                if (_assignedBuildingCacheExpiry.TryGetValue(vKey, out float expiry) &&
                    Time.time < expiry)
                {
                    _assignedBuildingCache.TryGetValue(vKey, out var cached);
                    return cached;
                }

                Component? result = null;
                if (villager is Villager v && v.residence is HunterBuilding hb)
                    result = hb;

                _assignedBuildingCache[vKey] = result;
                _assignedBuildingCacheExpiry[vKey] = Time.time + HunterCacheTTL;
                return result;
            }
            catch { return null; }
        }

        /// <summary>Convenience wrapper — true if FindAssignedHunterBuilding returns non-null.</summary>
        private static bool IsAnyHunter(Component villager)
            => FindAssignedHunterBuilding(villager) != null;

        /// <summary>Public wrapper so companion patches (HunterShelterGuard,
        /// HunterCarcassCollection, etc.) can reuse the same hunter check.</summary>
        public static bool IsAnyHunterPublic(Component villager)
            => IsAnyHunter(villager);

        // ════════════════════════════════════════════════════════════════════════
        //  HEALTH / ARROW HELPERS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns hunter health as a 0–1 fraction. Returns 1.0 (full health) if
        /// the health component cannot be found — safe default that prevents false
        /// retreat triggers.
        ///
        /// CONFIRMED path (dump 26-4-19):
        ///   hunter  → Villager.get_villagerHealth() → VillagerHealth component
        ///   VillagerHealth.get_health() → Single, already normalized 0–1
        ///
        /// Property refs cached after first lookup — no repeated GetProperty calls.
        /// No per-hunter cache: health changes every hit so staleness defeats the purpose.
        /// </summary>
        private static float GetHunterHealthPercent(Component hunter)
        {
            try
            {
                // Cache the two property references once per session
                if (!_healthPropSearchDone)
                {
                    _healthPropSearchDone = true;

                    _villagerHealthProp = hunter.GetType()
                        .GetProperty("villagerHealth", AllInstance);

                    Type? vhType = FindType("VillagerHealth");
                    if (vhType != null)
                        _healthValueProp = vhType.GetProperty("health", AllInstance);

                    if (_villagerHealthProp == null || _healthValueProp == null)
                        MelonLogger.Warning(
                            "[WotW] VillagerHealth props not found — low-HP check disabled.");
                    else
                        MelonLogger.Msg(
                            "[WotW] Health check ready: Villager.villagerHealth.health (0–1)");
                }

                if (_villagerHealthProp == null || _healthValueProp == null)
                    return 1.0f;

                var vh = _villagerHealthProp.GetValue(hunter);
                if (vh == null) return 1.0f;

                var raw = _healthValueProp.GetValue(vh);
                if (raw is float f) return Mathf.Clamp01(f);
            }
            catch { }
            return 1.0f;
        }

        /// <summary>
        /// Returns false if the hunter's assigned building reports that ammo or
        /// weapon is missing from storage. Returns true (ammo OK) if field can't
        /// be read — safe default so hunters aren't blocked on lookup failure.
        ///
        /// CONFIRMED fields from dump: HunterBuilding.isAmmoMissingFromStorages,
        ///                             HunterBuilding.isWeaponMissingFromStorages
        /// Field references are cached after first discovery — zero repeated heap
        /// allocations on subsequent calls.
        /// </summary>
        private static bool IsHunterAmmoAvailable(Component hunter)
        {
            try
            {
                var building = FindAssignedHunterBuilding(hunter);
                if (building == null) return true;

                // Discover field references once per session
                if (!_ammoFieldSearchDone)
                {
                    _ammoFieldSearchDone = true;
                    Type? check = building.GetType();
                    while (check != null &&
                           check.Name != "MonoBehaviour" && check.Name != "Object")
                    {
                        if (_ammoMissingField == null)
                            _ammoMissingField = check.GetField(
                                "isAmmoMissingFromStorages", AllInstance);
                        if (_weaponMissingField == null)
                            _weaponMissingField = check.GetField(
                                "isWeaponMissingFromStorages", AllInstance);
                        if (_ammoMissingField != null && _weaponMissingField != null)
                            break;
                        check = check.BaseType;
                    }
                    if (_ammoMissingField == null)
                        MelonLogger.Msg("[WotW] isAmmoMissingFromStorages not found — " +
                                        "ammo check disabled.");
                }

                if (_ammoMissingField == null) return true;

                bool ammoMissing   = (bool)(_ammoMissingField.GetValue(building)   ?? false);
                bool weaponMissing = _weaponMissingField != null &&
                                     (bool)(_weaponMissingField.GetValue(building) ?? false);
                return !ammoMissing && !weaponMissing;
            }
            catch { }
            return true;
        }


        // ── BGH targeting priority helpers ────────────────────────────────────

        /// <summary>
        /// Returns the BGH targeting priority for an animal.
        ///   Bear = 3  (highest yield + highest threat)
        ///   Wolf = 2  (pack threat, good pelt)
        ///   Boar = 1  (moderate)
        ///   Other = 0 (deer, groundhog, etc.)
        /// </summary>
        private static int GetAnimalPriority(Component animal)
        {
            if (animal == null) return 0;
            Type? t = animal.GetType();
            while (t != null && t.Name != "MonoBehaviour" && t.Name != "Component")
            {
                if (t.Name == "Bear") return 3;
                if (t.Name == "Wolf") return 2;
                if (t.Name == "Boar") return 1;
                t = t.BaseType;
            }
            return 0;
        }

        /// <summary>
        /// Scans for a dangerous animal with a higher priority than currentPriority
        /// within searchRadius of hunterPos. Returns the closest one found, or null.
        /// Reads from the shared AggressiveAnimal snapshot (no dedicated scan).
        /// </summary>
        private static Component? FindHigherPriorityAnimal(
            Vector3 hunterPos, Component currentTarget,
            int currentPriority, float searchRadius)
        {
            try
            {
                Component? best    = null;
                int        bestPri = currentPriority;
                float      bestDistSqr = float.MaxValue;
                float      radiusSqr   = searchRadius * searchRadius;

                // Single pass over the shared AggressiveAnimal snapshot —
                // filter each by name and compare priorities on the fly.
                // Bear=3, Wolf=2, Boar=1. Anything else is skipped.
                foreach (var animal in GetCachedAggressiveAnimals())
                {
                    if (animal == null || (Component)animal == currentTarget) continue;

                    int pri;
                    string name = animal.GetType().Name;
                    if      (name == "Bear") pri = 3;
                    else if (name == "Wolf") pri = 2;
                    else if (name == "Boar") pri = 1;
                    else continue;

                    if (pri <= currentPriority) continue;

                    float dSqr = (animal.transform.position - hunterPos).sqrMagnitude;
                    if (dSqr > radiusSqr) continue;

                    if (pri > bestPri || (pri == bestPri && dSqr < bestDistSqr))
                    {
                        best         = animal;
                        bestPri      = pri;
                        bestDistSqr  = dSqr;
                    }
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns true if the given animal Component is a dangerous predator
        /// (Wolf, Boar, Bear). Deer and Groundhog don't need kiting.
        /// Fox is aggressive (AggressiveAnimal confirmed) but lower priority.
        /// </summary>
        private static bool IsDangerousAnimal(Component animal)
        {
            if (animal == null) return false;
            Type? t = animal.GetType();
            while (t != null)
            {
                string n = t.Name;
                if (n == "Wolf" || n == "Boar" || n == "Bear") return true;
                t = t.BaseType;
                if (t?.Name == "MonoBehaviour" || t?.Name == "Component") break;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the animal is a Boar.
        /// Boar: flees when healthy, turns and charges when wounded (~50% HP).
        /// Post-shot retreat and proximity watcher skip boar when it's fleeing.
        /// </summary>
        private static bool IsBoar(Component animal)
        {
            if (animal == null) return false;
            Type? t = animal.GetType();
            while (t != null)
            {
                if (t.Name == "Boar") return true;
                t = t.BaseType;
                if (t?.Name == "MonoBehaviour" || t?.Name == "Component") break;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the animal is a Bear.
        /// Bear: charges when healthy, flees when near death (~25% HP).
        /// Post-shot retreat and proximity watcher skip bear when it's fleeing
        /// so the hunter can advance for kill shots instead of backing away.
        /// </summary>
        private static bool IsBear(Component animal)
        {
            if (animal == null) return false;
            Type? t = animal.GetType();
            while (t != null)
            {
                if (t.Name == "Bear") return true;
                t = t.BaseType;
                if (t?.Name == "MonoBehaviour" || t?.Name == "Component") break;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the animal is a Wolf.
        /// Wolves are always aggressive — never flee, always charge.
        /// Post-shot retreat fires unconditionally for wolves.
        /// </summary>
        private static bool IsWolf(Component animal)
        {
            if (animal == null) return false;
            Type? t = animal.GetType();
            while (t != null)
            {
                if (t.Name == "Wolf") return true;
                t = t.BaseType;
                if (t?.Name == "MonoBehaviour" || t?.Name == "Component") break;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the animal's movement is directed toward the hunter
        /// (i.e. it is charging, not fleeing). Uses NavMeshAgent velocity dot product.
        /// Threshold 0.25 = within ~75° arc toward hunter counts as approaching.
        /// Returns false (safe default = treat as fleeing) if no NavMeshAgent found.
        /// </summary>
        private static bool IsAnimalApproaching(Component animal, Vector3 hunterPos)
        {
            try
            {
                var agent = animal.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent == null || agent.velocity.sqrMagnitude < 0.01f)
                    return false; // Stationary — not actively charging

                Vector3 toHunter = (hunterPos - animal.transform.position).normalized;
                return Vector3.Dot(agent.velocity.normalized, toHunter) > 0.25f;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Gets the best kiting destination: nearest Hunting Blind assigned to
        /// this cabin, else null (caller falls back to cabin position).
        /// </summary>
        private static Vector3? GetKitingDestination(
            Vector3 hunterPos, Vector3 cabinPos, float workRadius)
        {
            // 1. Assigned Hunting Blind (has cover + range bonus)
            var blind = HuntingBlindSystem.FindRetreatBlind(
                hunterPos, cabinPos, workRadius);
            if (blind != null) return blind.Position;

            // 2. Nothing found — caller falls back to cabin
            return null;
        }

        /// <summary>
        /// <summary>
        /// Clears the villager's CombatComponent target so the kiting AI doesn't
        /// immediately re-engage and fight a forced retreat (which was causing
        /// leash-retreat thrash: leash → retreat → kiting re-chases → leash
        /// again every 3s).
        /// </summary>
        private static void ClearCombatTarget(Component worker)
        {
            try
            {
                var combat = worker.GetComponent<CombatComponent>();
                if (combat == null) return;
                // myTargetDamageable is the current combat target (confirmed
                // from dump). Setting it null breaks the kiting loop.
                var field = combat.GetType().GetField("myTargetDamageable",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                field?.SetValue(combat, null);
            }
            catch { }
        }

        /// <summary>
        /// Redirects the hunter's movement target using NavMeshAgent.SetDestination.
        /// Also sets Villager._forceRetreatNextCheck = true (confirmed from dump) so
        /// the game's own AI acknowledges the retreat on its next tick, rather than
        /// immediately reassigning a new combat task that fights our NavMesh override.
        ///
        /// Set forceRetreat=false for proactive engagement moves — we want the hunter
        /// to walk toward the threat and let vanilla combat AI take over, NOT retreat.
        /// </summary>
        private static void SetMovementTarget(Component worker, Vector3 destination,
                                              bool forceRetreat = true)
        {
            try
            {
                // Signal the game AI to retreat on its next evaluation tick.
                // CONFIRMED from dump: Villager.<_forceRetreatNextCheck>k__BackingField
                // Field reference cached after first lookup — no repeated GetField calls.
                // Skip when forceRetreat=false (proactive engagement) — setting the
                // retreat flag while moving TOWARD a threat causes immediate flip-flop.
                if (forceRetreat)
                {
                    if (!_forceRetreatFieldDone)
                    {
                        _forceRetreatFieldDone = true;
                        Type? check = worker.GetType();
                        while (check != null &&
                               check.Name != "MonoBehaviour" && check.Name != "Object")
                        {
                            _forceRetreatField = check.GetField(
                                "<_forceRetreatNextCheck>k__BackingField", AllInstance);
                            if (_forceRetreatField != null) break;
                            check = check.BaseType;
                        }
                    }
                    try { _forceRetreatField?.SetValue(worker, true); } catch { }
                }

                var agent = worker.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null)
                {
                    agent.SetDestination(destination);
                    return;
                }

                // SetWanderPoints fallback — confirmed method name from dump
                // on PassiveAnimal; same method exists on Villager movement system
                Type? wanderCheck = worker.GetType();
                while (wanderCheck != null)
                {
                    var setter = wanderCheck.GetMethod("SetWanderPoints", AllInstance);
                    if (setter != null)
                    {
                        setter.Invoke(worker, new object[] {
                            new List<Vector3> { destination }
                        });
                        return;
                    }
                    wanderCheck = wanderCheck.BaseType;
                    if (wanderCheck?.Name == "MonoBehaviour" || wanderCheck?.Name == "Component") break;
                }
            }
            catch { }
        }

        /// <summary>
        /// After firing a shot, push the hunter slightly backward from the animal
        /// to maintain bow range during the reload window. Distance is based on
        /// the fallback reload time and confirmed hunter speed.
        ///
        /// Formula: retreat = reload × speed × 0.5 (partial kite, not full retreat)
        /// The hunter keeps firing while moving — the goal is incremental distance
        /// gain per shot, not running away.
        /// </summary>
        // Per-hunter active kite tracking. Ends when reload expires or target dies.
        private static readonly Dictionary<int, float> _kiteEndTime =
            new Dictionary<int, float>();
        private static readonly Dictionary<int, Component> _kiteTarget =
            new Dictionary<int, Component>();

        private static void ApplyPostShotRetreat(Component hunter, Vector3 animalPos)
        {
            try
            {
                float reload = AnimalBehaviorSystem.KitingCalculator
                                   .GetEffectiveReloadSeconds(hunter)
                               / WardenOfTheWildsMod.HuntingLodgeBigGameShootMult.Value;

                // Record the kite window so per-frame updates (see
                // UpdateActiveKites) maintain backward pressure across the
                // whole reload period, not just at arrow-release moment.
                int hKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(hunter);
                _kiteEndTime[hKey] = Time.time + reload;

                // Try to find the target Component for continuous tracking
                Component target = FindNearestThreatInRadius(hunter.transform.position, 50f);
                if (target != null) _kiteTarget[hKey] = target;

                // Fire an immediate kite step right now — don't wait for first tick
                ApplyKiteStep(hunter, animalPos);
            }
            catch { }
        }

        /// <summary>
        /// One backward step — hunter moves away from target by (reload × speed).
        /// Called at arrow release and then continuously from UpdateActiveKites.
        /// </summary>
        private static void ApplyKiteStep(Component hunter, Vector3 animalPos)
        {
            try
            {
                Vector3 hPos = hunter.transform.position;
                Vector3 dir = (hPos - animalPos).normalized;

                float reload = AnimalBehaviorSystem.KitingCalculator
                                   .GetEffectiveReloadSeconds(hunter)
                               / WardenOfTheWildsMod.HuntingLodgeBigGameShootMult.Value;

                // Read hunter's LIVE movement speed so tech-tree bonuses
                // (e.g., Trailblazing) and FastVillagers both propagate into
                // the kite distance calc. Passing 0 here forced a fallback
                // that ignored player progression.
                float liveSpeed = 0f;
                var agent = hunter.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null && agent.speed > 0f) liveSpeed = agent.speed;

                float speed  = AnimalBehaviorSystem.KitingCalculator
                                   .GetEffectiveHunterSpeed(liveSpeed)
                               * WardenOfTheWildsMod.HuntingLodgeSpeedMult.Value;

                // FULL optimal retreat distance (no × 0.5). Hunter is trying to
                // stay ahead of a charging wolf; half-steps don't beat the chase.
                float retreatDist = AnimalBehaviorSystem.KitingCalculator
                    .OptimalRetreatDistance(reload, speed);

                Vector3 retreatPos = hPos + dir * retreatDist;

                // forceRetreat=false — hunter moves back but stays engaged.
                SetMovementTarget(hunter, retreatPos, forceRetreat: false);
            }
            catch { }
        }

        /// <summary>
        /// Per-frame kite maintenance. Iterates all active kites and re-asserts
        /// backward movement so combat AI's approach logic can't pull the hunter
        /// back into melee during reload. Ends when reload window expires.
        /// Called from the WagonShopEnhancement-style update loop... but we don't
        /// have one. Instead, piggyback on the existing HuntSubTask postfix which
        /// fires at task-tick frequency (~0.2s).
        /// </summary>
        public static void UpdateActiveKites()
        {
            if (_kiteEndTime.Count == 0) return;

            var expired = new List<int>();
            foreach (var kv in _kiteEndTime)
            {
                if (Time.time >= kv.Value) { expired.Add(kv.Key); continue; }

                if (!_kiteTarget.TryGetValue(kv.Key, out Component target) || target == null)
                { expired.Add(kv.Key); continue; }

                // Find hunter by walking the registry keyed at _buildingWorkers
                Component hunter = FindHunterByKey(kv.Key);
                if (hunter == null) { expired.Add(kv.Key); continue; }

                ApplyKiteStep(hunter, target.transform.position);
            }
            foreach (var k in expired)
            {
                _kiteEndTime.Remove(k);
                _kiteTarget.Remove(k);
            }
        }

        /// <summary>Reverse-lookup: find a hunter Component by its hash key.</summary>
        private static Component FindHunterByKey(int hKey)
        {
            foreach (var kvp in _buildingWorkers)
            {
                foreach (var worker in kvp.Value)
                {
                    if (worker == null) continue;
                    if (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(worker) == hKey)
                        return worker;
                }
            }
            return null;
        }

        // ── Cabin lookup ──────────────────────────────────────────────────────
        // Uses the same L2 building cache as IsHuntingLodgeHunter — no extra
        // FindObjectsOfType call needed.
        private static Component? FindCabinForHunter(Component hunter)
        {
            try
            {
                // Ensure building cache is warm (IsHuntingLodgeHunter populates it)
                // If called independently, do a quick inline refresh check.
                if (Time.time >= _lodgeBuildingCacheExpiry && _cachedLodgeBuildings.Count == 0)
                    IsHuntingLodgeHunter(hunter); // populate cache

                Vector3 vPos = hunter.transform.position;
                float bestDist = float.MaxValue;
                Component? best = null;

                foreach (var entry in _cachedLodgeBuildings)
                {
                    if (entry.Building == null) continue;
                    float d = Vector3.Distance(entry.Building.transform.position, vPos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = entry.Building;
                    }
                }
                return best;
            }
            catch { }
            return null;
        }

        // ── Most-derived game component extraction ───────────────────────────
        /// <summary>
        /// Walks up the predator's parent chain to find the first Component that
        /// passes IsDangerousAnimal — i.e. the actual Wolf/Bear/Boar MonoBehaviour.
        /// Needed because bear attacks can originate from a hitbox that is a
        /// SEPARATE root from the AggressiveAnimal component, giving two different
        /// RuntimeHelpers.GetHashCode values for the same physical animal.
        /// Falls back to the original component if nothing is found in hierarchy.
        /// </summary>
        /// <summary>
        /// Returns the nearest dangerous animal Component within radius of pos,
        /// reading from the shared AggressiveAnimal snapshot. Null if none found.
        /// </summary>
        private static Component? FindNearestDangerousAnimalTo(Vector3 pos, float radius)
        {
            try
            {
                Component? best     = null;
                float      bestDist = radius * radius;
                foreach (var animal in GetCachedAggressiveAnimals())
                {
                    if (animal == null) continue;
                    string name = animal.GetType().Name;
                    if (name != "Bear" && name != "Wolf" && name != "Boar") continue;
                    float d = (animal.transform.position - pos).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; best = animal; }
                }
                return best;
            }
            catch { return null; }
        }

        private static Component? NormalizePredatorComponent(Component predator)
        {
            if (predator == null) return null;
            Transform? t = predator.transform;
            while (t != null)
            {
                foreach (var comp in t.GetComponents<Component>())
                {
                    if (comp != null && IsDangerousAnimal(comp)) return comp;
                }
                t = t.parent;
            }
            // Not found walking up — scan nearby for the actual animal
            // (handles detached hitbox roots)
            return FindNearestDangerousAnimalTo(predator.transform.position, 12f)
                   ?? predator;
        }

        // GetComponent<Component>() returns Transform first — completely useless
        // for type checks (IsDangerousAnimal, IsAnyHunter, etc.).
        // This iterates ALL components on a GO and returns the one with the
        // deepest game-code inheritance depth, i.e. the actual Wolf/Villager/etc.
        private static Component? GetGameComponent(GameObject go)
        {
            if (go == null) return null;
            Component? best   = null;
            int        bestDepth = -1;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                int   depth = 0;
                Type? t     = comp.GetType();
                while (t != null
                       && t.Name != "MonoBehaviour" && t.Name != "Behaviour"
                       && t.Name != "Component"     && t.Name != "Object")
                {
                    depth++;
                    t = t.BaseType;
                }
                if (depth > bestDepth) { bestDepth = depth; best = comp; }
            }
            return best;
        }

        // ── IDamageable → Component extraction ───────────────────────────────
        // IDamageable is a game interface; Harmony gives us the object.
        // Extract the most-derived game Component via gameObject property.
        private static Component? ExtractComponent(object damageable)
        {
            if (damageable == null) return null;
            // If the IDamageable IS already a MonoBehaviour/Component, return directly.
            // Wolf, Villager, etc. all extend MonoBehaviour so this is the common path.
            if (damageable is Component c) return c;

            // Fallback: extract via gameObject / transform property
            try
            {
                var goProp = damageable.GetType()
                    .GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance);
                if (goProp != null)
                {
                    var go = goProp.GetValue(damageable) as GameObject;
                    if (go != null) return GetGameComponent(go);
                }

                var tfProp = damageable.GetType()
                    .GetProperty("transform", BindingFlags.Public | BindingFlags.Instance);
                if (tfProp != null)
                {
                    var tf = tfProp.GetValue(damageable) as Transform;
                    if (tf != null) return GetGameComponent(tf.gameObject);
                }
            }
            catch { }
            return null;
        }

        // ── Type finder ───────────────────────────────────────────────────────
        // Fast path used in hot-code: patches, caches, IsAnyHunter etc.
        // Searches by qualified name only — no full assembly scan.
        // Cache: types never move across the AppDomain, so a lookup by simple
        // name can be memoised for the life of the process. Null results are
        // also cached so a name that truly doesn't exist doesn't get re-scanned.
        private static readonly Dictionary<string, Type?> _typeCache =
            new Dictionary<string, Type?>();

        private static Type? FindType(string name)
        {
            if (_typeCache.TryGetValue(name, out var cached)) return cached;

            Type? found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = asm.GetType(name);
                if (found != null) break;
            }
            _typeCache[name] = found;
            return found;
        }

        // Slow path used ONLY from the dump — iterates all types in all assemblies
        // to find nested types (e.g. Villager+WoundedEscortState), namespaced types
        // (e.g. SomeCo.SomeNS.WoundedEscortState), and other types that don't match
        // by simple `asm.GetType(name)`.
        private static Type? FindTypeForDump(string name)
        {
            // Try the fast path first
            var fast = FindType(name);
            if (fast != null) return fast;

            // Full scan — acceptable since this only runs once per session at map load
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == name) return t;
                }
                catch { }
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  PROACTIVE THREAT SCAN
        //  Runs every 2.5s. For each hunter building with registered workers,
        //  finds the nearest dangerous animal (Bear/Wolf/Boar) within the
        //  building's hunting radius. Idle hunters (no active combat, not
        //  recently kited) are directed toward the threat so they engage
        //  proactively instead of waiting to be ambushed.
        //
        //  T1 and T2 hunters use identical scan logic. T2 Hunting Lodge hunters
        //  naturally perform better via existing fire-rate and damage multipliers.
        // ════════════════════════════════════════════════════════════════════════

        // ── Direct combat target injection via CombatComponent.SetTarget() ────
        //
        // Discovery (2026-04-21): CombatComponent.SetTarget(...) is public.
        // This is the RIGHT way to tell a villager "engage this target" — it
        // goes through the game's native combat target flow, setting up
        // pathfinding, approach, attacks. Vanilla AI takes over from there.
        //
        // Signature:
        //   SetTarget(IDamageable newTarget, CombatAction action,
        //             TargetSourceIdentifier sourceId, AIPathQueryData? = null,
        //             bool targetIsInputSpecified = false,
        //             bool allowPathRequeryIfSameTarget = false)
        //
        // Why the old code (_villagerTarget / villagerTargetObj setters)
        // didn't work: those are display/state fields, not the actual combat
        // target driver. Setting them doesn't engage the combat FSM.
        private static bool TryCommandHunterToAttack(Component worker, Component threat)
        {
            if (worker == null || threat == null) return false;

            try
            {
                var combatComp = worker.GetComponent<CombatComponent>();
                if (combatComp == null) return false;

                var targetDamageable = threat.GetComponent<IDamageable>();
                if (targetDamageable == null)
                {
                    // AggressiveAnimals often have their IDamageable on a child
                    // GameObject (the "Active" subobject). Fall back to a recursive
                    // lookup.
                    targetDamageable = threat.GetComponentInChildren<IDamageable>();
                }
                if (targetDamageable == null) return false;

                combatComp.SetTarget(
                    newTarget: targetDamageable,
                    newTargetCombatAction: CombatAction.Attack,
                    newTargetSourceIdentifier: TargetSourceIdentifier.Search);
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] TryCommandHunterToAttack: {ex.Message}");
                return false;
            }
        }

        // ── HuntSubTask field cache ───────────────────────────────────────────
        // ownerDamageable: IDamageable — the hunter (Villager implements IDamageable)
        // hunterBuilding:  HunterBuilding — for radius read
        private static System.Reflection.FieldInfo? _huntSubOwnerDamageable  = null;
        private static System.Reflection.FieldInfo? _huntSubHunterBuilding   = null;
        private static bool                          _huntSubFieldSearchDone  = false;

        // ── HuntSubTask.IsSubTaskValidToContinue POSTFIX ──────────────────────
        // IsSubTaskValidToContinue: true = keep wandering, false = prey found.
        // When the vanilla scan returns true (no prey) but a predator is in the
        // work radius, we set _villagerTarget = predator and flip to false so
        // HuntingAttackTargetTask transitions to attack mode.
        public static void HuntSubTaskIsValidContinuePostfix(
            object __instance,
            ref bool __result)
        {
            // Maintain any active post-shot kites — runs at hunt-task tick rate
            // (frequent enough to keep backward pressure during reload). This is
            // cheap: typically 0-2 active kites at any moment.
            UpdateActiveKites();

            try
            {
                // Cache field infos once
                if (!_huntSubFieldSearchDone)
                {
                    _huntSubFieldSearchDone = true;
                    Type? t = __instance.GetType();
                    while (t != null && t.Name != "Object")
                    {
                        if (_huntSubOwnerDamageable == null)
                            _huntSubOwnerDamageable = t.GetField("ownerDamageable", AllInstance);
                        if (_huntSubHunterBuilding == null)
                            _huntSubHunterBuilding = t.GetField("hunterBuilding", AllInstance);
                        if (_huntSubOwnerDamageable != null && _huntSubHunterBuilding != null) break;
                        t = t.BaseType;
                    }
                    MelonLogger.Msg($"[WotW] HuntSubTask fields: " +
                        $"ownerDamageable={_huntSubOwnerDamageable != null}, " +
                        $"hunterBuilding={_huntSubHunterBuilding != null}");
                }

                if (_huntSubOwnerDamageable == null) return;

                // Get hunter Component from ownerDamageable (Villager implements IDamageable)
                var ownerDamageable = _huntSubOwnerDamageable.GetValue(__instance);
                var hunter = ownerDamageable as Component;
                if (hunter == null) return;
                if (!IsAnyHunter(hunter)) return;

                // Get building + hunting radius for both chase safety and proactive scans
                Component? hBuilding = _huntSubHunterBuilding?.GetValue(__instance) as Component;
                float hRadius = hBuilding != null ? GetHunterBuildingRadius(hBuilding) : 100f;

                // ── Chase safety: pursuit leash + ambush detection ────────────
                // These checks run ALWAYS (regardless of __result), because the
                // danger happens while the hunter is actively engaged, not while
                // they're wandering looking for prey.
                if (EvaluateChaseSafety(hunter, hBuilding, hRadius))
                {
                    __result = false;  // break off hunt task — retreat fired
                    return;
                }

                // ── Proactive engagement only if vanilla found no prey ────────
                if (!__result) return; // vanilla found prey → don't add another target

                // Skip wounded
                float hp = GetHunterHealthPercent(hunter);
                float hpThresh = WardenOfTheWildsMod.HunterLowHealthThreshold.Value;
                if (hpThresh > 0f && hp < hpThresh) return;

                // Rate limit — 15s cooldown so we don't thrash on every sub-task tick
                int workerKey = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(hunter);
                if (_lastProactiveTimes.TryGetValue(workerKey, out float lastPro)
                    && Time.time - lastPro < 15f) return;

                // Stamp the cooldown BEFORE the scan. Previously this was only
                // stamped after a successful threat-command, which meant idle
                // hunters (no nearby threats) scanned on every sub-task tick —
                // 14 hunters × 10-20 ticks/s × threat-iterate-all-animals =
                // visible frame stutter every ~1.5s. Stamping unconditionally
                // caps the scan to once per 15s per hunter regardless of
                // outcome.
                _lastProactiveTimes[workerKey] = Time.time;

                // Find nearest predator in work radius (reuse hBuilding / hRadius from top)
                Component? threat = FindNearestThreatInRadius(
                    hunter.transform.position, hRadius);
                if (threat == null) return;

                // Command the hunter to attack the threat via the public
                // CombatComponent.SetTarget path — native combat AI handles
                // pathing, approach, and attacks from here.
                bool commanded = TryCommandHunterToAttack(hunter, threat);
                if (!commanded) return;

                __result = false;  // stop wandering — target acquired

                MelonLogger.Msg(
                    $"[WotW] Proactive engage: '{hunter.gameObject.name}' → " +
                    $"'{threat.gameObject.name}' ({threat.GetType().Name}) " +
                    $"{Vector3.Distance(hunter.transform.position, threat.transform.position):F0}u");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HuntSubTaskIsValidContinuePostfix: {ex.Message}");
            }
        }

        // ── Chase safety: leash + ambush detection ───────────────────────────
        //
        // Called every HuntSubTask tick. Checks two failure modes that kill T1
        // hunters:
        //
        //   1. PURSUIT LEASH  — hunter chased fleeing prey too far from cabin.
        //                       If distance from cabin > radius × LeashMult,
        //                       break off and retreat to cabin.
        //
        //   2. AMBUSH SCAN    — hunter walked into a pack. If aggressive-animal
        //                       count within AmbushScanRadius >= AmbushThreshold,
        //                       break off (one is the current target; 2+ means
        //                       an ambush came in).
        //
        // Returns true if a break-off was triggered (caller should flip
        // __result to false). Returns false when hunter can continue normally.

        private static readonly Dictionary<int, float> _lastChaseBreakTimes =
            new Dictionary<int, float>();
        // Bumped 3s → 20s: the kiting AI was re-chasing within the 3s window,
        // causing the leash to re-fire repeatedly (visible as 1-2s stutter in
        // the log). 20s gives the retreat a chance to actually complete before
        // re-evaluation. Kiting's own target logic will still handle combat.
        private const float ChaseBreakCooldown = 20f;

        private static bool EvaluateChaseSafety(
            Component hunter, Component? hBuilding, float hRadius)
        {
            try
            {
                // Rate-limit: don't fire retreat repeatedly if AI is processing
                int hKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(hunter);
                if (_lastChaseBreakTimes.TryGetValue(hKey, out float lastBreak)
                    && Time.time - lastBreak < ChaseBreakCooldown)
                    return false;

                Vector3 hunterPos = hunter.transform.position;

                // ── (1) Pursuit leash ─────────────────────────────────────────
                if (hBuilding != null)
                {
                    float leashMult = WardenOfTheWildsMod.HunterPursuitLeashMult.Value;
                    if (leashMult > 0f)
                    {
                        float leash = hRadius * leashMult;
                        float distFromCabin = Vector3.Distance(
                            hunterPos, hBuilding.transform.position);
                        if (distFromCabin > leash)
                        {
                            SetMovementTarget(hunter, hBuilding.transform.position,
                                forceRetreat: true);
                            // Also clear the combat target so kiting AI doesn't
                            // immediately re-engage and fight the retreat.
                            ClearCombatTarget(hunter);
                            _lastChaseBreakTimes[hKey] = Time.time;
                            // Log removed — leash was firing in tight loops
                            // (10+ times per hunter over a few minutes), each
                            // log call contributing to visible frame stutter.
                            return true;
                        }
                    }
                }

                // ── (2) Ambush scan ───────────────────────────────────────────
                int ambushThreshold = WardenOfTheWildsMod.HunterAmbushThreshold.Value;
                float ambushRadius = WardenOfTheWildsMod.HunterAmbushScanRadius.Value;
                if (ambushThreshold > 0 && ambushThreshold < 99 && ambushRadius > 0f)
                {
                    int threatCount = CountDangerousAnimalsInRadius(hunterPos, ambushRadius);
                    if (threatCount >= ambushThreshold)
                    {
                        Vector3 retreatDest = hBuilding != null
                            ? hBuilding.transform.position
                            : hunterPos;  // fallback — at least flag retreat
                        SetMovementTarget(hunter, retreatDest, forceRetreat: true);
                        _lastChaseBreakTimes[hKey] = Time.time;
                        MelonLogger.Msg(
                            $"[WotW] Chase break (ambush): '{hunter.gameObject.name}' — " +
                            $"{threatCount} threats within {ambushRadius:F0}u, retreating");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] EvaluateChaseSafety: {ex.Message}");
            }
            return false;
        }

        // ── Shared AggressiveAnimal cache ─────────────────────────────────────
        //
        // FindObjectsOfType is O(N) across ALL MonoBehaviours in the scene; at
        // 60 FPS across multiple consumers (shelter guard, chase safety,
        // proactive scan, carcass defense) it becomes the dominant per-frame
        // cost, especially after the recent spawn multipliers + Fox unlock
        // made AggressiveAnimal counts noticeably higher.
        //
        // Refreshes every CacheTTL seconds; all consumers share the snapshot.
        private static AggressiveAnimal[] _cachedAggressive = System.Array.Empty<AggressiveAnimal>();
        private static float _aggressiveCacheExpiry = 0f;
        // 2s TTL — animals move slowly enough that a 2-second lag is imperceptible
        // for chase-safety / proactive-engagement decisions, and this cache fires
        // every tick from EvaluateChaseSafety during active hunts. 0.75s produced
        // ~1.3Hz FindObjectsOfType scans and was the main combat-era stutter source.
        private const float AggressiveCacheTTL = 2.0f;

        public static AggressiveAnimal[] GetCachedAggressiveAnimals()
        {
            if (Time.time >= _aggressiveCacheExpiry)
            {
                _cachedAggressive = UnityEngine.Object.FindObjectsOfType<AggressiveAnimal>();
                _aggressiveCacheExpiry = Time.time + AggressiveCacheTTL;
            }
            return _cachedAggressive;
        }

        /// <summary>Counts AggressiveAnimals within a radius of the given point.</summary>
        private static int CountDangerousAnimalsInRadius(Vector3 center, float radius)
        {
            int count = 0;
            float rSqr = radius * radius;
            foreach (var animal in GetCachedAggressiveAnimals())
            {
                if (animal == null) continue;
                var comp = animal as Component;
                if (comp == null) continue;
                if ((comp.transform.position - center).sqrMagnitude <= rSqr)
                    count++;
            }
            return count;
        }

        // ── Proactive scan helpers ────────────────────────────────────────────

        // Work-radius field names to try (in order) via reflection.
        // huntingRadius is the first candidate — confirmed naming pattern from dumps.
        private static readonly string[] HuntingRadiusCandidates = {
            "huntingRadius", "_huntingRadius",
            "workRadius",    "_workRadius",
            "huntWorkRadius", "workerRadius",
            "radius",        "_radius",
        };
        private static string? _confirmedRadiusField = null;
        private static bool    _radiusFieldSearchDone = false;

        private static float GetHunterBuildingRadius(Component building)
        {
            const float FallbackRadius = 100f;
            try
            {
                // Discover field once per session
                if (!_radiusFieldSearchDone)
                {
                    _radiusFieldSearchDone = true;
                    Type? check = building.GetType();
                    while (check != null &&
                           check.Name != "MonoBehaviour" && check.Name != "Object")
                    {
                        foreach (string candidate in HuntingRadiusCandidates)
                        {
                            var fi = check.GetField(candidate, AllInstance);
                            if (fi != null && (fi.FieldType == typeof(float)
                                            || fi.FieldType == typeof(int)))
                            {
                                _confirmedRadiusField = candidate;
                                MelonLogger.Msg(
                                    $"[WotW] HunterBuilding work radius field: '{candidate}' " +
                                    $"on {check.Name}");
                                break;
                            }
                        }
                        if (_confirmedRadiusField != null) break;
                        check = check.BaseType;
                    }
                    if (_confirmedRadiusField == null)
                        MelonLogger.Msg(
                            $"[WotW] HunterBuilding: work radius field not found — " +
                            $"using fallback {FallbackRadius}u");
                }

                if (_confirmedRadiusField != null)
                {
                    var raw = building.GetType()
                        .GetField(_confirmedRadiusField, AllInstance)
                        ?.GetValue(building);
                    if (raw is float f && f > 1f) return f;
                    if (raw is int   i && i > 1)  return (float)i;
                }
            }
            catch { }
            return FallbackRadius * WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value;
        }

        /// <summary>
        /// Finds the nearest Bear, Wolf, or Boar within radius of pos.
        /// Reads from the shared AggressiveAnimal snapshot (no dedicated scan).
        /// </summary>
        private static Component? FindNearestThreatInRadius(Vector3 pos, float radius)
        {
            try
            {
                float radiusSqr = radius * radius;
                Component? best     = null;
                float      bestDist = radiusSqr;

                foreach (var animal in GetCachedAggressiveAnimals())
                {
                    if (animal == null) continue;
                    string name = animal.GetType().Name;
                    if (name != "Bear" && name != "Wolf" && name != "Boar") continue;

                    float dSqr = (animal.transform.position - pos).sqrMagnitude;
                    if (dSqr < bestDist) { bestDist = dSqr; best = animal; }
                }
                return best;
            }
            catch { return null; }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  DISCOVERY DUMP (preserved, call commented out in ApplyPatches)
        // ════════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Dumps all methods on combat-related types to the MelonLoader log.
        /// Re-enable by uncommenting the DumpCombatMethods() call in ApplyPatches()
        /// after any game update.
        /// </summary>
        public static void DumpCombatMethods()
        {
            string[] typesToDump = {
                // ── Item IDs — need Raw Meat value for SmokehousePatches ────────
                // ItemID is an enum. FindTypeForDump will find it even if nested.
                // Look for "RawMeat" (or similar) in the output.
                "ItemID",

                // ── Already confirmed — keeping for reference ─────────────────
                "HunterBuilding",
                "Villager",
                "Character",        // base class of Villager
                "CombatManager",

                // ── Health component — VillagerHealth is a component on Villager ─
                // Dump confirmed: Villager.get_villagerHealth() → VillagerHealth
                // Need to find currentHealth/maxHealth fields on VillagerHealth itself
                "VillagerHealth",

                // ── Hunter personal carried inventory (arrows check) ──────────
                // GOAL: check how many arrows the hunter is personally carrying,
                // NOT the cabin's building storage (which is empty during logistics
                // delivery gaps and causes false "no ammo" triggers).
                //
                // Suspected path:  Villager → some inventory/equipment component
                //                           → item count for Arrow ItemID
                //
                // Looking for: the component that tracks what a villager is CARRYING
                // right now (their personal load, not building stock). Also need:
                //   - Arrow item ID (covered by ItemID enum dump above)
                //   - Bow/crossbow item IDs (for weapon check)
                //   - GetItemCount(ItemID) or similar method to query carried count
                //
                // Butchering speed candidates also listed here — hunter processes
                // carcasses into raw meat at the cabin. Looking for the task class
                // or action that has a work-time / duration field to scale.
                "VillagerItemCarrier", "ItemCarrier", "CarriedItemStorage",
                "VillagerInventory", "ItemInventory", "VillagerLoadout",
                "VillagerCarry", "CarryComponent", "VillagerPack",
                "ItemStorage", "CarriedItems", "ItemContainer", "ResourceContainer",
                "HunterEquipment", "VillagerEquipment", "EquipmentManager",
                "ItemBasedEquipmentManager",   // seen as field type on Villager dump
                // ── Butchering / carcass processing speed ─────────────────────
                // GOAL: find the task/action that processes carcasses → raw meat
                // at the Hunter Cabin, so we can scale its duration with a mult pref.
                "ButcherCarcassTask", "ProcessCarcassTask", "CarcassProcessingTask",
                "ButcherTask", "SlaughterTask", "ProcessCarcassAction",
                "HunterCabinTask", "CarcassTask", "MeatProcessingTask",
                "ButcherAction", "HarvestCarcassTask", "FieldDressTask",

                // ── Weapon / projectile data ──────────────────────────────────
                // Looking for: bow attack interval, crossbow attack interval,
                // damage values, projectile speed (for arc/flat trajectory diff)
                "ArrowProjectile", "Projectile", "BoltProjectile",
                "WeaponData", "BowData", "CrossbowData",
                "HunterWeapon", "RangedWeapon", "Weapon",
                "ProjectileData", "ArrowData",

                // ── Wounded escort system ─────────────────────────────────────
                // CONFIRMED: Villager.get/set_woundedEscortState → WoundedEscortState enum
                // Need enum values to know which state = "freshly wounded, needs escort"
                // so we can patch the setter and route hunters toward their cabin.
                // Also need: what sets woundedEscortState (the method that makes them Wounded)
                "WoundedEscortState",   // enum values
                "WoundedVillager", "VillagerWounded", "WoundedHandler",

                // ── Task system — confirmed from dump: Villager has ───────────
                //   get_taskReceiverComponent()  → TaskReceiverComponent
                //   get_taskProcessorComponent() → TaskProcessorComponent
                //   get/set_blockNewTaskSearchesPriority() → Nullable<T>  (blocks AI task pickup)
                //
                // Need: AddTaskSearchEntry / RemoveTaskSearchEntry on these components,
                //       VillagerRetreatSearchEntry (flee task entry type),
                //       blockNewTaskSearchesPriority generic type T (priority enum/int?),
                //       hunt task entry class, carcass pickup task class.
                "TaskReceiverComponent", "TaskProcessorComponent",
                "VillagerTaskSearchEntry", "TaskSearchEntry", "IVillagerTaskSearchEntry",
                "VillagerRetreatSearchEntry", "HunterTaskSearchEntry",
                "FleeFromDangerSearchEntry",   // confirmed on animals — does villager use this too?
                "CarcassPickupTask", "HuntTask", "ForageTask",
                "VillagerTaskSystem", "TaskScheduler", "VillagerTask",
                "TaskEntry", "SearchEntry", "VillagerSearchEntry",
                "WorkTask", "BuildingTask", "WorkerTask",

                // ── Policy / research system ──────────────────────────────────
                // Looking for: how crossbow policy is applied to HunterBuilding,
                // what field/property changes when the policy is active,
                // attack speed modifier, damage modifier
                "Policy", "PolicyData", "PolicyManager",
                "ResearchPolicy", "ActivePolicy", "TownPolicy",
                "HunterPolicy", "CrossbowPolicy", "RangedPolicy",
                "PolicyEffect", "PolicyModifier",
                "Research", "ResearchManager", "ResearchData",
                "TechTree", "Upgrade", "UpgradeData",
                "BuildingPolicy", "VillagerPolicy",

                // ── Shooting / attack stats on the villager side ──────────────
                // Looking for: attackInterval, shootInterval, reloadTime,
                // bowCooldown, crossbowCooldown, attackSpeed, rangedAttackSpeed
                "VillagerCombat", "RangedCombat", "CombatStats",
                "VillagerStats", "CharacterStats",
                "AttackData", "RangedAttackData",

                // ── Predator animal types — need SetTarget method names ───────
                // We already patch AggressiveAnimal.OnCombatTargetSet but have
                // never dumped its full method surface. Need: SetTarget,
                // SetCombatTarget, TryEngageTarget, or equivalent to call
                // from the proactive scan coroutine (Path B).
                "AggressiveAnimal",
                "DangerousAnimal",

                // ── Hunt task search entry — CONFIRMED: HuntSearchEntry ──────
                // base: TaskSearchEntry
                // ProcessNewTask(Nullable<float> relativePriorityToBeat, Task currentHighestPriorityTask) → Task
                // Fields: Villager villager, WorkBucket canStoreCarcassWorkBucket
                "HuntSearchEntry",

                // ── Hunt task types — found via broad scan ────────────────────
                // HuntingAttackTargetTask: the Task returned by HuntSearchEntry
                //   when it finds valid prey. Previous dump showed NO fields —
                //   target must be in base Task class. Dump Task + TaskSearchEntry
                //   to find where the prey reference lives, then dump ctors to
                //   know how to construct HuntingAttackTargetTask via reflection.
                "HuntingAttackTargetTask",
                "HuntSubTask",
                "Task",
                "TaskSearchEntry",
            };

            foreach (string typeName in typesToDump)
            {
                // Use the exhaustive dump finder — catches nested types like
                // Villager+WoundedEscortState that fail the fast FindType() path.
                Type? t = FindTypeForDump(typeName);
                if (t == null) { MelonLogger.Msg($"[WotW DUMP] {typeName}: NOT FOUND"); continue; }

                MelonLogger.Msg($"[WotW DUMP] ── {typeName} " +
                    $"(full: {t.FullName}, base: {t.BaseType?.Name}) ──");

                // Enum: dump named values with their int representation
                if (t.IsEnum)
                {
                    var names  = Enum.GetNames(t);
                    var values = Enum.GetValues(t);
                    for (int i = 0; i < names.Length; i++)
                        MelonLogger.Msg(
                            $"[WotW DUMP]   ENUM {names[i]} = {Convert.ToInt32(values.GetValue(i))}");
                    continue;
                }

                // Constructors — critical for instantiating tasks via reflection
                foreach (var ctor in t.GetConstructors(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public   |
                    System.Reflection.BindingFlags.NonPublic))
                {
                    var parms = string.Join(", ", Array.ConvertAll(
                        ctor.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                    MelonLogger.Msg($"[WotW DUMP]   CTOR({parms})");
                }
                foreach (var m in t.GetMethods(AllInstance | AllStatic))
                {
                    if (m.DeclaringType != t) continue;
                    var parms = string.Join(", ", Array.ConvertAll(
                        m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                    MelonLogger.Msg($"[WotW DUMP]   {m.ReturnType.Name} {m.Name}({parms})");
                }
                MelonLogger.Msg($"[WotW DUMP]   -- FIELDS --");
                foreach (var f in t.GetFields(AllInstance | AllStatic))
                {
                    if (f.DeclaringType != t) continue;
                    MelonLogger.Msg($"[WotW DUMP]   {f.FieldType.Name} {f.Name}");
                }
            }

            // ── Broad type scan: Hunt / Prey / Aggress ────────────────────────
            // Catches obfuscated or unexpectedly-named types the name list misses.
            // Primary goal: find the hunt task search entry class name and any
            // method on AggressiveAnimal that can force an aggro target assignment.
            MelonLogger.Msg("[WotW DUMP] ── BROAD SCAN: Hunt / Prey / Aggress types ──");
            var _seenBroadTypes = new HashSet<string>();
            foreach (var _scanAsm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var _scanType in _scanAsm.GetTypes())
                    {
                        string _sn = _scanType.Name;
                        if ((_sn.Contains("Hunt") || _sn.Contains("Prey")
                             || _sn.Contains("Aggress"))
                            && _seenBroadTypes.Add(_sn))
                        {
                            MelonLogger.Msg(
                                $"[WotW DUMP] SCAN: {_sn}  ({_scanType.FullName})");
                        }
                    }
                }
                catch { }
            }

            MelonLogger.Msg("[WotW DUMP] Combat method dump complete.");

            // Note: VillagerRetreatSearchEntry live values are read lazily on first
            // hunter combat event (TryReadVanillaRetreatThreshold) since villagers
            // aren't loaded yet when this dump fires at OnMapLoaded.

            WardenOfTheWilds.Systems.AnimalBehaviorSystem.DumpAnimalFields();
        }
    }
}
