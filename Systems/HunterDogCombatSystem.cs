using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Hunter-dog combat overhaul (Pets DLC).
    ///
    /// Vanilla already gives a dog assigned to a hunter a "defend" job
    /// (<c>Dog.defendable = VillagerOccupationHunter</c>) so it follows the
    /// hunter and attacks threats — but predators pick targets via a threat
    /// table, so the predator peels onto whoever deals damage (the hunter).
    /// This system adds the missing pieces:
    ///
    ///   • TAUNT (hard forced-target): when a predator aggros a defended
    ///     hunter, we force the predator's combat target onto the dog
    ///     (targetIsInputSpecified — the sticky path that resists vanilla
    ///     auto-reassign) and re-assert it on a pulse. One predator per dog.
    ///     The dog tanks until it hits its flee-HP threshold OR the hunter
    ///     retreats; then the taunt fades after a short tail so the predator
    ///     keeps chasing the fleeing dog (dragging it off the hunter) before
    ///     releasing.
    ///
    ///   • AUTO-FLEE: when the hunter retreats, the dog disengages and flees
    ///     with him (the still-live taunt keeps the predator on the fleeing
    ///     dog — the dog becomes a moving decoy).
    ///
    ///   • FLEE-HP SLIDER: overrides the dog's vanilla 0.5 retreat threshold.
    ///
    ///   • HEALTH / ARMOR BUFFS (all dogs): scale maxLife + add armor.
    ///
    /// Targeting is no-scan: hunter↔dog is resolved through the native
    /// <c>VillagerOccupationHunter.defenders</c> list and <c>Dog.defendable</c>.
    /// The only scene-wide query is a one-shot dog scan at map load to apply
    /// buffs (same pattern as AnimalSpawnTuningSystem).
    /// </summary>
    public static class HunterDogCombatSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static bool _registered;

        // ── Active taunts, keyed by predator instance hash ──────────────────
        private sealed class TauntEntry
        {
            public Component Predator;
            public GameObject Dog;
            public Component Hunter;      // Villager
            public float LastPulse;       // Time.time of last SetTarget
            public float FadeEndTime;     // -1 = still tanking; >0 = fading, release at this time
        }

        private static readonly Dictionary<int, TauntEntry> _activeTaunts =
            new Dictionary<int, TauntEntry>();

        // Per-instance baseline cache for armor + speeds (so reapply doesn't
        // compound across map loads / reassignments).
        private static readonly Dictionary<int, float> _origBaseArmor =
            new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _origRunSpeed =
            new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _origWalkSpeed =
            new Dictionary<int, float>();

        // Reusable scratch list so UpdateActiveTaunts allocates nothing per tick.
        private static readonly List<int> _expiredScratch = new List<int>();

        // ── Registration ────────────────────────────────────────────────────
        public static void Register(HarmonyLib.Harmony harmony)
        {
            if (_registered) return;
            try
            {
                Type dogType = AccessTools.TypeByName("Dog");
                if (dogType == null)
                {
                    MelonLogger.Msg(
                        "[WotW] HunterDogCombat: Dog type not found (pre-DLC build?). Skipping.");
                    _registered = true;
                    return;
                }

                // Dog.Awake postfix → apply health/armor buffs to every new dog.
                var awake = AccessTools.Method(dogType, "Awake");
                if (awake != null)
                    harmony.Patch(awake, postfix: new HarmonyMethod(
                        typeof(HunterDogCombatSystem), nameof(DogAwakePostfix)));

                // Dog.set_defendable postfix → re-apply our flee-HP override
                // right after vanilla resets it to 0.5 on assignment.
                var setDefendable = AccessTools.PropertySetter(dogType, "defendable");
                if (setDefendable != null)
                    harmony.Patch(setDefendable, postfix: new HarmonyMethod(
                        typeof(HunterDogCombatSystem), nameof(DogSetDefendablePostfix)));

                _registered = true;
                MelonLogger.Msg("[WotW] HunterDogCombat: patched Dog.Awake + set_defendable.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterDogCombat.Register: {ex.Message}");
            }
        }

        public static void OnMapLoaded()
        {
            _activeTaunts.Clear();
            _origBaseArmor.Clear();
            _origRunSpeed.Clear();
            _origWalkSpeed.Clear();

            // One-shot buff scan (only meaningful with the DLC present).
            if (!DlcDetection.PetsDlcActive) return;
            try
            {
                Type dogType = AccessTools.TypeByName("Dog");
                if (dogType == null) return;
                var dogs = UnityEngine.Object.FindObjectsOfType(dogType);
                int n = 0;
                foreach (var obj in dogs)
                {
                    if (obj is Component c) { ApplyBuffs(c.gameObject); n++; }
                }
                if (n > 0)
                    MelonLogger.Msg($"[WotW] HunterDogCombat: applied buffs to {n} dog(s) on map load.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterDogCombat.OnMapLoaded: {ex.Message}");
            }
        }

        // ── Harmony bodies ──────────────────────────────────────────────────
        public static void DogAwakePostfix(object __instance)
        {
            if (!DlcDetection.PetsDlcActive) return;
            if (__instance is Component c) ApplyBuffs(c.gameObject);
        }

        public static void DogSetDefendablePostfix(object __instance)
        {
            if (!DlcDetection.PetsDlcActive) return;
            try
            {
                ApplyFleePct(__instance);
                // The component is live by assignment time, so it's safe to
                // (re)apply health/armor here too — catches dogs assigned after
                // the map-load scan without waiting for a reload.
                if (__instance is Component c) ApplyBuffs(c.gameObject);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterDogCombat.set_defendable: {ex.Message}");
            }
        }

        // ── Buffs (all dogs) ────────────────────────────────────────────────
        private static void ApplyBuffs(GameObject dogGo)
        {
            try
            {
                if (dogGo == null) return;
                var dmg = dogGo.GetComponent<DamageableComponent>();
                if (dmg == null) return;

                // CRITICAL: the DamageableComponent may not be initialized yet
                // (at Dog.Awake, maxLife is still 0). Reading lifePercentage on
                // an uninitialized component computes 0/0 = NaN, and writing that
                // NaN back via RebaseLifePercentage permanently corrupts the dog
                // into an immortal (NaN health never satisfies life <= 0, and the
                // HUD renders (int)(NaN*100) as int.MinValue%). Bail until the
                // component is live — the map-load scan + assignment hook reapply
                // once maxLife is valid.
                float maxLife = dmg.maxLife;
                if (!(maxLife > 0f) || float.IsNaN(maxLife) || float.IsInfinity(maxLife))
                    return;

                // Health: SetMaxLifeMultiplier is an ABSOLUTE set, so reapplying
                // is naturally idempotent. Default 1.0 → multiplier 0 (no-op).
                dmg.SetMaxLifeMultiplier(
                    Mathf.Max(0f, WardenOfTheWildsMod.DogHealthMult.Value - 1f));

                // Only WRITE life to repair an already-corrupted value (NaN /
                // Infinity / out of [0,1]). Otherwise leave life alone — raising
                // maxLife just lets the dog heal into the bigger pool naturally.
                // Never feed a bad fraction back into RebaseLifePercentage.
                float pct = dmg.lifePercentage;
                if (float.IsNaN(pct) || float.IsInfinity(pct) || pct < 0f || pct > 1f)
                    dmg.RebaseLifePercentage(1f); // un-stick a corrupted dog → full heal

                // Armor: cache vanilla baseArmor per instance, always compute
                // from baseline so a reapply doesn't stack.
                int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(dmg);
                if (!_origBaseArmor.TryGetValue(id, out float baseArmor))
                {
                    baseArmor = dmg.baseArmor;
                    _origBaseArmor[id] = baseArmor;
                }
                dmg.baseArmor = baseArmor + WardenOfTheWildsMod.DogArmorBonus.Value;

                // Speed: scale the dog's run/walk base speeds so it keeps pace
                // with the hunter instead of trailing (vanilla has no manual dog
                // control, so a lagging dog simply can't be where the fight is).
                // Baseline-cached per instance so reapply doesn't compound.
                var land = dogGo.GetComponent<LandAnimal>();
                if (land != null)
                {
                    float spdMult = Mathf.Max(0.1f, WardenOfTheWildsMod.DogSpeedMult.Value);
                    int sid = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(land);
                    if (!_origRunSpeed.TryGetValue(sid, out float run))
                    {
                        run = land.movementSpeedBaseRun;
                        _origRunSpeed[sid] = run;
                    }
                    if (!_origWalkSpeed.TryGetValue(sid, out float walk))
                    {
                        walk = land.movementSpeedBase;
                        _origWalkSpeed[sid] = walk;
                    }
                    land.movementSpeedBaseRun = run  * spdMult;
                    land.movementSpeedBase    = walk * spdMult;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterDogCombat.ApplyBuffs: {ex.Message}");
            }
        }

        // ── Flee-HP override (hunting dogs) ─────────────────────────────────
        private static FieldInfo _retreatEntryField;
        private static bool _retreatEntryFieldTried;

        private static void ApplyFleePct(object dog)
        {
            if (dog == null) return;

            // Only override for dogs defending a hunter — leave doghouse guard
            // dogs on vanilla behavior.
            var defendableProp = dog.GetType().GetProperty("defendable", AllInstance);
            object defendable = defendableProp?.GetValue(dog);
            if (defendable == null) return;
            if (!(defendable is VillagerOccupationHunter)) return;

            if (!_retreatEntryFieldTried)
            {
                _retreatEntryFieldTried = true;
                Type t = dog.GetType();
                while (t != null && _retreatEntryField == null)
                {
                    _retreatEntryField = t.GetField("animalRetreatSearchEntry", AllInstance);
                    t = t.BaseType;
                }
            }
            var entry = _retreatEntryField?.GetValue(dog) as FleeFromDangerSearchEntry;
            if (entry == null) return;

            entry.onlyRetreatWhenInjured = true;
            entry.lifePercToRetreat =
                Mathf.Clamp01(WardenOfTheWildsMod.DogFleeHealthPct.Value);
        }

        // ── Taunt trigger (called when a hunter attacks a dangerous animal) ──
        /// <summary>
        /// The hunter is engaging <paramref name="predator"/> (its current
        /// attack/kite target — caller guarantees it's a dangerous animal).
        /// If the hunter has a healthy defending dog within leash range and the
        /// dog isn't already tanking something, force the predator onto the dog
        /// so it tanks instead of letting the predator turn on the hunter.
        ///
        /// Called repeatedly (every shot + every kite tick), so it doubles as
        /// the retry: the dog engages the moment it's within leash range, even
        /// if it was trailing when the fight started. Idempotent — a no-op once
        /// a taunt for this predator is already active.
        /// </summary>
        public static void EngageHuntersDog(Component predator, Component hunter)
        {
            try
            {
                if (predator == null || hunter == null) return;
                if (!DlcDetection.PetsDlcActive) return;
                if (!WardenOfTheWildsMod.HunterDogTauntEnabled.Value) return;

                int pKey = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(predator);
                if (_activeTaunts.ContainsKey(pKey)) return; // already tanking this predator

                GameObject dogGo = FindDefendingDog(hunter);
                if (dogGo == null) return;

                // 1-target rule, with priority swap. The dog holds ONE predator,
                // but it shouldn't stay locked on a lesser/fleeing one while the
                // hunter takes a bigger threat. If the dog is already tanking a
                // different predator, only swap to this one when it's a higher
                // danger rank (Bear>Wolf>Boar) or the current target is gone /
                // fleeing — otherwise keep the current tank (don't downgrade).
                int existingKey = 0;
                TauntEntry existing = null;
                foreach (var kv in _activeTaunts)
                    if (kv.Value.Dog == dogGo) { existing = kv.Value; existingKey = kv.Key; break; }

                if (existing != null)
                {
                    if (existing.Predator == predator) return; // same target already
                    bool currentGone    = existing.Predator == null;
                    bool currentFleeing  = !currentGone && IsRetreating(existing.Predator);
                    bool newIsHigher     = DangerRank(predator) > DangerRank(existing.Predator);
                    if (!(currentGone || currentFleeing || newIsHigher))
                        return; // keep the current (equal/lower priority) tank

                    ReleaseTaunt(existing);
                    _activeTaunts.Remove(existingKey);
                }

                // Leash range + dog-health gate.
                float leash = Mathf.Max(1f, WardenOfTheWildsMod.HunterDogTauntLeashRange.Value);
                if ((dogGo.transform.position - hunter.transform.position).sqrMagnitude
                    > leash * leash) return;

                var dogDmg = dogGo.GetComponent<DamageableComponent>();
                if (dogDmg == null || dogDmg.lifePercentage <= 0f) return;
                if (dogDmg.lifePercentage <= WardenOfTheWildsMod.DogFleeHealthPct.Value) return;

                if (!PulsePredatorOntoDog(predator, dogGo)) return;

                _activeTaunts[pKey] = new TauntEntry
                {
                    Predator    = predator,
                    Dog         = dogGo,
                    Hunter      = hunter,
                    LastPulse   = Time.time,
                    FadeEndTime = -1f,
                };

                MelonLogger.Msg(
                    $"[WotW] Dog taunt: '{dogGo.name}' pulling '{predator.gameObject.name}' " +
                    $"off '{hunter.gameObject.name}'.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterDogCombat.OnPredatorAggroedHunter: {ex.Message}");
            }
        }

        // ── Per-tick maintenance (called from HunterCombatPatches tick) ─────
        public static void UpdateActiveTaunts()
        {
            if (_activeTaunts.Count == 0) return;

            float now = Time.time;
            float pulse = Mathf.Max(0.25f, WardenOfTheWildsMod.HunterDogTauntPulseSeconds.Value);
            float fade  = Mathf.Max(0f, WardenOfTheWildsMod.HunterDogTauntFadeSeconds.Value);

            _expiredScratch.Clear();
            foreach (var kv in _activeTaunts)
            {
                var e = kv.Value;

                // Validity: predator + dog must still exist.
                if (e.Predator == null || e.Dog == null)
                { _expiredScratch.Add(kv.Key); continue; }

                var dogDmg = e.Dog.GetComponent<DamageableComponent>();
                if (dogDmg == null || dogDmg.lifePercentage <= 0f)
                { _expiredScratch.Add(kv.Key); continue; } // dog dead → release now

                // Predator driven off (fleeing) → job done, release so the dog
                // stops chasing it across the map and returns to guarding.
                if (IsRetreating(e.Predator))
                { _expiredScratch.Add(kv.Key); continue; }

                // Determine flee triggers if still tanking.
                if (e.FadeEndTime < 0f)
                {
                    bool dogLow  = dogDmg.lifePercentage
                                   <= WardenOfTheWildsMod.DogFleeHealthPct.Value;
                    bool dogFlee = IsRetreating(e.Dog);
                    bool hunterFlee = e.Hunter != null && IsRetreating(e.Hunter);

                    if (dogLow || dogFlee || hunterFlee)
                    {
                        e.FadeEndTime = now + fade;
                        // If it's the hunter bugging out, actively send the dog
                        // with him so the predator chases the fleeing decoy.
                        if (hunterFlee && WardenOfTheWildsMod.DogAutoFleeWithHunter.Value)
                            CommandDogToFlee(e.Dog, e.Predator, e.Hunter);
                    }
                }

                // Fade complete → release (predator reverts to vanilla).
                if (e.FadeEndTime >= 0f && now >= e.FadeEndTime)
                { _expiredScratch.Add(kv.Key); continue; }

                // Re-assert the forced target on the pulse cadence.
                if (now - e.LastPulse >= pulse)
                {
                    if (PulsePredatorOntoDog(e.Predator, e.Dog))
                        e.LastPulse = now;
                    else
                        _expiredScratch.Add(kv.Key); // predator combat gone
                }
            }

            for (int i = 0; i < _expiredScratch.Count; i++)
            {
                int key = _expiredScratch[i];
                if (_activeTaunts.TryGetValue(key, out var dead))
                    ReleaseTaunt(dead);
                _activeTaunts.Remove(key);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────
        /// <summary>
        /// No-scan hunter→dog lookup via the native defenders list. Returns the
        /// first living Dog defending this hunter, or null.
        /// </summary>
        private static GameObject FindDefendingDog(Component hunter)
        {
            try
            {
                var villager = hunter as Villager ?? hunter.GetComponent<Villager>();
                if (villager == null) return null;
                if (!(villager.occupation is VillagerOccupationHunter occ)) return null;

                var defenders = occ.defenders;
                if (defenders == null) return null;

                for (int i = 0; i < defenders.Count; i++)
                {
                    var d = defenders[i];
                    if (d == null) continue;
                    var go = d.gameObject;
                    if (go == null) continue;
                    if (go.GetComponent("Dog") == null) continue;
                    return go;
                }
            }
            catch { }
            return null;
        }

        // Threat value pumped onto the dog so it dominates the predator's
        // target-scoring query. The animal's score is distance + state +
        // threat; a value this large guarantees the dog outscores the hunter
        // regardless of distance or the hunter's accumulated damage-threat.
        private const float TauntThreat = 1_000_000f;

        /// <summary>
        /// Drives the predator onto the dog. The PRIMARY lever is the threat
        /// table: aggressive animals choose targets via an autonomous combat
        /// query (score = distance + state + threat) and overwrite any forced
        /// focus-target on their next query tick — so a bare SetTarget doesn't
        /// stick. By pumping the dog's threat far above everything else, the
        /// animal's OWN query picks the dog. We also nudge focus-targets both
        /// ways so the switch happens immediately and the dog actively fights
        /// back rather than just soaking hits. Returns false if the predator
        /// has no usable combat component (taunt should release).
        /// </summary>
        private static bool PulsePredatorOntoDog(Component predator, GameObject dogGo)
        {
            try
            {
                var predCombat = predator.GetComponent<CombatComponent>();
                if (predCombat == null) return false;

                var dogDmg = dogGo.GetComponent<IDamageable>();
                var predDmgComp = predator.GetComponent<DamageableComponent>();
                if (dogDmg == null) return false;

                // PRIMARY: make the dog the highest-threat target in the
                // predator's scoring table.
                predDmgComp?.SetThreat(dogGo, TauntThreat);

                // Immediate nudge so the switch lands this frame.
                predCombat.SetTarget(
                    newTarget: dogDmg,
                    newTargetCombatAction: CombatAction.Attack,
                    newTargetSourceIdentifier: TargetSourceIdentifier.Search,
                    targetIsInputSpecified: true);

                // Push the dog to actively attack the predator (not just tank):
                // pump the predator into the dog's threat table + nudge target.
                var dogCombat = dogGo.GetComponent<CombatComponent>();
                var dogDmgComp = dogGo.GetComponent<DamageableComponent>();
                var predDmg = predator.GetComponent<IDamageable>();
                // Pump the predator into the dog's threat table so the dog's OWN
                // combat query picks it (the dog is task/query-driven; direct
                // nav/target commands get overridden by its AI — vanilla has no
                // manual dog control). This works once the predator is within
                // the dog's perception; keeping the dog fast enough to stay near
                // the hunter (DogSpeedMult) is what closes the distance gap.
                if (dogCombat != null && predDmg != null)
                {
                    dogDmgComp?.SetThreat(predator.gameObject, TauntThreat);
                    dogCombat.SetTarget(
                        newTarget: predDmg,
                        newTargetCombatAction: CombatAction.Attack,
                        newTargetSourceIdentifier: TargetSourceIdentifier.Search,
                        targetIsInputSpecified: true);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Clears the inflated threat values when a taunt ends so the predator
        /// reverts to normal target scoring (and the dog stops fixating on it).
        /// </summary>
        private static void ReleaseTaunt(TauntEntry e)
        {
            try
            {
                if (e == null) return;
                if (e.Predator != null && e.Dog != null)
                    e.Predator.GetComponent<DamageableComponent>()?.SetThreat(e.Dog, 0f);
                if (e.Dog != null && e.Predator != null)
                    e.Dog.GetComponent<DamageableComponent>()?.SetThreat(e.Predator.gameObject, 0f);
            }
            catch { }
        }

        /// <summary>Danger ranking for taunt-swap priority: Bear > Wolf > Boar.
        /// Anything else (deer/fox/groundhog) ranks 0 and is never a taunt
        /// target anyway (gated by IsDangerousAnimal at the call sites).</summary>
        private static int DangerRank(Component animal)
        {
            if (animal == null) return 0;
            Type t = animal.GetType();
            while (t != null && t.Name != "MonoBehaviour" && t.Name != "Component")
            {
                if (t.Name == "Bear") return 3;
                if (t.Name == "Wolf") return 2;
                if (t.Name == "Boar") return 1;
                t = t.BaseType;
            }
            return 0;
        }

        /// <summary>True if the entity's combat component is in a retreat state.</summary>
        private static bool IsRetreating(Component c)
        {
            try
            {
                var combat = c.GetComponent<CombatComponent>();
                return combat != null && combat.isRetreating;
            }
            catch { return false; }
        }

        private static bool IsRetreating(GameObject go)
        {
            try
            {
                var combat = go.GetComponent<CombatComponent>();
                return combat != null && combat.isRetreating;
            }
            catch { return false; }
        }

        /// <summary>Disengage the dog and send it toward the hunter (away from
        /// the predator) so it flees as a decoy alongside the retreating hunter.</summary>
        private static void CommandDogToFlee(GameObject dogGo, Component predator, Component hunter)
        {
            try
            {
                var dogCombat = dogGo.GetComponent<CombatComponent>();
                dogCombat?.ClearAllTargetData();

                // Flee point: behind the hunter relative to the predator.
                Vector3 hPos = hunter.transform.position;
                Vector3 away = (hPos - predator.transform.position);
                away = away.sqrMagnitude > 0.01f ? away.normalized : hunter.transform.forward;
                Vector3 fleePoint = hPos + away * 6f;

                var agent = dogGo.GetComponent<AICrateNavMeshAgent>();
                if (agent != null)
                    agent.SetDestination(fleePoint);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HunterDogCombat.CommandDogToFlee: {ex.Message}");
            }
        }
    }
}
