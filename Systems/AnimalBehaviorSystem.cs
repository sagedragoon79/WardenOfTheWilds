using MelonLoader;
using UnityEngine;
using System;
using System.Reflection;
using System.Collections.Generic;

// ─────────────────────────────────────────────────────────────────────────────
//  AnimalBehaviorSystem
//  Per-species behavior profiles and runtime phase detection.
//
//  CONFIRMED BEHAVIOR (from player observation):
//    Deer        — always flees; 2 arrows to kill early game; low danger
//    Boar        — healthy: FLEES from hunter
//                  wounded (below threshold ~50%?): turns and FIGHTS BACK
//                  !! Do NOT chase a fleeing boar — it will reverse on you !!
//                  Correct hunter tactic: hold at Hunting Stand, let it close
//    Wolf        — always AGGRESSIVE; never flees; straight charge
//                  Correct hunter tactic: kite backwards, shoot while retreating
//    Bear        — healthy (>~25%): CHARGES
//                  low health (<~25%): tries to FLEE
//                  Correct hunter tactic: kite while charging, chase when fleeing
//    Fox         — CONFIRMED AggressiveAnimal (not PassiveAnimal) from 26-4-18 dump
//                  Has killCount field and foxWanderRadius; predator near ChickenCoops
//                  Fights back when healthy, flees when wounded
//                  TrapperLodge handles via traps — HuntingLodge ignores (low priority)
//    Groundhog   — unknown; likely pure FLEE (pest, crop-raider)
//                  Has cropsEaten counter — confirmed crop damage mechanic
//                  TrapperLodge handles via traps — hunter should ignore
//
//  WHAT WE NEED FROM THE DECOMPILE:
//    • Animal state machine class/enum — look for AnimalState, WildAnimalState,
//      BehaviorState, AnimalBehavior with values like Idle/Flee/Charge/Fight
//    • Health threshold fields on each animal type:
//        boar:  fightBackHealthThreshold / aggroHealthPercent / chargeHealthPercent
//        bear:  fleeHealthThreshold / panicHealthPercent / retreatHealthPercent
//    • Current state getter:
//        PassiveAnimal.currentState / GetBehaviorState() / behaviorPhase
//    • On-hit/on-damaged event:
//        OnTakeDamage(float amount) / OnHit() / OnDamageTaken()
//        — this is where state transitions happen (flee→fight for boar, charge→flee for bear)
//    • Aggro target:
//        aggroTarget / attackTarget / currentEnemy — what the animal is pursuing
//
//  HOW THE KITING SYSTEM USES THIS:
//    Every 8 seconds (AttractionWatcher tick) AND on-hit (once we have that hook),
//    the kiting logic queries AnimalBehaviorSystem.GetPhase(animal) and tells the
//    hunter assigned to that stand what to do:
//
//    Boar  FLEEING   → hunter HOLDS at stand (wait for it to turn)
//    Boar  FIGHTING  → hunter RETREATS to stand edge, fires from max range
//    Wolf  CHARGING  → hunter RETREATS (kite backward toward cabin)
//    Bear  CHARGING  → hunter HOLDS at stand, fires rapidly (2 hunters = suppression)
//    Bear  FLEEING   → hunter ADVANCES for kill shots (safe to close in now)
//    Deer  FLEEING   → hunter CHASES normally (vanilla behavior is fine)
//    Fox   *         → hunter IGNORES — TrapperLodge handles this
//    Groundhog *     → hunter IGNORES — TrapperLodge handles this
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Systems
{
    // ── Animal behavior phase (what the animal is currently doing) ────────────
    public enum AnimalPhase
    {
        Unknown,    // State couldn't be read (fallback to vanilla behavior)
        Idle,       // Wandering, not engaged
        Fleeing,    // Running away from threat
        Charging,   // Actively closing on target to attack
        FightingBack, // Wounded boar — turned and now attacking
        Dead,
    }

    // ── Hunter response strategy for each phase ───────────────────────────────
    public enum HunterResponse
    {
        VanillaAI,   // Let vanilla handle it (deer, unknown animals)
        HoldAtStand, // Stay at Hunting Stand position, fire from there
        RetreatToStand, // Back up toward stand while firing
        KiteBackward,   // Keep moving away from animal, max range shots
        AdvanceForKill, // Animal is fleeing/wounded — safe to close in
        Ignore,         // Do not engage (fox, groundhog — trapper's job)
    }

    // ── Per-species static profile ────────────────────────────────────────────
    public class AnimalProfile
    {
        public string   ClassName;           // Game class name (confirmed or candidate)
        public bool     IsDangerous;         // Can kill a hunter
        public bool     TrapperTarget;       // Trapper Lodge specialises in this
        public bool     HunterTarget;        // Hunting Lodge should pursue this
        public float    MegaYield;           // 1.0 = normal; >1.0 = BearMegaYieldMult applies
        public float    EstimatedFleeThreshold;   // HP% at which it flees (0 = never flees)
        public float    EstimatedFightThreshold;  // HP% at which it turns to fight (0 = always flees)

        // Field/property name candidates for the health threshold in the game assembly
        public string[] HealthThresholdCandidates;
        // Field/property name candidates for the current behavior state
        public string[] StateCandidates;

        public AnimalProfile(
            string className, bool isDangerous, bool trapperTarget, bool hunterTarget,
            float megaYield, float fleeThreshold, float fightThreshold,
            string[] healthThresholdCandidates, string[] stateCandidates)
        {
            ClassName                  = className;
            IsDangerous                = isDangerous;
            TrapperTarget              = trapperTarget;
            HunterTarget               = hunterTarget;
            MegaYield                  = megaYield;
            EstimatedFleeThreshold     = fleeThreshold;
            EstimatedFightThreshold    = fightThreshold;
            HealthThresholdCandidates  = healthThresholdCandidates;
            StateCandidates            = stateCandidates;
        }

        /// <summary>
        /// Determine the correct hunter response given the animal's current phase.
        /// This is the core of the phase-aware kiting system.
        /// </summary>
        public HunterResponse GetResponse(AnimalPhase phase)
        {
            if (!HunterTarget) return HunterResponse.Ignore;

            // Deer — vanilla behavior is fine, hunter chases and shoots
            if (ClassName == "Deer") return HunterResponse.VanillaAI;

            return phase switch
            {
                AnimalPhase.Fleeing => ClassName == "Boar"
                    ? HunterResponse.HoldAtStand    // Boar will turn and fight — don't chase!
                    : HunterResponse.AdvanceForKill, // Bear/wolf fleeing = safe to close in

                AnimalPhase.Charging =>
                    HunterResponse.KiteBackward,    // Always back up from a charging animal

                AnimalPhase.FightingBack =>
                    HunterResponse.RetreatToStand,  // Boar turned to fight — create distance

                AnimalPhase.Idle =>
                    HunterResponse.HoldAtStand,     // Wait for it to notice the hunter

                AnimalPhase.Dead =>
                    HunterResponse.VanillaAI,       // Let the hunter collect normally

                _ => HunterResponse.HoldAtStand,    // Unknown — safe default
            };
        }
    }

    public static class AnimalBehaviorSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Species registry ──────────────────────────────────────────────────
        // Populated with observed behavior and confirmed class names.
        // Health thresholds are estimates until confirmed from decompile.
        public static readonly Dictionary<string, AnimalProfile> Profiles =
            new Dictionary<string, AnimalProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Deer"] = new AnimalProfile(
                className:              "Deer",
                isDangerous:            false,
                trapperTarget:          false,
                hunterTarget:           true,
                megaYield:              1.0f,
                fleeThreshold:          1.0f,   // Flees at full health (always flees)
                fightThreshold:         0.0f,   // Never fights back
                healthThresholdCandidates: Array.Empty<string>(),
                stateCandidates: new[] {
                    "currentState", "behaviorState", "state",
                    "fleeState", "idleState",
                }),

            ["Boar"] = new AnimalProfile(
                className:              "Boar",
                isDangerous:            true,
                trapperTarget:          false,
                hunterTarget:           true,
                megaYield:              1.0f,  // Good tallow — multiplied in Smokehouse
                fleeThreshold:          1.0f,  // Flees when healthy
                fightThreshold:         0.5f,  // Estimated: turns to fight at ~50% HP
                healthThresholdCandidates: new[] {
                    "fightBackHealthThreshold", "aggroHealthPercent",
                    "chargeHealthPercent", "fightHealthThreshold",
                    "counterAttackThreshold", "enrageThreshold",
                },
                stateCandidates: new[] {
                    "currentState", "behaviorState", "state",
                    "isEnraged", "isFighting", "isCharging", "isFleeing",
                }),

            ["Wolf"] = new AnimalProfile(
                className:              "Wolf",
                isDangerous:            true,
                trapperTarget:          false,
                hunterTarget:           true,
                megaYield:              1.0f,
                fleeThreshold:          0.0f,  // Never flees — always aggressive
                fightThreshold:         0.0f,
                healthThresholdCandidates: new[] {
                    "fleeHealthThreshold", "retreatHealthPercent",
                    "panicThreshold",
                },
                stateCandidates: new[] {
                    "currentState", "behaviorState", "state",
                    "isCharging", "isAttacking", "aggroTarget",
                }),

            ["Bear"] = new AnimalProfile(
                // NOTE: No BearCarcass ItemID — HealthyCarcass (57) used as proxy
                className:              "Bear",
                isDangerous:            true,
                trapperTarget:          false,
                hunterTarget:           true,
                megaYield:              1.0f,   // BearMegaYieldMult pref applied on top
                fleeThreshold:          0.25f,  // Observed: flees at ~25% HP
                fightThreshold:         0.0f,   // Doesn't "turn to fight" — just flees when low
                healthThresholdCandidates: new[] {
                    "fleeHealthThreshold", "retreatHealthPercent",
                    "panicHealthPercent", "fleePercent",
                    "panicThreshold", "retreatThreshold",
                },
                stateCandidates: new[] {
                    "currentState", "behaviorState", "state",
                    "isCharging", "isFleeing", "isPanicked",
                }),

            ["Fox"] = new AnimalProfile(
                // CONFIRMED: Fox extends AggressiveAnimal (not PassiveAnimal) —
                // from 26-4-18 dump. Fox CAN fight back; it has killCount and
                // foxWanderRadius fields, and is an active predator near ChickenCoops.
                // IsDangerous = true reflects that it can deal damage if cornered.
                // Still primarily a TrapperLodge target — HuntingLodge won't kite it
                // (low health = minimal threat to an experienced hunter).
                className:              "Fox",
                isDangerous:            true,   // AggressiveAnimal — can fight back
                trapperTarget:          true,   // TrapperLodge specialises in fox
                hunterTarget:           false,  // Hunting Lodge ignores fox — trapper's job
                megaYield:              1.0f,
                fleeThreshold:          0.5f,  // Estimated: flees when wounded
                fightThreshold:         1.0f,  // Fights when healthy (AggressiveAnimal baseline)
                healthThresholdCandidates: new[] {
                    "fleeHealthThreshold", "retreatHealthPercent", "panicThreshold",
                },
                stateCandidates: new[] {
                    "currentState", "behaviorState", "state",
                    "isCharging", "isFleeing", "isAttacking",
                }),

            ["Groundhog"] = new AnimalProfile(
                className:              "Groundhog",
                isDangerous:            false,
                trapperTarget:          true,   // TrapperLodge specialises in groundhog
                hunterTarget:           false,  // Hunting Lodge ignores — trapper's job
                megaYield:              1.0f,
                fleeThreshold:          1.0f,  // Likely always flees
                fightThreshold:         0.0f,
                healthThresholdCandidates: Array.Empty<string>(),
                stateCandidates: new[] { "currentState", "behaviorState", "isFleeing" }),
        };

        // ── Runtime phase detection ───────────────────────────────────────────
        /// <summary>
        /// Reads the current behavior phase of an animal instance via reflection.
        /// Returns AnimalPhase.Unknown if the state field/property can't be read
        /// (fallback to vanilla hunter behavior).
        /// </summary>
        public static AnimalPhase GetPhase(Component animal)
        {
            if (animal == null) return AnimalPhase.Unknown;

            string typeName = animal.GetType().Name;
            if (!Profiles.TryGetValue(typeName, out AnimalProfile? profile))
                return AnimalPhase.Unknown;

            try
            {
                // Try to read current HP percentage
                float hpPercent = GetHealthPercent(animal);

                if (hpPercent <= 0f) return AnimalPhase.Dead;

                // Bear: charge until flee threshold, then run
                if (typeName == "Bear")
                {
                    return hpPercent <= profile.EstimatedFleeThreshold
                        ? AnimalPhase.Fleeing
                        : AnimalPhase.Charging;
                }

                // Boar: flee until fight threshold, then turn and fight
                if (typeName == "Boar")
                {
                    if (hpPercent <= profile.EstimatedFightThreshold)
                        return AnimalPhase.FightingBack;

                    // Also check if it's currently fleeing vs charging via state field
                    return GetStateFieldPhase(animal, profile) ?? AnimalPhase.Fleeing;
                }

                // Wolf: always charging
                if (typeName == "Wolf") return AnimalPhase.Charging;

                // Fox, Groundhog: always fleeing
                if (typeName == "Fox" || typeName == "Groundhog") return AnimalPhase.Fleeing;

                // Deer: always fleeing (from hunter perspective)
                if (typeName == "Deer") return AnimalPhase.Fleeing;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] AnimalBehaviorSystem.GetPhase ({typeName}): {ex.Message}");
            }

            return AnimalPhase.Unknown;
        }

        // ── Health percentage helper ──────────────────────────────────────────
        // Candidate field/property names sourced from common game patterns.
        // Will be narrowed once combat dump is analysed.
        private static readonly string[] HpFieldCandidates = {
            "currentHealth", "health", "hp", "hitPoints",
            "currentHp", "currentHitPoints",
            "healthPoints", "lifePoints",
        };
        private static readonly string[] MaxHpFieldCandidates = {
            "maxHealth", "maxHp", "maxHitPoints",
            "totalHealth", "baseHealth", "fullHealth",
        };

        public static float GetHealthPercent(Component animal)
        {
            var type = animal.GetType();

            float current = -1f, max = -1f;

            foreach (string name in HpFieldCandidates)
            {
                object? val = type.GetField(name, AllInstance)?.GetValue(animal)
                           ?? type.GetProperty(name, AllInstance)?.GetValue(animal);
                if (val == null) continue;
                current = Convert.ToSingle(val);
                break;
            }

            foreach (string name in MaxHpFieldCandidates)
            {
                object? val = type.GetField(name, AllInstance)?.GetValue(animal)
                           ?? type.GetProperty(name, AllInstance)?.GetValue(animal);
                if (val == null) continue;
                max = Convert.ToSingle(val);
                break;
            }

            if (current < 0f || max <= 0f) return -1f; // Couldn't read
            return current / max;
        }

        // ── State field phase reader ──────────────────────────────────────────
        // For animals with an explicit state enum/field, read it directly.
        // Returns null if the state field couldn't be read or parsed.
        private static AnimalPhase? GetStateFieldPhase(Component animal, AnimalProfile profile)
        {
            var type = animal.GetType();

            foreach (string name in profile.StateCandidates)
            {
                object? val = type.GetField(name, AllInstance)?.GetValue(animal)
                           ?? type.GetProperty(name, AllInstance)?.GetValue(animal);
                if (val == null) continue;

                // Bool fields (isFleeing, isCharging, etc.)
                if (val is bool b)
                {
                    if (name.Contains("leeI") || name.Contains("Flee")) // isFleeing
                        return b ? AnimalPhase.Fleeing : null;
                    if (name.Contains("harg") || name.Contains("Charg")) // isCharging
                        return b ? AnimalPhase.Charging : null;
                    if (name.Contains("ight") || name.Contains("Fight")) // isFighting
                        return b ? AnimalPhase.FightingBack : null;
                    continue;
                }

                // Enum or int state — convert to string and pattern-match
                string stateStr = val.ToString() ?? "";
                if (stateStr.IndexOf("flee", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stateStr.IndexOf("run",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stateStr.IndexOf("panic",StringComparison.OrdinalIgnoreCase) >= 0)
                    return AnimalPhase.Fleeing;

                if (stateStr.IndexOf("charge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stateStr.IndexOf("attack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stateStr.IndexOf("aggro",  StringComparison.OrdinalIgnoreCase) >= 0)
                    return AnimalPhase.Charging;

                if (stateStr.IndexOf("fight",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stateStr.IndexOf("enrage", StringComparison.OrdinalIgnoreCase) >= 0)
                    return AnimalPhase.FightingBack;

                if (stateStr.IndexOf("idle",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stateStr.IndexOf("wander",StringComparison.OrdinalIgnoreCase) >= 0)
                    return AnimalPhase.Idle;
            }
            return null;
        }

        // ── Lookup helpers ────────────────────────────────────────────────────
        public static AnimalProfile? GetProfile(Component animal) =>
            Profiles.TryGetValue(animal.GetType().Name, out AnimalProfile? p) ? p : null;

        public static bool IsHunterTarget(Component animal) =>
            GetProfile(animal)?.HunterTarget ?? false;

        public static bool IsTrapperTarget(Component animal) =>
            GetProfile(animal)?.TrapperTarget ?? false;

        public static bool IsDangerous(Component animal) =>
            GetProfile(animal)?.IsDangerous ?? false;

        /// <summary>
        /// Returns the BearMegaYieldMult preference if the animal is a bear
        /// (HealthyCarcass proxy), otherwise 1.0.
        /// Used by Smokehouse tallow bonus calculations.
        /// </summary>
        public static float GetMegaYieldMult(string animalTypeName)
        {
            if (string.Equals(animalTypeName, "Bear", StringComparison.OrdinalIgnoreCase))
                return WardenOfTheWildsMod.BearMegaYieldMult.Value;
            return 1.0f;
        }

        // ── Animal movement speed reader ──────────────────────────────────────
        // Needed to compare against hunter speed — determines whether the hunter
        // can outrun the animal or needs the Hunting Stand as a hard backstop.
        private static readonly string[] AnimalSpeedCandidates = {
            "moveSpeed", "movementSpeed", "speed", "runSpeed",
            "chaseSpeed", "chargeSpeed", "walkSpeed",
            // NavMeshAgent wrapper (common Unity pattern)
            "navMeshSpeed", "agentSpeed",
        };

        /// <summary>
        /// Reads the movement speed of an animal instance.
        /// Returns -1 if the field can't be found (caller falls back to estimates).
        /// </summary>
        public static float GetAnimalSpeed(Component animal)
        {
            var type = animal.GetType();
            foreach (string name in AnimalSpeedCandidates)
            {
                object? val = type.GetField(name, AllInstance)?.GetValue(animal)
                           ?? type.GetProperty(name, AllInstance)?.GetValue(animal);
                if (val == null) continue;
                try { return Convert.ToSingle(val); } catch { }
            }

            // Try NavMeshAgent.speed via the component
            try
            {
                var agent = animal.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) return agent.speed;
            }
            catch { }

            return -1f;
        }

        // ── Kiting calculator ─────────────────────────────────────────────────
        /// <summary>
        /// Calculates optimal kiting parameters for a HuntingLodge hunter
        /// engaging a specific animal, given current hunter stats.
        ///
        /// FORMULA:
        ///   retreatDistance = reloadTime × hunterSpeed
        ///   — This is how far the hunter should move between each shot.
        ///   — Moving exactly this far per cycle keeps the animal at constant
        ///     range while the hunter is always reloading during movement
        ///     (zero idle time, zero standing-still vulnerability).
        ///
        ///   safeLeadDistance = animalSpeed × flightTime
        ///   — How far ahead of the animal the hunter needs to be to stay safe.
        ///   — flightTime = approxRange / projectileSpeed (arrow travel time)
        ///
        ///   canOutrunAnimal = hunterSpeed > animalSpeed
        ///   — If true: hunter kites indefinitely.
        ///   — If false: hunter MUST use the Hunting Stand as a backstop.
        ///     The stand absorbs the closing distance the hunter can't escape.
        ///
        /// ALL values are -1 until confirmed from the combat dump.
        /// The calculator degrades gracefully — any unknown value falls back
        /// to the configured preference defaults.
        /// </summary>
        public static class KitingCalculator
        {
            // ── Confirmed field names (fill in from dump) ─────────────────────
            // Hunter reload / shoot cooldown
            public static readonly string[] ReloadFieldCandidates = {
                "shootCooldown", "attackCooldown", "reloadTime",
                "fireCooldown", "shootInterval", "attackInterval",
                "bowCooldown", "crossbowCooldown",
                // The property we already know from HunterBuilding
                "trappingCarcassSpawnInterval",  // NOT this one — trap-specific
            };

            // Hunter movement speed
            public static readonly string[] HunterSpeedCandidates = {
                "moveSpeed", "movementSpeed", "speed", "walkSpeed",
                "runSpeed", "offRoadSpeed", "terrainSpeed",
                // Tech tree multiplier — applied on top of base speed
                "speedMultiplier", "offRoadSpeedMultiplier", "terrainSpeedBonus",
                "hunterSpeedBonus",
            };

            // Arrow/projectile speed — used to estimate lead distance
            public static readonly string[] ProjectileSpeedCandidates = {
                "projectileSpeed", "arrowSpeed", "boltSpeed",
                "missileSpeed", "shotSpeed",
            };

            // Approximate bow/crossbow range (world units) — hunter won't shoot beyond this
            public static readonly string[] ShootRangeCandidates = {
                "shootRange", "attackRange", "bowRange",
                "maxRange", "engageRange", "detectionRange",
            };

            // ── Known estimated values (replaced once dump confirms actuals) ──
            // All in world units or seconds. Used as fallbacks when reflection fails.
            // BowReloadSeconds / CrossbowReloadSeconds preferences override these at runtime.
            public const float FallbackReloadSeconds  = 3.0f;   // Bow estimate (pending dump)
            // NOTE: FastVillagers mod (VillagerSpeed = 2) doubles all villager movement.
            // Our SetWorkerSpeed() reads the live value so it stacks correctly.
            // These fallbacks are vanilla estimates — actual values come from the dump.
            // With FastVillagers active, effective vanilla speed is ~2× these numbers.
            public const float FallbackHunterSpeed    = 4.5f;   // Vanilla estimate (~9 with FV×2)
            // huntingRadius confirmed = 100.0 world units from log.
            // Bow range is a fraction of that — hunter engages within ~20% of work radius.
            public const float FallbackBowRange       = 20.0f;  // Estimate ~20u (100u / 5)
            public const float FallbackCrossbowRange  = 28.0f;  // Hunting Lodge upgrade estimate
            public const float FallbackProjectileSpd  = 20.0f;  // Arrow flight speed estimate
            // Confirmed: T2 trappingCarcassSpawnInterval = 26 days (from log)
            // TrapperLodge ×1.6 pelt mult → 26 / 1.6 = 16 days
            public const int   ConfirmedTrapIntervalDays = 26;

            // ── FastVillagers compat ──────────────────────────────────────────
            // Detects whether FastVillagers is loaded and reads its configured
            // speed multiplier so we can factor it into kiting estimates.
            private static float _fastVillagersSpeedMult = -1f;

            public static float GetEffectiveHunterSpeed(float rawReadSpeed)
            {
                // If we successfully read the speed from the game (rawReadSpeed > 0),
                // it already includes FastVillagers' modification — use it directly.
                if (rawReadSpeed > 0f) return rawReadSpeed;

                // Fallback: try to read FastVillagers config to scale our estimate.
                if (_fastVillagersSpeedMult < 0f)
                    _fastVillagersSpeedMult = ReadFastVillagersSpeedMult();

                float mult = _fastVillagersSpeedMult > 0f ? _fastVillagersSpeedMult : 1f;
                return FallbackHunterSpeed * mult;
            }

            // ── Weapon-aware reload time ──────────────────────────────────────
            // Tries to detect whether the hunter has a crossbow equipped by
            // reflecting on known weapon/policy field candidates. Falls back to
            // the configured bow or crossbow reload preference, then to the
            // FallbackReloadSeconds constant.
            //
            // Called by ApplyPostShotRetreat and DangerProximityWatcher so that
            // crossbow hunters back up further per shot (longer reload window).
            private static readonly string[] CrossbowDetectCandidates = {
                "hasCrossbow", "crossbowEquipped", "useCrossbow",
                "weaponType", "currentWeapon", "equippedWeapon",
                "attackWeapon", "rangedWeapon",
            };

            public static float GetEffectiveReloadSeconds(UnityEngine.Component hunter)
            {
                // Try to detect crossbow from the hunter component
                if (hunter != null)
                {
                    try
                    {
                        System.Type? t = hunter.GetType();
                        while (t != null)
                        {
                            foreach (string candidate in CrossbowDetectCandidates)
                            {
                                // Check bool fields first (hasCrossbow, crossbowEquipped)
                                var fi = t.GetField(candidate,
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic |
                                    System.Reflection.BindingFlags.Instance);
                                if (fi != null)
                                {
                                    object? val = fi.GetValue(hunter);
                                    if (val is bool b && b)
                                        return WardenOfTheWildsMod.CrossbowReloadSeconds.Value;
                                    // String/enum check: "Crossbow", "crossbow"
                                    if (val != null && val.ToString()
                                        .IndexOf("crossbow", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                        return WardenOfTheWildsMod.CrossbowReloadSeconds.Value;
                                }
                            }
                            t = t.BaseType;
                            if (t?.Name == "MonoBehaviour" || t?.Name == "Object") break;
                        }
                    }
                    catch { }
                }

                // No crossbow detected — use bow reload time
                return WardenOfTheWildsMod.BowReloadSeconds.Value;
            }

            private static float ReadFastVillagersSpeedMult()
            {
                try
                {
                    // FastVillagers writes to MelonPreferences under [FastVillagersConfig]
                    // VillagerSpeed entry — confirmed from log: VillagerSpeed = 2
                    var cat = MelonLoader.MelonPreferences.GetCategory("FastVillagersConfig");
                    if (cat == null) return 1f;

                    var entry = cat.GetEntry("VillagerSpeed");
                    if (entry == null) return 1f;

                    return System.Convert.ToSingle(entry.BoxedValue);
                }
                catch { return 1f; }
            }

            // Known animal speeds (estimates — replaced from dump).
            // IMPORTANT: Animals are NOT affected by the FastVillagers mod (confirmed).
            // FastVillagers only boosts villager/worker movement, not wildlife.
            // This means with FastVillagers ×2, hunters outrun every animal listed here.
            // Vanilla hunter (~4.5 u/s) may struggle vs wolf; FV hunter (~9 u/s) does not.
            public static readonly Dictionary<string, float> EstimatedAnimalSpeeds =
                new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["Deer"]       = 5.5f,  // Fast flee — hard for vanilla hunter, easy with FV
                ["Boar"]       = 4.0f,  // Medium charge speed; vanilla hunter can just outrun
                ["Wolf"]       = 6.0f,  // Fastest — vanilla hunter cannot outrun; FV hunter can
                ["Bear"]       = 3.5f,  // Slow but dangerous; both vanilla and FV hunters outrun
                ["Fox"]        = 5.0f,  // Quick flee; trapper target, not relevant to kiting
                ["Groundhog"]  = 3.0f,  // Slow burrowing pest; trapper target
            };

            /// <summary>
            /// Computes the optimal retreat distance per shot cycle for the given
            /// hunter stats and animal type.
            ///
            /// retreatDistance = reloadTime × hunterSpeed
            ///
            /// The kiting system moves the hunter this far toward the Hunting Stand
            /// (or cabin) immediately after each shot fires, so that by the time
            /// the bow is ready again the hunter has put maximum distance between
            /// themselves and the animal — without wasting any reload time standing still.
            /// </summary>
            public static float OptimalRetreatDistance(
                float reloadSeconds, float hunterSpeedUnitsPerSec)
            {
                float reload = reloadSeconds > 0f ? reloadSeconds : FallbackReloadSeconds;
                float speed  = hunterSpeedUnitsPerSec > 0f
                    ? hunterSpeedUnitsPerSec
                    : FallbackHunterSpeed;
                return reload * speed;
            }

            /// <summary>
            /// Returns true if the hunter can outrun this animal type.
            /// If false, the Hunting Stand is a required backstop — the hunter
            /// cannot kite indefinitely in open terrain.
            /// </summary>
            public static bool CanOutrun(float hunterSpeed, string animalTypeName)
            {
                float animalSpd = EstimatedAnimalSpeeds.TryGetValue(
                    animalTypeName, out float s) ? s : 5f;
                float effective = hunterSpeed > 0f ? hunterSpeed : FallbackHunterSpeed;
                return effective > animalSpd;
            }

            /// <summary>
            /// Approximate world-unit distance a hunting arrow travels before
            /// hitting a stationary target at the given range.
            /// Used to compute the lead offset when the target is moving.
            ///
            /// leadOffset = animalSpeed × (range / projectileSpeed)
            ///
            /// The hunter should aim this many units AHEAD of the animal's
            /// current position in the direction of movement. Not directly
            /// actionable from our wander-point injection approach (we can't
            /// control shot aim), but useful as a tuning reference for
            /// confirming whether the game already does predictive aiming.
            /// </summary>
            public static float ArrowLeadOffset(
                float animalSpeed, float engageRange,
                float projectileSpeed = FallbackProjectileSpd)
            {
                float spd = projectileSpeed > 0f ? projectileSpeed : FallbackProjectileSpd;
                return animalSpeed * (engageRange / spd);
            }

            /// <summary>
            /// Produces a human-readable summary of the kiting parameters for
            /// a given animal type — logged on HuntingLodge path selection and
            /// shown in OnGUI when a stand is selected.
            /// </summary>
            public static string Summarize(
                string animalTypeName,
                float reloadSeconds, float hunterSpeed,
                float engageRange = FallbackBowRange)
            {
                float retreat = OptimalRetreatDistance(reloadSeconds, hunterSpeed);
                bool canOutrun = CanOutrun(hunterSpeed, animalTypeName);
                float lead = ArrowLeadOffset(
                    EstimatedAnimalSpeeds.TryGetValue(animalTypeName, out float s) ? s : 5f,
                    engageRange);

                return $"{animalTypeName}: retreat {retreat:F1}u/shot | " +
                       $"lead {lead:F1}u | " +
                       $"stand required: {(!canOutrun ? "YES" : "no")}";
            }
        }

        // ── Dump for decompile research ───────────────────────────────────────
        /// <summary>
        /// Logs all fields and methods on known animal types.
        /// Called from HunterCombatPatches.DumpCombatMethods() — no separate
        /// activation needed.
        /// </summary>
        public static void DumpAnimalFields()
        {
            string[] animalTypes = { "Deer", "Boar", "Wolf", "Bear", "Fox", "Groundhog",
                                     "PassiveAnimal", "AggressiveAnimal", "WildAnimal" };

            foreach (string typeName in animalTypes)
            {
                Type? t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(typeName);
                    if (t != null) break;
                }
                if (t == null)
                {
                    MelonLogger.Msg($"[WotW ANIMAL DUMP] {typeName}: NOT FOUND");
                    continue;
                }

                MelonLogger.Msg($"[WotW ANIMAL DUMP] ── {typeName} (base: {t.BaseType?.Name}) ──");

                // Fields — especially health, state, threshold values
                foreach (var f in t.GetFields(AllInstance |
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (f.DeclaringType != t) continue;
                    MelonLogger.Msg($"[WotW ANIMAL DUMP]   field  {f.FieldType.Name} {f.Name}");
                }

                // Methods — especially OnTakeDamage, state transitions
                foreach (var m in t.GetMethods(AllInstance |
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.DeclaringType != t) continue;
                    var parms = string.Join(", ", Array.ConvertAll(
                        m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                    MelonLogger.Msg($"[WotW ANIMAL DUMP]   method {m.ReturnType.Name} {m.Name}({parms})");
                }
            }
            MelonLogger.Msg("[WotW ANIMAL DUMP] Animal field dump complete.");

            // ── Speed / reload spotlight ──────────────────────────────────────
            // Specifically call out speed and reload fields from HunterBuilding
            // and villager types so we don't have to grep the full dump manually.
            MelonLogger.Msg("[WotW ANIMAL DUMP] ── SPEED / RELOAD SPOTLIGHT ──");
            string[] spotlightTypes = { "HunterBuilding", "Villager", "VillagerController",
                                        "Worker", "Boar", "Wolf", "Bear", "Deer" };
            string[] spotlightKeywords = {
                "speed", "reload", "cooldown", "interval", "range",
                "attack", "shoot", "fire", "bow", "arrow", "projectile",
                "offroad", "terrain", "multiplier",
            };

            foreach (string typeName in spotlightTypes)
            {
                Type? t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(typeName);
                    if (t != null) break;
                }
                if (t == null) continue;

                bool headerPrinted = false;
                foreach (var f in t.GetFields(
                    AllInstance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    bool match = false;
                    foreach (string kw in spotlightKeywords)
                        if (f.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        { match = true; break; }
                    if (!match) continue;

                    if (!headerPrinted)
                    {
                        MelonLogger.Msg($"[WotW ANIMAL DUMP]   {typeName}:");
                        headerPrinted = true;
                    }
                    MelonLogger.Msg($"[WotW ANIMAL DUMP]     {f.FieldType.Name} {f.Name}");
                }

                foreach (var p in t.GetProperties(
                    AllInstance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    bool match = false;
                    foreach (string kw in spotlightKeywords)
                        if (p.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        { match = true; break; }
                    if (!match) continue;

                    if (!headerPrinted)
                    {
                        MelonLogger.Msg($"[WotW ANIMAL DUMP]   {typeName}:");
                        headerPrinted = true;
                    }
                    MelonLogger.Msg($"[WotW ANIMAL DUMP]     prop {p.PropertyType.Name} {p.Name}");
                }
            }
            MelonLogger.Msg("[WotW ANIMAL DUMP] Speed/reload spotlight complete.");
        }
    }
}
