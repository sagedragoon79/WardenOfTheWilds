using MelonLoader;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;

// ─────────────────────────────────────────────────────────────────────────────
//  HuntingDogSystem
//  Companion dog bonuses for Trapper Lodge and Hunting Lodge.
//
//  VANILLA STATUS:
//    HunterBuilding already has GetPetSlotCount() and GetPetAssignees() methods
//    confirmed from the Assembly-CSharp.dll method dump. The pet slot system
//    exists on HunterBuilding — Crate Entertainment plans to add cats and dogs
//    as a future content update. This system pre-wires to those slots.
//
//  DOG ROLES BY PATH:
//
//    HuntingLodge Dog — Combat companion
//      The core kiting problem: hunters stand still while reloading and get
//      killed by bears/wolves closing the distance. A dog solves this organically:
//        • Dog charges and harasses the animal during the reload window
//        • Animal's aggro splits or fully transfers to the dog
//        • Hunter retreats to Hunting Stand/Blind while dog engages
//        • Hunter fires from safe elevated range; dog takes reduced damage
//        • Bear/wolf behavior: animal must choose dog or hunter — typically
//          goes for the closer threat (the dog), buying hunter time to kite
//      Additional bonuses:
//        • Search radius bonus — dog sniffs out game, increasing encounter rate
//        • Wounded tracking — dog follows a fleeing wounded bear/boar, keeping
//          it flagged so the hunter can pursue for the kill shot
//        • Pack bonus if two HuntingLodge hunters share a Hunting Stand zone:
//          two dogs = animal is occupied long enough for coordinated fire
//
//    TrapperLodge Dog — Trap line companion
//      • Patrol bonus — dog completes trap-line checks faster, reducing the
//        trapper's travel time between triggered traps
//      • Fox flushing — dog flushes foxes from dens toward active traps,
//        increasing trap trigger rate near ChickenCoop (compounds coop bonus)
//      • Groundhog alert — dog detects active groundhog burrows near Cropfields,
//        triggering a notification and directing the trapper to that zone first
//      • Tended Wilds synergy: dog alerts on pest activity near cultivated plots
//
//  IMPLEMENTATION PLAN (activate when dog content ships):
//    1. Hook GetPetAssignees() — when a dog is assigned to a HunterBuilding,
//       detect path (TrapperLodge / HuntingLodge) and register in this system
//    2. For HuntingLodge: inject dog as a decoy during the kiting tick
//       - When hunter fires a shot (post-shoot hook), set dog's target = the animal
//       - Dog moves toward animal; hunter's NavMeshAgent target = Hunting Stand
//       - Net result: hunter retreats, dog engages, animal chases dog
//    3. For TrapperLodge: boost trap-line check frequency proportional to
//       number of dogs assigned (each dog = one additional trap check per day)
//    4. Gold upkeep: dog fed from town food supply (same as villager food need)
//       OR small gold cost like guard tower villager — TBD from Crate's impl
//
//  CONFIRMED METHODS (from prior dump):
//    HunterBuilding.GetPetSlotCount()   — how many pet slots the building has
//    HunterBuilding.GetPetAssignees()   — returns currently assigned pets
//
//  UNCONFIRMED (needs dog content update + new dump):
//    ? Pet / Dog / AnimalCompanion class name
//    ? Dog.SetTarget(Component target) — for decoy/engage mechanic
//    ? Dog.health / maxHealth           — to track dog survival
//    ? HunterBuilding.AssignPet(Pet)    — how dogs are slotted in
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Systems
{
    public enum DogRole
    {
        None,
        HuntingCompanion,   // HuntingLodge — combat decoy + search bonus
        TrapLinePatrol,     // TrapperLodge — faster collection + fox flushing
    }

    public class DogAssignment
    {
        public Component    Dog             = null!;
        public Component    AssignedBuilding = null!;
        public DogRole      Role;
        public Vector3      CabinPosition;
        public float        LastDecoyTime;      // Last time dog was sent to intercept
        public float        LastPatrolTime;     // Last trap-line patrol tick
        public bool         IsEngaging;         // Currently harassing an animal
    }

    public static class HuntingDogSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ── Active dog registry ───────────────────────────────────────────────
        private static readonly Dictionary<int, DogAssignment> ActiveDogs =
            new Dictionary<int, DogAssignment>();

        public static void OnMapLoaded()
        {
            ActiveDogs.Clear();
            MelonLogger.Msg("[WotW] HuntingDogSystem: map loaded, dogs cleared.");
        }

        // ── Registration (called when pet assigned to HunterBuilding) ─────────
        public static void RegisterDog(Component dog, Component building,
                                       WardenOfTheWilds.Components.HunterT2Path path)
        {
            if (dog == null || building == null) return;

            int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(dog);
            var role = path == WardenOfTheWilds.Components.HunterT2Path.TrapperLodge
                ? DogRole.TrapLinePatrol
                : path == WardenOfTheWilds.Components.HunterT2Path.HuntingLodge
                    ? DogRole.HuntingCompanion
                    : DogRole.None;

            ActiveDogs[key] = new DogAssignment
            {
                Dog              = dog,
                AssignedBuilding = building,
                Role             = role,
                CabinPosition    = building.transform.position,
            };

            MelonLogger.Msg(
                $"[WotW] Dog registered: '{dog.gameObject.name}' → {role} " +
                $"at '{building.gameObject.name}'");
        }

        public static void UnregisterDog(Component dog)
        {
            if (dog == null) return;
            int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(dog);
            ActiveDogs.Remove(key);
        }

        // ── Combat decoy tick (HuntingLodge) ──────────────────────────────────
        /// <summary>
        /// Called after a hunter fires a shot (post-shoot hook, once confirmed).
        /// Sends the assigned dog to intercept the animal the hunter just shot at,
        /// freeing the hunter to retreat to the Hunting Stand during the reload window.
        ///
        /// Decoy sequence:
        ///   1. Dog moves toward the animal (aggro transfer)
        ///   2. Hunter's movement target set to nearest Hunting Stand or cabin
        ///   3. Animal chases the closer threat (dog)
        ///   4. Hunter fires next shot from the stand — safe range maintained
        /// </summary>
        public static void OnHunterFired(Component hunter, Component animal)
        {
            if (!WardenOfTheWildsMod.HuntingLodgeKitingEnabled.Value) return;

            try
            {
                // Find the dog assigned to this hunter's cabin
                var cabin = FindCabinForHunter(hunter);
                if (cabin == null) return;

                int cabinHash = System.Runtime.CompilerServices
                    .RuntimeHelpers.GetHashCode(cabin);

                DogAssignment? assignment = null;
                foreach (var a in ActiveDogs.Values)
                {
                    if (a.Role != DogRole.HuntingCompanion) continue;
                    if (System.Runtime.CompilerServices.RuntimeHelpers
                            .GetHashCode(a.AssignedBuilding) == cabinHash)
                    {
                        assignment = a;
                        break;
                    }
                }
                if (assignment == null) return;

                // Rate limit — don't spam decoy every frame
                if (Time.time - assignment.LastDecoyTime < 2f) return;
                assignment.LastDecoyTime = Time.time;

                // Send dog toward the animal
                SendDogToIntercept(assignment.Dog, animal);
                assignment.IsEngaging = true;

                // Direct hunter to nearest Hunting Stand or Blind
                var retreatPos = FindRetreatPosition(hunter, cabin);
                if (retreatPos.HasValue)
                    SetMovementTarget(hunter, retreatPos.Value);

                MelonLogger.Msg(
                    $"[WotW] Dog decoy: '{assignment.Dog.gameObject.name}' → " +
                    $"'{animal.gameObject.name}', hunter retreating");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] HuntingDogSystem.OnHunterFired: {ex.Message}");
            }
        }

        // ── Trap patrol tick (TrapperLodge) ───────────────────────────────────
        /// <summary>
        /// Called on the 8-second attraction watcher tick.
        /// Sends TrapperLodge dogs to patrol toward triggered traps,
        /// effectively reducing the trapper's travel time.
        /// </summary>
        public static void TickTrapPatrol()
        {
            foreach (var assignment in ActiveDogs.Values)
            {
                if (assignment.Role != DogRole.TrapLinePatrol) continue;
                if (Time.time - assignment.LastPatrolTime < 8f) continue;
                assignment.LastPatrolTime = Time.time;

                // TODO: find nearest triggered trap and send dog toward it
                // Requires knowing trap class name and triggered state field
                // (confirmed from dump after dog content update)
            }
        }

        // ── Search radius bonus ───────────────────────────────────────────────
        /// <summary>
        /// Returns the search/detection radius multiplier provided by an
        /// assigned dog. Applied to HuntingLodge's huntingRadius.
        /// Dog increases the effective detection range by sniffing out game.
        /// </summary>
        public static float GetSearchRadiusBonus(Component building)
        {
            foreach (var a in ActiveDogs.Values)
            {
                if (a.Role != DogRole.HuntingCompanion) continue;
                if (a.AssignedBuilding == building)
                    return WardenOfTheWildsMod.HuntingDogSearchBonus.Value;
            }
            return 1.0f;
        }

        /// <summary>
        /// Returns the trap interval multiplier provided by an assigned dog.
        /// Each dog on TrapperLodge reduces trap check interval (faster collection).
        /// </summary>
        public static float GetTrapIntervalBonus(Component building)
        {
            int dogCount = 0;
            foreach (var a in ActiveDogs.Values)
            {
                if (a.Role != DogRole.TrapLinePatrol) continue;
                if (a.AssignedBuilding == building) dogCount++;
            }
            // Each dog reduces interval by a configurable amount, capped at 2 dogs
            float bonus = 1f + (System.Math.Min(dogCount, 2) * 0.2f);
            return bonus; // e.g. 2 dogs = 1.4× faster (interval ÷ 1.4)
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static Component? FindCabinForHunter(Component hunter)
        {
            // TODO: find the HunterBuilding that this villager is assigned to.
            // Pattern: iterate HunterBuildings, check if hunter is in assignedWorkers.
            // Implement once worker field name confirmed from combat dump.
            return null;
        }

        private static void SendDogToIntercept(Component dog, Component target)
        {
            try
            {
                // Try NavMeshAgent destination first (standard Unity approach)
                var agent = dog.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null)
                {
                    agent.SetDestination(target.transform.position);
                    return;
                }

                // Fallback: inject wander point (same system as deer attraction)
                var type = dog.GetType();
                System.Type? check = type;
                while (check != null)
                {
                    var setter = check.GetMethod("SetWanderPoints", AllInstance);
                    if (setter != null)
                    {
                        setter.Invoke(dog, new object[] {
                            new List<Vector3> { target.transform.position }
                        });
                        return;
                    }
                    check = check.BaseType;
                }
            }
            catch { }
        }

        private static Vector3? FindRetreatPosition(Component hunter, Component cabin)
        {
            float workRadius = 100f * WardenOfTheWildsMod.HuntingLodgeRadiusMult.Value;

            // Prefer assigned Hunting Blind
            var blind = HuntingBlindSystem.FindRetreatBlind(
                hunter.transform.position,
                cabin.transform.position,
                workRadius);
            if (blind != null) return blind.Position;

            // Fall back to nearest Deer Stand within radius
            var stand = DeerStandSystem.AllStands
                .GetEnumerator();
            Vector3? best = null;
            float bestDist = float.MaxValue;
            while (stand.MoveNext())
            {
                float d = Vector3.Distance(
                    stand.Current.Position, hunter.transform.position);
                if (d < workRadius && d < bestDist)
                {
                    bestDist = d;
                    best = stand.Current.Position;
                }
            }
            if (best.HasValue) return best;

            // Last resort: cabin position itself
            return cabin.transform.position;
        }

        private static void SetMovementTarget(Component worker, Vector3 destination)
        {
            try
            {
                var agent = worker.GetComponent<UnityEngine.AI.NavMeshAgent>();
                if (agent != null) { agent.SetDestination(destination); return; }

                // Wander points fallback
                var type = worker.GetType();
                System.Type? check = type;
                while (check != null)
                {
                    var setter = check.GetMethod("SetWanderPoints", AllInstance);
                    if (setter != null)
                    {
                        setter.Invoke(worker, new object[] {
                            new List<Vector3> { destination }
                        });
                        return;
                    }
                    check = check.BaseType;
                }
            }
            catch { }
        }
    }
}
