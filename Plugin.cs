using HarmonyLib;
using MelonLoader;
using UnityEngine;
using System;
using System.Collections;
using System.Reflection;
using WardenOfTheWilds.Components;
using WardenOfTheWilds.Systems;
using WardenOfTheWilds.Patches;

// ─────────────────────────────────────────────────────────────────────────────
//  Stalk & Smoke  v0.1.0
//  A Farthest Frontier overhaul mod by SageDragoon
//  Companion mod to Tended Wilds — both mods enhance each other when active.
//
//  Overview:
//    • Hunter Cabin: Fix Lodge upgrade regression; branching T2 paths
//      (Trapper Lodge = pelts/furs focus | Hunting Lodge = meat/big game focus);
//      multi-worker at T2; optional manual trap placement.
//    • Deer Stands: Cheap placeable structures unlocked by Hunting Lodge.
//      Placed near farms they attract deer, turning a crop-raiding nuisance
//      into a managed hunting resource. Lure toggle adds risk/reward.
//    • Fishing Shack: Tier 2 Fishing Dock with +50 % base output, 2-worker
//      support, and a new Fish Oil byproduct for use as fertilizer or trade.
//    • Smokehouse: Work-area radius (workers only collect within radius);
//      source pinning to specific Hunter Cabins / Fishing Shacks; smarter
//      idle fallback; clearer UI tooltips.
//    • Tended Wilds companion synergies (active only when TW is loaded):
//      - Cultivated berries/greens near Deer Stand → attraction bonus
//      - Willow stock at ForagerShack → cheaper willow trap crafting
//      - Fish Oil → fertilizer for cultivated ForagerShack plots
//      - Herb/mushroom supply near Smokehouse → herb-cured smoked goods
//        (gains food variety bonus tag)
// ─────────────────────────────────────────────────────────────────────────────

[assembly: MelonInfo(typeof(WardenOfTheWilds.WardenOfTheWildsMod), "Warden of the Wilds", "0.1.0", "SageDragoon")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace WardenOfTheWilds
{
    public class WardenOfTheWildsMod : MelonMod
    {
        // ── Singleton ────────────────────────────────────────────────────────
        public static WardenOfTheWildsMod Instance { get; private set; } = null!;
        public static MelonLogger.Instance Log => Instance.LoggerInstance;

        // ── Companion detection ──────────────────────────────────────────────
        /// <summary>True when Tended Wilds is loaded alongside this mod.</summary>
        public static bool TendedWildsActive { get; private set; } = false;

        // ── Feature toggles ───────────────────────────────────────────────────
        public static MelonPreferences_Entry<bool> HunterOverhaulEnabled   { get; private set; } = null!;
        public static MelonPreferences_Entry<bool> FishingOverhaulEnabled  { get; private set; } = null!;
        public static MelonPreferences_Entry<bool> SmokehouseOverhaulEnabled { get; private set; } = null!;
        public static MelonPreferences_Entry<bool> DeerStandsEnabled       { get; private set; } = null!;

        // ── Hunter config ─────────────────────────────────────────────────────
        /// <summary>Work radius multiplier for Hunting Lodge (1.0 = vanilla).</summary>
        public static MelonPreferences_Entry<float> HuntingLodgeRadiusMult  { get; private set; } = null!;
        /// <summary>Pelt/fur yield multiplier for Trapper Lodge. Trappers specialise in
        /// fox and groundhog — pelts are their primary output, not tallow.</summary>
        public static MelonPreferences_Entry<float> TrapperLodgePeltMult    { get; private set; } = null!;
        /// <summary>Maximum deployed traps for Trapper Lodge. Vanilla T2 = 1 trap.
        /// Trapper Lodge raises this to let the trapper run multiple lines simultaneously.
        /// Confirmed: vanilla userDefinedMaxDeployedTraps = 1 (T2 only — T1 has no traps).</summary>
        public static MelonPreferences_Entry<int>   TrapperLodgeTrapCount   { get; private set; } = null!;
        /// <summary>Tallow bonus multiplier when a Smokehouse processes Boar or Bear carcasses.
        /// Boar and Bear are the primary tallow sources — hunted by Hunting Lodge.</summary>
        public static MelonPreferences_Entry<float> BoarBearTallowMult      { get; private set; } = null!;
        /// <summary>Small meat bonus for Hunting Lodge hunters (main benefit is radius + 2 workers).</summary>
        public static MelonPreferences_Entry<float> HuntingLodgeMeatMult    { get; private set; } = null!;
        /// <summary>Additional multiplier on ALL outputs (meat+tallow+pelt) for bear carcasses.
        /// Bears are rare and dangerous — this stacks on top of other bonuses to represent
        /// the exceptional value of a successful bear hunt.</summary>
        public static MelonPreferences_Entry<float> BearMegaYieldMult       { get; private set; } = null!;
        /// <summary>Trap spawn interval multiplier bonus when Trapper Lodge is within
        /// range of a water body. Historically trappers set lines near rivers/streams.</summary>
        public static MelonPreferences_Entry<float> TrapperWaterBonus       { get; private set; } = null!;
        /// <summary>Radius within which a water body counts as "near" the Trapper Lodge.</summary>
        public static MelonPreferences_Entry<float> TrapperWaterBonusRadius { get; private set; } = null!;

        // ── Small-game unlock (fox + groundhog) ──────────────────────────────
        /// <summary>When true, foxes spawn in-world using their normal biome rules
        /// instead of the vanilla 1500-day delay that effectively disables them.
        /// Foxes are fully implemented in game code (wander, food pursuit, retreat)
        /// but Crate gated them behind a delay.</summary>
        public static MelonPreferences_Entry<bool>  UnlockFoxSpawns           { get; private set; } = null!;
        /// <summary>When true, groundhogs spawn in-world using their normal biome rules.
        /// Same unlock mechanism as foxes — Crate has them gated behind 1500 days.</summary>
        public static MelonPreferences_Entry<bool>  UnlockGroundhogSpawns     { get; private set; } = null!;
        /// <summary>Days to wait before foxes begin spawning after map start.
        /// Vanilla default is 1500 (never). Our unlocked default is 30 days so
        /// foxes appear in the first in-game year.</summary>
        public static MelonPreferences_Entry<int>   FoxSpawnDelayDays         { get; private set; } = null!;
        /// <summary>Days to wait before groundhogs begin spawning after map start.</summary>
        public static MelonPreferences_Entry<int>   GroundhogSpawnDelayDays   { get; private set; } = null!;

        // ── Big game spawn tuning ────────────────────────────────────────────
        /// <summary>Multiplier on bear population cap per map. 1.0 = vanilla,
        /// 1.5 = 50% more bears for BGH hunters to work with. Scales both
        /// initial spawn count and maxAnimalCount on the Bear AnimalGroupDefinition.</summary>
        public static MelonPreferences_Entry<float> BearSpawnMultiplier       { get; private set; } = null!;
        /// <summary>Multiplier on wolf population cap per map.</summary>
        public static MelonPreferences_Entry<float> WolfSpawnMultiplier       { get; private set; } = null!;
        /// <summary>Multiplier on boar population cap. Vanilla boars are already
        /// common; default 1.0 keeps them as-is.</summary>
        public static MelonPreferences_Entry<float> BoarSpawnMultiplier       { get; private set; } = null!;
        /// <summary>Multiplier on deer population cap. Affects T1 hunting yield.</summary>
        public static MelonPreferences_Entry<float> DeerSpawnMultiplier       { get; private set; } = null!;

        // ── Butcher speed (T2 2-slot cabins) ─────────────────────────────────
        /// <summary>Butchering work-unit multiplier for T2 Hunting Lodge cabins.
        /// When both workers butcher simultaneously (vanilla behaviour when
        /// carcasses are queued), throughput becomes 2 × this. 1.25 = 25%
        /// faster per worker; combined with 2 workers = ~2.5× T1 vanilla.
        /// Addresses the core "T2 underperforms T1" complaint — butchering was
        /// the bottleneck, now it isn't.</summary>
        public static MelonPreferences_Entry<float> HuntingLodgeButcherSpeedMult { get; private set; } = null!;
        /// <summary>Butchering work-unit multiplier for T2 Trapper Lodge cabins.
        /// Same design as HuntingLodge: 2 workers × this = strong but not
        /// game-breaking. Trapper carcasses (small carcasses from traps) go
        /// through the same butchering pipeline.</summary>
        public static MelonPreferences_Entry<float> TrapperLodgeButcherSpeedMult { get; private set; } = null!;

        // ── Hunting Lodge — big game / survival config ────────────────────────
        /// <summary>Movement speed multiplier applied to Hunting Lodge hunters in off-road terrain.
        /// The tech tree grants a speed boost to all hunters off-road; this preference applies
        /// a baseline boost at T2 before any tech upgrade. Stacks multiplicatively with tech.
        /// Faster hunters = longer safe retreat distance per reload cycle.</summary>
        public static MelonPreferences_Entry<float> HuntingLodgeSpeedMult     { get; private set; } = null!;
        /// <summary>Shoot interval multiplier for Hunting Lodge hunters targeting Boar, Wolf, or Bear.
        /// A lower interval (< 1.0) means faster firing rate against large dangerous game.
        /// Represents crossbow upgrade or coordinated two-hunter suppression fire.</summary>
        public static MelonPreferences_Entry<float> HuntingLodgeBigGameShootMult { get; private set; } = null!;
        /// <summary>Hunter bow reload time in seconds. Used to calculate optimal kite distance
        /// (retreat = reloadTime × hunterSpeed). Update from in-game observation or dump.</summary>
        public static MelonPreferences_Entry<float> BowReloadSeconds             { get; private set; } = null!;
        /// <summary>Hunter crossbow reload time in seconds. Crossbows fire slower but hit harder —
        /// longer reload = hunter should back up further per shot cycle.</summary>
        public static MelonPreferences_Entry<float> CrossbowReloadSeconds        { get; private set; } = null!;
        /// <summary>If true, Hunting Lodge hunters targeting Boar/Wolf/Bear will retreat
        /// to a Hunting Stand position before engaging, rather than chasing into the wild.
        /// Requires at least one Hunting Stand within work radius.</summary>
        public static MelonPreferences_Entry<bool>  HuntingLodgeKitingEnabled    { get; private set; } = null!;
        /// <summary>Health percentage (0–1) below which a hunter refuses to engage predators
        /// and retreats instead. Applies to all tiers — a wounded hunter should not pick fights.
        /// 0.70 = retreat when below 70% HP. 0 = disable check.</summary>
        public static MelonPreferences_Entry<float> HunterLowHealthThreshold     { get; private set; } = null!;
        /// <summary>Minimum arrow count a hunter must carry before engaging prey.
        /// Below this count the hunter paths back to their assigned building to restock.
        /// 0 = disable check. Requires arrow count field to be found in Assembly-CSharp.</summary>
        public static MelonPreferences_Entry<int>   HunterMinArrows              { get; private set; } = null!;
        /// <summary>Distance (world units) a sheltering hunter scans for nearby threats
        /// before emerging from their cabin. Vanilla default is 75u which is tight —
        /// a wolf at that range can still aggro. 90u adds a ~7 tile safety buffer
        /// so the hunter stays put until the threat has genuinely moved off.</summary>
        public static MelonPreferences_Entry<float> HunterShelterSearchRadius    { get; private set; } = null!;
        /// <summary>HP fraction (0-1) below which a T1 hunter will commit to melee
        /// to finish a kill. Above this threshold the hunter stays at range (kite-only).
        /// 0.10 = only melee when target has &lt;10% HP. 0 = never melee. 1 = always.</summary>
        public static MelonPreferences_Entry<float> T1MeleeThreshold             { get; private set; } = null!;
        /// <summary>Pursuit leash multiplier on the hunter's cabin work radius.
        /// If a hunter wanders more than (radius × this value) from their cabin
        /// during a chase, they break off and return. 1.5 = 50% beyond work area.
        /// Prevents hunters from chasing fleeing boars across the entire map.</summary>
        public static MelonPreferences_Entry<float> HunterPursuitLeashMult       { get; private set; } = null!;
        /// <summary>Radius around an engaged hunter to scan for ambush threats
        /// during the chase. If additional aggressive animals appear within this
        /// range (beyond the current target), the hunter retreats.</summary>
        public static MelonPreferences_Entry<float> HunterAmbushScanRadius       { get; private set; } = null!;
        /// <summary>Minimum number of aggressive animals near the hunter that
        /// triggers an ambush break-off. 2 = current target + one other = retreat.
        /// 3 = tolerate one straggler. Set 99 to disable the check.</summary>
        public static MelonPreferences_Entry<int>   HunterAmbushThreshold        { get; private set; } = null!;

        // ── Deer Stand config ─────────────────────────────────────────────────
        /// <summary>Radius within which a Deer Stand attracts game (world units).</summary>
        public static MelonPreferences_Entry<float> DeerStandAttractionRadius { get; private set; } = null!;
        /// <summary>Bonus multiplier when stand is within farm plot proximity.</summary>
        public static MelonPreferences_Entry<float> DeerStandFarmBonus        { get; private set; } = null!;
        /// <summary>Additional bonus when Lure mode is active on the stand.</summary>
        public static MelonPreferences_Entry<float> DeerStandLureBonus        { get; private set; } = null!;
        /// <summary>Key to toggle Lure mode on a selected Deer Stand.</summary>
        public static MelonPreferences_Entry<string> DeerStandLureKeyName     { get; private set; } = null!;

        // ── Fishing Dock config ───────────────────────────────────────────────
        /// <summary>Output multiplier at Fishing Dock (Tier 2). Stacks with tech.</summary>
        public static MelonPreferences_Entry<float> FishingDockOutputMult { get; private set; } = null!;

        // ── Smokehouse config ─────────────────────────────────────────────────
        /// <summary>Default work-area radius for Smokehouse (world units).</summary>
        public static MelonPreferences_Entry<float> SmokehouseDefaultRadius { get; private set; } = null!;
        /// <summary>Key name for cycling Hunter Lodge path. Default P.</summary>
        public static MelonPreferences_Entry<string> HunterPathKeyName     { get; private set; } = null!;

        // ── Hunting Dog config ────────────────────────────────────────────────
        /// <summary>Search radius multiplier for a Hunting Lodge with an assigned dog.
        /// Dog sniffs out game — increases effective detection range.</summary>
        public static MelonPreferences_Entry<float> HuntingDogSearchBonus   { get; private set; } = null!;
        /// <summary>When true, Hunting Lodge dogs act as combat decoys after each shot,
        /// intercepting the target animal while the hunter retreats to reload.</summary>
        public static MelonPreferences_Entry<bool>  HuntingDogDecoyEnabled  { get; private set; } = null!;

        // ── Hunter danger detection config ────────────────────────────────────
        /// <summary>Radius (world units) within which a dangerous animal (Wolf/Boar/Bear)
        /// triggers a proactive retreat even before attacking. Hunters patrolling near a
        /// wolf den will back away before the wolves close to bite range.</summary>
        public static MelonPreferences_Entry<float> HunterDangerRadius { get; private set; } = null!;

        // ── Hunting Blind config ──────────────────────────────────────────────
        /// <summary>Shoot range bonus (world units) granted to a hunter assigned to a Hunting Blind.
        /// Represents the elevation advantage of a fixed raised platform over open ground.</summary>
        public static MelonPreferences_Entry<float> HuntingBlindRangeBonus  { get; private set; } = null!;
        /// <summary>Gold upkeep cost per occupied Hunting Blind per in-game period.
        /// Mirrors the guard tower civilian-worker upkeep. 0 = free to staff.</summary>
        public static MelonPreferences_Entry<float> HuntingBlindGoldUpkeep  { get; private set; } = null!;

        // ── Resolved keybinds ─────────────────────────────────────────────────
        public static KeyCode DeerStandLureKey { get; private set; } = KeyCode.L;
        public static KeyCode HunterPathKey    { get; private set; } = KeyCode.P;

        // ─────────────────────────────────────────────────────────────────────
        public override void OnInitializeMelon()
        {
            Instance = this;

            // ── Companion detection ──────────────────────────────────────────
            foreach (var mod in MelonBase.RegisteredMelons)
            {
                if (mod == this) continue;
                string name = mod.Info?.Name ?? "";
                if (name.IndexOf("Tended Wilds", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    TendedWildsActive = true;
                    Log.Msg("[WotW] Tended Wilds detected — companion synergies enabled.");
                    break;
                }
            }

            // ── Preferences ──────────────────────────────────────────────────
            var cat = MelonPreferences.CreateCategory("WardenOfTheWilds");

            HunterOverhaulEnabled = cat.CreateEntry("HunterOverhaulEnabled", true,
                display_name: "Hunter Overhaul Enabled",
                description: "Enables Hunter Cabin/Lodge upgrade overhaul. " +
                             "Fixes the Lodge-is-a-downgrade bug and adds T2 branching paths.");

            FishingOverhaulEnabled = cat.CreateEntry("FishingOverhaulEnabled", true,
                display_name: "Fishing Overhaul Enabled",
                description: "Enables Fishing Shack Tier 2 (Fishing Dock) with improved " +
                             "output, 2-worker support, and Fish Oil byproduct.");

            SmokehouseOverhaulEnabled = cat.CreateEntry("SmokehouseOverhaulEnabled", true,
                display_name: "Smokehouse Overhaul Enabled",
                description: "Enables Smokehouse work-area radius and source pinning. " +
                             "Workers will only collect from sources within their radius.");

            DeerStandsEnabled = cat.CreateEntry("DeerStandsEnabled", true,
                display_name: "Deer Stands Enabled",
                description: "Enables placeable Deer Stands. Unlocked by Hunting Lodge (T2). " +
                             "Stands near farms attract deer, converting crop-raiding into " +
                             "a managed hunting resource.");

            // Hunter
            HuntingLodgeRadiusMult = cat.CreateEntry("HuntingLodgeRadiusMult", 1.35f,
                display_name: "Hunting Lodge Work Radius Multiplier",
                description: "Work radius multiplier applied to Hunting Lodge buildings. " +
                             "1.35 = 35% larger radius than vanilla.");

            TrapperLodgePeltMult = cat.CreateEntry("TrapperLodgePeltMult", 1.6f,
                display_name: "Trapper Lodge Pelt Multiplier",
                description: "Pelt/fur yield multiplier for Trapper Lodge. " +
                             "1.6 = 60% more pelts than vanilla Lodge.");

            TrapperLodgeTrapCount = cat.CreateEntry("TrapperLodgeTrapCount", 3,
                display_name: "Trapper Lodge Max Deployed Traps",
                description: "Maximum simultaneous traps a Trapper Lodge can deploy. " +
                             "Vanilla T2 = 1 trap (T1 has no trapping). " +
                             "3 = three active trap lines, matching the 26-day cycle: " +
                             "at ×1.6 pelt mult the interval drops to ~16 days, " +
                             "so 3 traps keeps the trapper continuously busy without idle time. " +
                             "Higher values extend reach but one trapper can only service so many lines.");

            BoarBearTallowMult = cat.CreateEntry("BoarBearTallowMult", 1.5f,
                display_name: "Boar/Bear Tallow Multiplier",
                description: "Tallow output multiplier when a Smokehouse processes Boar or Bear carcasses. " +
                             "Boar and Bear are the primary tallow sources — this bonus applies at the " +
                             "Smokehouse, not the Hunter Cabin. 1.5 = 50% more tallow from large game.");

            HuntingLodgeMeatMult = cat.CreateEntry("HuntingLodgeMeatMult", 1.15f,
                display_name: "Hunting Lodge Meat Multiplier",
                description: "Small meat yield multiplier for Hunting Lodge hunters. The main benefit " +
                             "of the Hunting Lodge is radius and 2 worker slots (volume); this is a " +
                             "modest quality bonus on top. 1.15 = 15% more meat per carcass.");

            BearMegaYieldMult = cat.CreateEntry("BearMegaYieldMult", 2.5f,
                display_name: "Bear Mega-Yield Multiplier",
                description: "Bears are rare, dangerous apex predators. This multiplier is applied on " +
                             "top of all other bonuses for ALL outputs (meat, tallow, pelt) when a " +
                             "bear carcass is processed. 2.5 = bears yield 2.5× what a normal large " +
                             "game carcass would after other bonuses. Represents the exceptional value " +
                             "of a successful bear hunt.");

            TrapperWaterBonus = cat.CreateEntry("TrapperWaterBonus", 1.25f,
                display_name: "Trapper Lodge Water Proximity Bonus",
                description: "Pelt yield bonus when a Trapper Lodge is near a water body. " +
                             "Trappers set lines along rivers and streams — historically the richest " +
                             "trapping grounds. 1.25 = 25% more pelt output near water.");

            TrapperWaterBonusRadius = cat.CreateEntry("TrapperWaterBonusRadius", 60f,
                display_name: "Trapper Water Bonus Detection Radius",
                description: "World-unit radius within which a water body triggers the Trapper " +
                             "Lodge water proximity bonus.");

            UnlockFoxSpawns = cat.CreateEntry("UnlockFoxSpawns", true,
                display_name: "Unlock Fox Spawns",
                description: "Enables foxes in the world. Crate implemented foxes fully (wander, " +
                             "food pursuit, retreat behaviors) but gated them behind a 1500-day " +
                             "spawn delay that effectively disables them. When true, this mod " +
                             "overrides the delay so foxes appear on schedule.");

            UnlockGroundhogSpawns = cat.CreateEntry("UnlockGroundhogSpawns", true,
                display_name: "Unlock Groundhog Spawns",
                description: "Enables groundhogs in the world, same unlock mechanism as foxes.");

            FoxSpawnDelayDays = cat.CreateEntry("FoxSpawnDelayDays", 90,
                display_name: "Fox Spawn Delay (days)",
                description: "Days after the FIRST chicken coop is built before foxes begin " +
                             "spawning (vanilla gates foxes on coop presence). 90 = ~3 months " +
                             "of coop development before the first fox appears, giving the " +
                             "player time to establish defenses. More chickens attract more " +
                             "foxes (vanilla scaling). Lower = aggressive pest pressure, " +
                             "higher = lenient.");

            GroundhogSpawnDelayDays = cat.CreateEntry("GroundhogSpawnDelayDays", 90,
                display_name: "Groundhog Spawn Delay (days)",
                description: "Days after the FIRST crop field is built before groundhogs begin " +
                             "spawning (vanilla gates groundhogs on field presence). 90 = ~3 " +
                             "months of farming before crops come under pressure. More fields " +
                             "attract more groundhog groups.");

            BearSpawnMultiplier = cat.CreateEntry("BearSpawnMultiplier", 1.5f,
                display_name: "Bear Spawn Multiplier",
                description: "Multiplier on bear population cap per map. 1.5 = 50% more bears, " +
                             "giving BGH hunters meaningful big-game targets. 1.0 = vanilla. " +
                             "Scales initialSpawnCount + maxAnimalCount on the Bear group.");

            WolfSpawnMultiplier = cat.CreateEntry("WolfSpawnMultiplier", 1.25f,
                display_name: "Wolf Spawn Multiplier",
                description: "Multiplier on wolf population cap. 1.25 = 25% more wolves. " +
                             "More frequent wolves mean more hunter engagements — set lower if " +
                             "early game is too dangerous.");

            BoarSpawnMultiplier = cat.CreateEntry("BoarSpawnMultiplier", 1.0f,
                display_name: "Boar Spawn Multiplier",
                description: "Multiplier on boar population cap. Vanilla default is already " +
                             "generous; 1.0 keeps the balance.");

            DeerSpawnMultiplier = cat.CreateEntry("DeerSpawnMultiplier", 1.0f,
                display_name: "Deer Spawn Multiplier",
                description: "Multiplier on deer population cap. Affects T1 hunter meat yield.");

            HuntingLodgeButcherSpeedMult = cat.CreateEntry(
                "HuntingLodgeButcherSpeedMult", 1.25f,
                display_name: "Hunting Lodge Butcher Speed Multiplier",
                description: "Per-worker butchering speed bonus for T2 Hunting Lodge. " +
                             "Stacks with 2-worker parallelism — 2 workers × 1.25 ≈ 2.5× " +
                             "T1 vanilla butchering throughput. Resolves the 'T2 " +
                             "underperforms T1' complaint (butchering was the bottleneck). " +
                             "1.0 = vanilla per-worker speed (still 2× from parallelism).");

            TrapperLodgeButcherSpeedMult = cat.CreateEntry(
                "TrapperLodgeButcherSpeedMult", 1.25f,
                display_name: "Trapper Lodge Butcher Speed Multiplier",
                description: "Same mechanic for Trapper Lodge. Small carcasses from traps " +
                             "go through the same butchering pipeline — bonus applies.");

            // Hunting Lodge big game
            HuntingLodgeSpeedMult = cat.CreateEntry("HuntingLodgeSpeedMult", 1.35f,
                display_name: "Hunting Lodge Off-Road Speed Multiplier",
                description: "Movement speed multiplier for Hunting Lodge (BGH) hunters. Applied " +
                             "at T2 as a baseline path specialisation bonus — stacks with the " +
                             "tech tree off-road speed upgrade (Trailblazing). 1.35 = 35% faster " +
                             "than a vanilla hunter, enough for BGH to consistently out-kite wolves " +
                             "and boars. T1 hunters without this buff rely on shelter + initial " +
                             "engagement distance for survival. " +
                             "Faster retreat = more distance covered per reload cycle = safer kiting. " +
                             "Requires confirming the speed field name from the combat dump.");

            HuntingLodgeBigGameShootMult = cat.CreateEntry("HuntingLodgeBigGameShootMult", 0.75f,
                display_name: "Hunting Lodge Big Game Shoot Interval Multiplier",
                description: "Shoot/fire-rate interval multiplier for Hunting Lodge hunters when " +
                             "targeting Boar, Wolf, or Bear. Lower = faster shots. Represents the " +
                             "Hunting Lodge's crossbow upgrade and two-hunter coordination. " +
                             "0.75 = 25% faster firing rate against large dangerous game. " +
                             "Requires confirming shoot interval field from Assembly-CSharp decompile.");

            BowReloadSeconds = cat.CreateEntry("BowReloadSeconds", 3.0f,
                display_name: "Bow Reload Time (seconds)",
                description: "Hunter bow reload/attack interval in seconds. Used to calculate " +
                             "optimal kite distance: retreat = reloadTime × hunterSpeed. " +
                             "Default 3.0 is an estimate — update once confirmed from dump or " +
                             "in-game timing.");

            CrossbowReloadSeconds = cat.CreateEntry("CrossbowReloadSeconds", 4.5f,
                display_name: "Crossbow Reload Time (seconds)",
                description: "Hunter crossbow reload/attack interval in seconds. Crossbows fire " +
                             "slower than bows but deal more damage — longer reload means the " +
                             "hunter should back up further per shot cycle to maintain safe range. " +
                             "Default 4.5 is an estimate — update once confirmed from dump.");

            HuntingLodgeKitingEnabled = cat.CreateEntry("HuntingLodgeKitingEnabled", true,
                display_name: "Hunting Lodge Kiting Enabled",
                description: "When enabled and a Hunting Stand is within work radius, Hunting Lodge " +
                             "hunters will move to the stand position before engaging Boar/Wolf/Bear, " +
                             "rather than chasing them into the wilderness. This draws dangerous " +
                             "animals to a controlled engagement zone — the hunter shoots from the " +
                             "stand, retreats if wounded, then re-engages. Requires decompile of " +
                             "hunter pathfinding/retreat methods to implement fully.");

            HunterLowHealthThreshold = cat.CreateEntry("HunterLowHealthThreshold", 0.50f,
                display_name: "Hunter Low Health Retreat Threshold",
                description: "Health percentage (0.0–1.0) below which a hunter refuses to engage " +
                             "predators and retreats at full distance instead. Applies to all tiers " +
                             "and paths — a critically wounded hunter should not pick fights. " +
                             "0.50 = retreat when below 50% HP. " +
                             "0 = disable check entirely. Also overrides the Boar/Bear flee-skip: " +
                             "a wounded hunter retreats from even a fleeing Boar.");

            HunterMinArrows = cat.CreateEntry("HunterMinArrows", 10,
                display_name: "Hunter Minimum Arrows Before Hunting",
                description: "Minimum arrows a hunter must carry before engaging prey. If their " +
                             "quiver drops below this count they will path back to their assigned " +
                             "building to restock rather than continuing to hunt dry. " +
                             "0 = disable check. Requires the arrow-count field to be located in " +
                             "the Assembly-CSharp dump — see log for discovery status.");

            HunterShelterSearchRadius = cat.CreateEntry("HunterShelterSearchRadius", 45f,
                display_name: "Hunter Cabin Emergence Radius (u)",
                description: "Distance at which the Shelter Guard keeps a sheltering hunter " +
                             "inside their cabin. Vanilla's own check only looks for Raiders + " +
                             "Bears (wolves and boars are invisible to it), and uses 75u. " +
                             "Our Shelter Guard adds wolf/boar/bear awareness. " +
                             "45u ≈ hunter's bow range (40u) + a small safety margin — hunter " +
                             "stays inside only when threats are in the direct engagement zone, " +
                             "and emerges to shoot anything further out. " +
                             "Increase this (e.g., 60-90u) for more conservative sheltering.");

            T1MeleeThreshold = cat.CreateEntry("T1MeleeThreshold", 0.10f,
                display_name: "T1 Hunter Melee HP Threshold (0-1)",
                description: "T1 hunters refuse melee unless the target's HP fraction is below " +
                             "this value. Prevents T1 hunters (who carry bows) from charging into " +
                             "aggressive animals. 0.10 = only finish targets with <10% HP. " +
                             "0 = never melee (pure kite). 1 = always allow melee (vanilla). " +
                             "Only affects T1 (Vanilla-path) hunters — T2 paths keep vanilla logic.");

            HunterPursuitLeashMult = cat.CreateEntry("HunterPursuitLeashMult", 1.5f,
                display_name: "Hunter Pursuit Leash Multiplier",
                description: "Maximum pursuit distance as a multiple of the hunter cabin's work " +
                             "radius. If a hunter chases prey past (radius × this value) from their " +
                             "cabin, they break off and return. 1.5 = 50% beyond work area. " +
                             "Prevents hunters from chasing fleeing boars across the map and " +
                             "getting killed by distant wolves or bears.");

            HunterAmbushScanRadius = cat.CreateEntry("HunterAmbushScanRadius", 25f,
                display_name: "Hunter Ambush Scan Radius (u)",
                description: "Distance around an engaged hunter scanned for ambush threats. If " +
                             "additional aggressive animals (beyond the current target) appear " +
                             "within this range during a chase, the hunter retreats. Catches the " +
                             "'boar led me into a wolf pack' death scenario.");

            HunterAmbushThreshold = cat.CreateEntry("HunterAmbushThreshold", 2,
                display_name: "Hunter Ambush Threat Count",
                description: "Number of aggressive animals within the ambush scan radius that " +
                             "triggers a retreat. 2 = current target + one other = retreat. " +
                             "3 = tolerate one straggler. 99 = disable the check entirely.");

            // Hunting Dog (pre-wired for Crate's upcoming cat/dog content update)
            HuntingDogSearchBonus = cat.CreateEntry("HuntingDogSearchBonus", 1.3f,
                display_name: "Hunting Dog Search Radius Bonus",
                description: "Multiplier applied to a Hunting Lodge's work radius when a dog " +
                             "is assigned. Dogs sniff out game at greater range than unaided " +
                             "hunters. 1.3 = 30% larger effective detection radius. " +
                             "Requires Crate's dog content update to activate.");

            HuntingDogDecoyEnabled = cat.CreateEntry("HuntingDogDecoyEnabled", true,
                display_name: "Hunting Dog Combat Decoy Enabled",
                description: "When true, an assigned Hunting Lodge dog intercepts dangerous game " +
                             "(Boar/Wolf/Bear) after each hunter shot, drawing the animal's aggro " +
                             "while the hunter retreats to reload from a safe distance. " +
                             "This is the natural kiting solution — the dog tanks, the hunter shoots. " +
                             "Requires Crate's dog content update and confirmed pet API methods.");

            // Hunter danger detection
            HunterDangerRadius = cat.CreateEntry("HunterDangerRadius", 40f,
                display_name: "Hunter Danger Detection Radius",
                description: "World units within which a Wolf, Boar, or Bear triggers a proactive " +
                             "retreat — even before it attacks. Hunters chasing deer near a wolf den " +
                             "will back away when predators enter this radius. " +
                             "40 = roughly 2× bow range. Set to 0 to disable proximity retreats.");

            // Hunting Blind
            HuntingBlindRangeBonus = cat.CreateEntry("HuntingBlindRangeBonus", 6f,
                display_name: "Hunting Blind Shoot Range Bonus",
                description: "Bonus world units added to a hunter's shoot range when stationed " +
                             "in a Hunting Blind. Represents the elevation advantage of a fixed " +
                             "raised platform. Stacks with tech tree range upgrades.");

            HuntingBlindGoldUpkeep = cat.CreateEntry("HuntingBlindGoldUpkeep", 0.5f,
                display_name: "Hunting Blind Gold Upkeep",
                description: "Gold upkeep per occupied Hunting Blind per in-game maintenance period. " +
                             "Mirrors the guard tower civilian-worker model. Set to 0 to disable " +
                             "upkeep entirely. Requires guard tower upkeep field from dump.");

            // Deer stands
            DeerStandAttractionRadius = cat.CreateEntry("DeerStandAttractionRadius", 40f,
                display_name: "Deer Stand Attraction Radius",
                description: "World-unit radius within which a Deer Stand increases " +
                             "the probability of deer spawning or lingering.");

            DeerStandFarmBonus = cat.CreateEntry("DeerStandFarmBonus", 1.5f,
                display_name: "Deer Stand Farm Proximity Bonus",
                description: "Attraction multiplier when a Deer Stand is placed within " +
                             "40u of a farm plot. Deer are drawn to crops — use this " +
                             "to turn a problem into a food source.");

            DeerStandLureBonus = cat.CreateEntry("DeerStandLureBonus", 1.3f,
                display_name: "Deer Stand Lure Mode Bonus",
                description: "Additional attraction multiplier when Lure mode is active. " +
                             "Lure mode risks slightly higher crop-nibbling near farms.");

            DeerStandLureKeyName = cat.CreateEntry("DeerStandLureKey", "L",
                display_name: "Deer Stand Lure Toggle Key",
                description: "While a Deer Stand is selected, press this key to toggle " +
                             "Lure mode on/off. Use Unity KeyCode name (e.g. L, F, Tab).");

            // Fishing
            FishingDockOutputMult = cat.CreateEntry("FishingDockOutputMult", 1.5f,
                display_name: "Fishing Dock Output Multiplier",
                description: "Fish output multiplier at Tier 2 Fishing Dock. " +
                             "1.5 = 50% more fish, bringing it in line with other food sources.");

            // Smokehouse
            SmokehouseDefaultRadius = cat.CreateEntry("SmokehouseDefaultRadius", 55f,
                display_name: "Smokehouse Default Work Radius",
                description: "Default radius (world units) within which a Smokehouse " +
                             "worker will collect raw meat and fish. Workers ignore " +
                             "buildings outside this radius, preventing map-wide travel.");

            HunterPathKeyName = cat.CreateEntry("HunterPathKey", "P",
                display_name: "Hunter Lodge Path Cycle Key",
                description: "While a T2 Hunter Lodge is selected, press this key to cycle " +
                             "between Vanilla / Trapper Lodge / Hunting Lodge specialisations. " +
                             "Use Unity KeyCode name (e.g. P, Y, Tab).");

            // ── Parse keybinds ────────────────────────────────────────────────
            if (Enum.TryParse(DeerStandLureKeyName.Value, ignoreCase: true, out KeyCode lureKey))
                DeerStandLureKey = lureKey;
            else
                Log.Warning($"[WotW] Could not parse DeerStandLureKey \"{DeerStandLureKeyName.Value}\", defaulting to L.");

            if (Enum.TryParse(HunterPathKeyName.Value, ignoreCase: true, out KeyCode pathKey))
                HunterPathKey = pathKey;
            else
                Log.Warning($"[WotW] Could not parse HunterPathKey \"{HunterPathKeyName.Value}\", defaulting to P.");

            // ── Apply Harmony patches ─────────────────────────────────────────
            // PatchAll() handles attribute-decorated patches (none yet in this mod).
            // The three game-type patches are applied manually because their target
            // class names are resolved at runtime via Assembly.GetType() scanning.
            HarmonyInstance.PatchAll();

            HunterCabinPatches.ApplyPatches(HarmonyInstance);
            HunterCombatPatches.ApplyPatches(HarmonyInstance);
            HunterHotkeyPatches.ApplyPatches(HarmonyInstance);
            FishingShackPatches.ApplyPatches(HarmonyInstance);
            SmokehousePatches.ApplyPatches(HarmonyInstance);

            Log.Msg($"[WotW] Stalk & Smoke 0.1.0 loaded." +
                    $" TendedWilds: {TendedWildsActive}" +
                    $" | Hunter: {HunterOverhaulEnabled.Value}" +
                    $" | Fishing: {FishingOverhaulEnabled.Value}" +
                    $" | Smokehouse: {SmokehouseOverhaulEnabled.Value}" +
                    $" | DeerStands: {DeerStandsEnabled.Value}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != "Map") return;

            Log.Msg("[WotW] Map scene loaded — initialising systems.");

            // Reset per-scene state in all systems
            HunterCabinEnhancement.OnMapLoaded();
            FishingShackEnhancement.OnMapLoaded();
            SmokehouseEnhancement.OnMapLoaded();
            DeerStandSystem.OnMapLoaded();
            HuntingBlindSystem.OnMapLoaded();
            HuntingDogSystem.OnMapLoaded();
            HunterCombatPatches.OnMapLoaded();
            HunterHotkeyPatches.OnMapLoaded();
            TendedWildsCompat.OnMapLoaded();

            // Unlock fox + groundhog spawns (override vanilla 1500-day delays)
            SmallGameUnlockSystem.OnMapLoaded();

            // Scale bear/wolf/boar/deer population caps via config multipliers
            AnimalSpawnTuningSystem.OnMapLoaded();

            MelonCoroutines.Start(LateInit());

            // Deer Stand attraction — runs every 8 s once scene settles (15 s head start)
            if (DeerStandsEnabled.Value)
                MelonCoroutines.Start(DeerStandSystem.AttractionWatcher());

            // Hunter danger proximity watch — runs every 2s, triggers retreat if
            // a predator enters HunterDangerRadius before it can land a hit
            if (HunterOverhaulEnabled.Value && HunterDangerRadius.Value > 0f)
                MelonCoroutines.Start(HunterCombatPatches.DangerProximityWatcher());
        }

        /// <summary>
        /// Delayed init to allow Unity scene objects and save data to fully
        /// deserialise before we attach components and apply patches.
        /// Mirrors the pattern used in Tended Wilds and Manifest Delivery.
        /// </summary>
        private IEnumerator LateInit()
        {
            yield return new WaitForSeconds(10f);

            int attempts = 0;
            while (attempts < 15)
            {
                attempts++;
                if (TryAttachComponents())
                {
                    Log.Msg($"[WotW] Components attached after {attempts} attempt(s).");
                    yield break;
                }
                yield return new WaitForSeconds(3f);
            }

            Log.Warning("[WotW] LateInit: failed to find game buildings after all attempts.");
        }

        private bool TryAttachComponents()
        {
            // TODO: Replace "HunterCabin", "FishingShack", "Smokehouse" with confirmed
            // class names from Assembly-CSharp.dll decompilation.
            // See DESIGN.md § Technical Notes for context.

            bool foundAny = false;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Hunter Cabin — HunterBuilding confirmed from Assembly-CSharp.dll decompile
                var hunterType = asm.GetType("HunterBuilding") ?? asm.GetType("HunterCabin");
                if (hunterType != null && HunterOverhaulEnabled.Value)
                {
                    foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(hunterType))
                    {
                        var go = (obj as Component)?.gameObject;
                        if (go != null && go.GetComponent<HunterCabinEnhancement>() == null)
                        {
                            go.AddComponent<HunterCabinEnhancement>();
                            foundAny = true;
                        }
                    }
                }

                // Fishing Shack
                var fishType = asm.GetType("FishingShack") ?? asm.GetType("FishermanShack");
                if (fishType != null && FishingOverhaulEnabled.Value)
                {
                    foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(fishType))
                    {
                        var go = (obj as Component)?.gameObject;
                        if (go != null && go.GetComponent<FishingShackEnhancement>() == null)
                        {
                            go.AddComponent<FishingShackEnhancement>();
                            foundAny = true;
                        }
                    }
                }

                // Smokehouse — NOTE: capital H confirmed from Assembly-CSharp.dll decompile
                var smokeType = asm.GetType("SmokeHouse");
                if (smokeType != null && SmokehouseOverhaulEnabled.Value)
                {
                    foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(smokeType))
                    {
                        var go = (obj as Component)?.gameObject;
                        if (go != null && go.GetComponent<SmokehouseEnhancement>() == null)
                        {
                            go.AddComponent<SmokehouseEnhancement>();
                            foundAny = true;
                        }
                    }
                }
            }

            return foundAny;
        }
    }
}
