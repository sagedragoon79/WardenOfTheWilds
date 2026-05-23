using System;
using System.Collections;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Systems
{
    /// <summary>
    /// Adds Fox to the hunter "huntable animals" team mask so hunters
    /// engage foxes during HuntSubTask just like they do wolves and boars.
    ///
    /// Why it's not vanilla:
    ///   <c>Team</c> enum (Wolves, Bears, Boars, PassiveAnimalToHunt,
    ///   Pests, Chickens, Dogs, etc.) has no dedicated Fox entry — foxes
    ///   are on <c>Team.Pests</c> (or whatever Crate picked). Vanilla
    ///   <c>combatManager.huntingAnimalsTeamDefinition.enemyTeams</c>
    ///   doesn't include Pests, so hunters' HuntSubTask combat-target
    ///   search never considers foxes valid prey.
    ///
    /// What we do:
    ///   1. Wait until at least one Fox exists in the scene (or query
    ///      AnimalManager.foxesRO if available).
    ///   2. Read its <c>combatComp.teamDef.team</c> bitmask.
    ///   3. OR that mask into
    ///      <c>combatManager.huntingAnimalsTeamDefinition.enemyTeams</c>
    ///      AND <c>enemyTeamsForSearching</c>.
    ///   4. Also OR the layer mask from the fox's team into
    ///      <c>enemyTeamSearchLayerMask</c> so the spatial scan picks
    ///      it up.
    ///   5. Done once per session — ScriptableObject mutation persists.
    ///
    /// Soft-gated by Pets DLC + a config pref so players can opt out.
    /// </summary>
    public static class HunterFoxTargetingSystem
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _appliedThisSession;

        public static void OnMapLoaded()
        {
            _appliedThisSession = false;

            if (!DlcDetection.PestGameplayActive) return;
            if (!WardenOfTheWildsMod.HuntersTargetFoxes.Value) return;

            MelonCoroutines.Start(WaitForFoxThenWire());
        }

        /// <summary>
        /// Resolves the Fox prefab via AnimalGroupDefinition (no live
        /// instance required) and reads its team to patch the hunter
        /// huntable mask immediately on map load. Retries periodically
        /// until the AnimalManager has the Fox group dict populated.
        /// </summary>
        private static IEnumerator WaitForFoxThenWire()
        {
            // Initial settle — animal manager wires up the dict
            yield return new WaitForSecondsRealtime(10f);

            var tick = new WaitForSecondsRealtime(5f);
            int attempts = 0;
            const int MaxAttempts = 60; // 60 × 5s = 5 minutes safety cap

            // v1.0.14 fix #3 — don't count attempts while GameManager is null
            // (player lingering on the new-game map-gen/preview screen). Only
            // the bounded post-GM countdown applies; pre-game we wait
            // indefinitely. Mirrors WaitForAnimalManagerThenInit's fix.
            while (!_appliedThisSession && attempts < MaxAttempts)
            {
                if (UnitySingleton<GameManager>.Instance == null)
                {
                    // Pre-game — wait patiently, don't burn the budget.
                    yield return tick;
                    continue;
                }

                attempts++;
                try
                {
                    if (TryApply())
                    {
                        _appliedThisSession = true;
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning(
                        $"[WotW] HunterFoxTargetingSystem.TryApply: {ex.Message}");
                }
                yield return tick;
            }

            if (!_appliedThisSession)
                MelonLogger.Warning(
                    "[WotW] HunterFoxTargetingSystem: Fox prefab unresolvable after " +
                    "5 min post-GameManager. Hunters won't engage foxes this session.");
        }

        private static bool TryApply()
        {
            var gm = UnitySingleton<GameManager>.Instance;
            var animalManager = gm?.animalManager;
            var combatManager = gm?.combatManager;
            if (animalManager == null || combatManager == null) return false;

            // Resolve the Fox prefab via AnimalGroupDefinition. No live
            // instance required — the prefab carries the same combatComp
            // configuration that instances inherit. Works the moment the
            // spawn dict is populated (very early in scene load).
            GameObject foxPrefab = ResolveFoxPrefab(animalManager);
            if (foxPrefab == null) return false;

            // Walk the prefab's components for one that exposes a teamDef
            // field/property. Vanilla puts CombatComponent on the prefab
            // root, but a hardcoded GetComponent<CombatComponent>() ties
            // us to an exact type name we'd rather not bind to.
            object teamDef = null;
            foreach (var mb in foxPrefab.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                Type mbType = mb.GetType();
                var fld = mbType.GetField("teamDef", AllInstance)
                       ?? mbType.GetField("_teamDef", AllInstance);
                object val = fld != null ? fld.GetValue(mb) : null;
                if (val == null)
                {
                    var prop = mbType.GetProperty("teamDef", AllInstance);
                    val = prop?.GetValue(mb);
                }
                if (val != null) { teamDef = val; break; }
            }
            if (teamDef == null) return false;

            var teamField = teamDef.GetType().GetField("team", AllInstance);
            if (teamField == null) return false;
            object foxTeamRaw = teamField.GetValue(teamDef);
            int foxTeamInt = Convert.ToInt32(foxTeamRaw);

            // v1.0.14 fix — the combat-target scan uses both:
            //   (a) team bitmask filter (handled above), AND
            //   (b) Physics.OverlapSphere with enemyTeamSearchLayerMask
            //       — the actual Unity LAYER the candidate's collider lives on.
            //
            // We need the hunter's search mask to include the layer foxes
            // physically sit on, which is the prefab's gameObject.layer (an
            // int 0–31). Earlier (buggy) code read teamDef.enemyTeamSearchLayerMask
            // and merged that — but that's the layer mask the FOX uses to
            // find IT enemies (villagers, buildings), not the layer foxes
            // occupy. Net result: the team bitmask included Pests but the
            // OverlapSphere search layer didn't, so no fox colliders were
            // returned to the hunter's target query. Hunter had nothing to
            // walk toward. UI right-click attack flashes red because the
            // UI uses a different damageable-validity check that DOES find
            // foxes — but the hunter's own combat-acquisition pipeline
            // ignored them.
            int foxLayer = foxPrefab.layer;          // 0–31
            int foxLayerMaskBits = 1 << foxLayer;    // bitmask form

            // Patch BOTH team definitions:
            //
            // 1. hunterTeamDefinition — the hunter villager's combatComp
            //    teamDef. This is what AttackTargetSearchEntry.ProcessTargetCheck
            //    uses (via receiverCombatComponent.teamDef.enemyTeamsForSearching)
            //    to decide which animals to acquire as combat targets.
            //    Without this, hunters never SEE foxes as targets.
            //
            // 2. huntingAnimalsTeamDefinition — the team the hunter wears
            //    during HuntSubTask for incoming-damage routing (so wolves
            //    aren't auto-hostile to a hunter who's currently hunting).
            //    Patching this lets foxes "see" the hunter as enemy, which
            //    matters if foxes ever go aggressive on humans (vanilla:
            //    they don't, but defense-in-depth).
            bool patched1 = PatchTeamDef(combatManager, "hunterTeamDefinition",
                foxTeamInt, foxLayerMaskBits, foxLayer, "hunterTeamDefinition");
            bool patched2 = PatchTeamDef(combatManager, "huntingAnimalsTeamDefinition",
                foxTeamInt, foxLayerMaskBits, foxLayer, "huntingAnimalsTeamDefinition");

            return patched1 || patched2;
        }

        /// <summary>
        /// OR the fox team bit into the given TeamDefinition's enemyTeams /
        /// enemyTeamsForSearching, and OR the fox's physics layer into
        /// enemyTeamSearchLayerMask. Logs delta either way (or "already OK"
        /// if the field is fully patched from a prior session pass).
        /// </summary>
        private static bool PatchTeamDef(
            object combatManager, string fieldName,
            int foxTeamInt, int foxLayerMaskBits, int foxLayer, string label)
        {
            var defField = combatManager.GetType().GetField(fieldName, AllInstance);
            object def = defField?.GetValue(combatManager);
            if (def == null)
            {
                MelonLogger.Warning(
                    $"[WotW] HunterFoxTargeting: {fieldName} not found on combatManager.");
                return false;
            }

            var enemyTeams       = def.GetType().GetField("enemyTeams", AllInstance);
            var enemyTeamsSearch = def.GetType().GetField("enemyTeamsForSearching", AllInstance);
            var layerMask        = def.GetType().GetField("enemyTeamSearchLayerMask", AllInstance);
            if (enemyTeams == null) return false;

            int currentEnemyTeams = Convert.ToInt32(enemyTeams.GetValue(def));
            int currentSearch     = enemyTeamsSearch != null
                ? Convert.ToInt32(enemyTeamsSearch.GetValue(def))
                : currentEnemyTeams;
            int currentLayer = layerMask != null
                ? ((LayerMask)layerMask.GetValue(def)).value
                : 0;

            bool teamOk =
                (currentEnemyTeams & foxTeamInt) != 0
                && (currentSearch & foxTeamInt) != 0;
            bool layerOk = layerMask == null
                || (currentLayer & foxLayerMaskBits) != 0;

            if (teamOk && layerOk)
            {
                MelonLogger.Msg(
                    $"[WotW] HunterFoxTargeting [{label}]: fox team + layer already in mask; no change.");
                return true;
            }

            int newEnemyTeams = currentEnemyTeams | foxTeamInt;
            int newSearch     = currentSearch     | foxTeamInt;

            enemyTeams.SetValue(def,
                Enum.ToObject(enemyTeams.FieldType, newEnemyTeams));
            if (enemyTeamsSearch != null)
                enemyTeamsSearch.SetValue(def,
                    Enum.ToObject(enemyTeamsSearch.FieldType, newSearch));

            int newLayer = currentLayer;
            if (layerMask != null)
            {
                newLayer = currentLayer | foxLayerMaskBits;
                layerMask.SetValue(def, (LayerMask)newLayer);
            }

            MelonLogger.Msg(
                $"[WotW] HunterFoxTargeting [{label}]: fox team 0x{foxTeamInt:X} added. " +
                $"enemyTeams 0x{currentEnemyTeams:X} → 0x{newEnemyTeams:X}, " +
                $"searchLayerMask 0x{currentLayer:X} → 0x{newLayer:X} " +
                $"(fox phys layer {foxLayer}).");
            return true;
        }

        private static Type AppDomainFindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(name); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Walks <c>AnimalManager.spawnIntervalsByAnimalGroupDict</c> to find
        /// the Fox <c>AnimalGroupDefinition</c>, then calls its
        /// <c>GetWeightedAnimalPrefab()</c> to retrieve the prefab. Returns
        /// null until the dict is populated, which is normal for the first
        /// 1-3 seconds of map load.
        /// </summary>
        private static GameObject ResolveFoxPrefab(object animalManager)
        {
            try
            {
                var dictField = animalManager.GetType().GetField(
                    "spawnIntervalsByAnimalGroupDict", AllInstance);
                var dict = dictField?.GetValue(animalManager)
                    as System.Collections.IDictionary;
                if (dict == null) return null;

                foreach (var key in dict.Keys)
                {
                    if (key == null) continue;
                    var typeProp = key.GetType().GetProperty("animalType", AllInstance);
                    if (typeProp == null) continue;

                    string typeName = typeProp.GetValue(key)?.ToString();
                    if (typeName != "Fox") continue;

                    var getPrefab = key.GetType().GetMethod(
                        "GetWeightedAnimalPrefab", Type.EmptyTypes);
                    return getPrefab?.Invoke(key, null) as GameObject;
                }
            }
            catch { /* fall through */ }
            return null;
        }
    }
}
