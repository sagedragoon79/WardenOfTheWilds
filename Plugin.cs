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
//  Warden of the Wilds  v0.1.0
//  A Farthest Frontier hunter + fishing overhaul by SageDragoon
//
//  Overview:
//    • Hunter Cabin: Fix Lodge upgrade regression; branching T2 paths
//      (Trapper Lodge = pelts/furs focus | Hunting Lodge = meat/big game focus);
//      multi-worker at T2; optional manual trap placement.
//    • Hunter combat AI: kiting, retreat-to-cabin, reemergence, proactive
//      engagement scans, ambush detection, pursuit leash.
//    • Fishing Shack: Tier 2 Fishing Dock with +50 % base output, 2-worker
//      support, Angler/Creeler mode system, Sustainable Fishing tech rework.
//    • Ctrl+K: select every hunter on the map (right-click to move/attack).
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
        // TOP-LEVEL — disable an entire area of the mod.
        public static MelonPreferences_Entry<bool> HunterOverhaulEnabled   { get; private set; } = null!;
        public static MelonPreferences_Entry<bool> FishingOverhaulEnabled  { get; private set; } = null!;

        // SUB-SYSTEM — finer-grained toggles for diagnosis. Each can be turned
        // off independently to isolate a performance issue. Top-level toggles
        // above still gate the whole area; these only matter when top-level
        // is ON.
        /// <summary>Kiting AI, proactive engagement scans, post-shot retreat,
        /// chase-safety/leash, multi-predator retreat. The "smart combat" layer
        /// on top of vanilla. Suspect #1 for combat-era stutter.</summary>
        public static MelonPreferences_Entry<bool> HunterCombatEnabled { get; private set; } = null!;
        /// <summary>Per-frame keybind polling (Ctrl+K rally, Alt+K return home,
        /// Ctrl+K select-all). Cheap but fires every frame.</summary>
        public static MelonPreferences_Entry<bool> HunterRallyEnabled { get; private set; } = null!;
        /// <summary>Big Game Hunter / Trap Master button UI injection +
        /// slider pin. OnGUI overlay on the hunter shack.</summary>
        public static MelonPreferences_Entry<bool> HunterUIEnabled { get; private set; } = null!;
        /// <summary>Angler / Creeler slider button UI injection. Tooltip
        /// rendering, mode-change events, OnGUI overhead.</summary>
        public static MelonPreferences_Entry<bool> FishingUIEnabled { get; private set; } = null!;
        /// <summary>Tech tree patch (Sustainable Fishing value + description).
        /// Runs once on map load; normally zero runtime cost.</summary>
        public static MelonPreferences_Entry<bool> TechTreePatchEnabled { get; private set; } = null!;

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
        /// <summary>Small meat bonus for Hunting Lodge hunters (main benefit is radius + 2 workers).</summary>
        public static MelonPreferences_Entry<float> HuntingLodgeMeatMult    { get; private set; } = null!;
        /// <summary>Additional multiplier on ALL outputs (meat+tallow+pelt) for bear carcasses.
        /// Bears are rare and dangerous — this stacks on top of other bonuses to represent
        /// the exceptional value of a successful bear hunt.</summary>
        public static MelonPreferences_Entry<float> BearMegaYieldMult       { get; private set; } = null!;
        /// <summary>Bonus meat deposited into the killing BGH hunter's cabin when a bear dies.
        /// Stacks on top of the vanilla butcher yield from the carcass itself (~30 meat).
        /// 220 bonus + 30 butcher ≈ 250 meat per bear kill total.</summary>
        public static MelonPreferences_Entry<int>   BGHBearBonusMeat       { get; private set; } = null!;
        /// <summary>Bonus pelts (ItemHide) deposited into the killing BGH hunter's cabin when
        /// a bear dies. Stacks on top of the vanilla butcher yield (~2 hide).
        /// 3 bonus + 2 butcher ≈ 5 pelts per bear kill total.</summary>
        public static MelonPreferences_Entry<int>   BGHBearBonusPelt       { get; private set; } = null!;
        /// <summary>Bonus tallow deposited into the killing BGH hunter's cabin when a bear dies.
        /// Stacks on top of the vanilla butcher yield (~3 tallow).
        /// 5 bonus + 3 butcher ≈ 8 tallow per bear kill total.</summary>
        public static MelonPreferences_Entry<int>   BGHBearBonusTallow     { get; private set; } = null!;
        /// <summary>Trap spawn interval multiplier bonus when Trapper Lodge is within
        /// range of a water body. Historically trappers set lines near rivers/streams.</summary>
        public static MelonPreferences_Entry<float> TrapperWaterBonus       { get; private set; } = null!;
        /// <summary>Radius within which a water body counts as "near" the Trapper Lodge.</summary>
        public static MelonPreferences_Entry<float> TrapperWaterBonusRadius { get; private set; } = null!;
        /// <summary>Trap Master traps tick faster than vanilla. This multiplier is applied
        /// on top of the pelt mult — combined effect shortens the spawn interval further.
        /// 1.25 = 25% faster trap ticks (interval ÷ 1.25).</summary>
        public static MelonPreferences_Entry<float> TrapMasterSpeedMult     { get; private set; } = null!;
        /// <summary>Minimum number of water tiles (sampled on a grid within hunting radius)
        /// required to trigger the Trap Master water pelt bonus. Maps with extensive
        /// lakefront or river coverage reward placing the lodge near water.</summary>
        public static MelonPreferences_Entry<int>   TrapperWaterTileThreshold { get; private set; } = null!;
        /// <summary>Chance (0-1) per trap carcass spawn that the trap catches a bear instead.
        /// On success, bonus meat/hide/tallow are injected directly into the cabin's
        /// manufacturingStorage (same amounts as BGH bear kill bonus). Passive bear income
        /// for the Trap Master path — no combat required.</summary>
        public static MelonPreferences_Entry<float> TrapMasterBearChance    { get; private set; } = null!;

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

        // ── Rally / recall keybinds ──────────────────────────────────────────
        /// <summary>Key to rally all hunters to the current cursor world position.
        /// Early-game raider defense use case — player can pull every hunter to
        /// a chokepoint with one hotkey instead of clicking each individually.
        /// Unity KeyCode name (G, H, R, etc.). Change if it conflicts with
        /// vanilla bindings.</summary>
        public static MelonPreferences_Entry<string> HunterRallyKeyName       { get; private set; } = null!;
        /// <summary>Key to send all hunters back to their assigned cabins.
        /// Useful for pulling hunters out of combat when things escalate.</summary>
        public static MelonPreferences_Entry<string> HunterReturnHomeKeyName  { get; private set; } = null!;
        /// <summary>Key to select all hunters into vanilla's multi-selection.
        /// Combined with normal vanilla click-to-move, this lets you hotkey-select
        /// every hunter then right-click terrain to command them. No rally point
        /// commitment needed.</summary>
        public static MelonPreferences_Entry<string> HunterSelectAllKeyName  { get; private set; } = null!;

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

        // ── Fishing Shack mode config ────────────────────────────────────────
        /// <summary>Output multiplier at Fishing Dock (Tier 2). Stacks with tech.
        /// Legacy config — kept for FishingDock buildings (separate class from FishingShack).</summary>
        public static MelonPreferences_Entry<float> FishingDockOutputMult { get; private set; } = null!;
        /// <summary>Catch multiplier for Angler mode rod fishing.
        /// Applied via GetNumFishCaught postfix. 1.5 = 50% more fish per catch cycle.
        /// Full Angler: both workers at this rate. Mixed: averaged with CreelerRodMult.</summary>
        public static MelonPreferences_Entry<float> AnglerCatchMult { get; private set; } = null!;
        /// <summary>Timer reduction for Angler mode. Multiplied against vanilla 28-32s
        /// between-catch timer. 0.65 = 35% faster catches (~18-21s). Lower = faster.</summary>
        public static MelonPreferences_Entry<float> AnglerTimerReduction { get; private set; } = null!;
        /// <summary>Bonus carry capacity for Angler workers. Added to vanilla fishCapacity (25).
        /// 15 = carry 40 fish before returning (fewer return trips).</summary>
        public static MelonPreferences_Entry<int>   AnglerCapacityBonus { get; private set; } = null!;
        /// <summary>Rod-fishing output multiplier for Creeler workers.
        /// Creelers spend time on traps, reducing their rod-fishing yield.
        /// Real Creeler output comes from the daily-tick trap system.
        /// 0.5 = 50% reduced rod-fishing. 0 = no rod output at all.</summary>
        public static MelonPreferences_Entry<float> CreelerRodMult { get; private set; } = null!;
        /// <summary>Days between Creeler trap spawns. Each Creeler worker slot produces
        /// CrabTrapFishPerSpawn fish every this many days. Lower = faster income.
        /// Water tile bonus divides this further on water-heavy maps.</summary>
        public static MelonPreferences_Entry<int>   CrabTrapSpawnDays { get; private set; } = null!;
        /// <summary>Fish produced per Creeler slot per spawn event. At default 4 fish
        /// every 5 days with 2 Creeler slots: ~584 fish/year passive income.</summary>
        public static MelonPreferences_Entry<int>   CrabTrapFishPerSpawn { get; private set; } = null!;
        /// <summary>Number of physical creel traps a Creeler-mode fishing shack deploys
        /// in water within its fishing radius. Recommended range 4-6.</summary>
        public static MelonPreferences_Entry<int>   CreelerTrapCount { get; private set; } = null!;
        /// <summary>Fishing radius multiplier for Creeler mode. Creeler traps spread
        /// across a larger water area. 2.0 = 60u (vanilla 30u). Affects which water
        /// tiles are counted for the water bonus and which fishing areas are used.</summary>
        public static MelonPreferences_Entry<float> CreelerRadiusMult { get; private set; } = null!;
        /// <summary>Minimum water tiles within the fishing radius to activate the
        /// Creeler water bonus. Maps with large deep lakes reward Creeler placement.
        /// 15 tiles on 8u grid = a moderate lake shoreline.</summary>
        public static MelonPreferences_Entry<int>   CreelerWaterTileThreshold { get; private set; } = null!;
        /// <summary>Trap spawn interval divisor when water bonus is active.
        /// 1.25 = 25% faster trap ticks on water-rich maps. Stacks with
        /// base CrabTrapSpawnDays — at default values: 5d / 1.25 = 4d interval.</summary>
        public static MelonPreferences_Entry<float> CreelerWaterTileBonus { get; private set; } = null!;
        /// <summary>Fish replenish rate bonus applied to the Sustainable Fishing tech node.
        /// Vanilla is 0.3 (30%). We bump to 0.5 (50%) to make the node worth investing in.</summary>
        public static MelonPreferences_Entry<float> FishReplenishOverride { get; private set; } = null!;
        /// <summary>Storage capacity for fishing shacks (vanilla = 100).
        /// Set higher to match wagon capacity for efficient logistics.</summary>
        public static MelonPreferences_Entry<int> FishingShackStorageCap { get; private set; } = null!;

        // ── Tech tree state ───────────────────────────────────────────────────
        private static bool _techTreePatched = false;
        /// <summary>True once Sustainable Fishing has been researched. Checked by
        /// FishingShackEnhancement to gate mode slider and buffs.</summary>
        public static bool SustainableFishingResearched { get; private set; } = false;

        /// <summary>
        /// Fires exactly once when Sustainable Fishing is researched during a
        /// session. UI (slider) subscribes to this instead of polling. The
        /// firing is driven by TechResearchPatches's Harmony hook on the tech
        /// node's rank setter.
        /// </summary>
        public static event System.Action OnSustainableFishingResearched;

        /// <summary>Internal: set the research flag and fire the event exactly
        /// once on transition from false → true.</summary>
        internal static void SetSustainableFishingResearched(bool researched)
        {
            if (SustainableFishingResearched == researched) return;
            SustainableFishingResearched = researched;
            if (researched)
            {
                Log.Msg("[WotW] Sustainable Fishing researched — firing event.");
                try { OnSustainableFishingResearched?.Invoke(); } catch { }
            }
        }

        /// <summary>Key name for cycling Hunter Lodge path. Default P.</summary>
        public static MelonPreferences_Entry<string> HunterPathKeyName     { get; private set; } = null!;

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

            // Sub-system diagnostics — disable individual parts of the mod
            // to isolate a performance issue. These only matter when their
            // parent top-level toggle is ON.
            HunterCombatEnabled = cat.CreateEntry("HunterCombatEnabled", true,
                display_name: "[Diag] Hunter Combat Enabled",
                description: "When OFF, disables kiting AI, proactive engagement scans, " +
                             "post-shot retreat, chase-safety/leash, and multi-predator " +
                             "retreat. Vanilla combat remains. Suspect #1 for combat stutter.");

            HunterRallyEnabled = cat.CreateEntry("HunterRallyEnabled", true,
                display_name: "[Diag] Hunter Rally Hotkeys Enabled",
                description: "When OFF, disables the per-frame hotkey polling (Ctrl+K, " +
                             "Alt+K, etc.). No measurable cost normally, but can be turned " +
                             "off to rule out OnUpdate overhead.");

            HunterUIEnabled = cat.CreateEntry("HunterUIEnabled", true,
                display_name: "[Diag] Hunter UI Enabled",
                description: "When OFF, skips injecting Big Game Hunter / Trap Master " +
                             "buttons and the trap-slider pin. Useful for isolating UI-layer " +
                             "stutter.");

            FishingUIEnabled = cat.CreateEntry("FishingUIEnabled", true,
                display_name: "[Diag] Fishing UI Enabled",
                description: "When OFF, skips injecting the Angler/Creeler button slider " +
                             "and its tooltip. Useful for isolating UI-layer stutter.");

            TechTreePatchEnabled = cat.CreateEntry("TechTreePatchEnabled", true,
                display_name: "[Diag] Tech Tree Patch Enabled",
                description: "When OFF, disables the Sustainable Fishing tech-node patch " +
                             "(value bump + description override). Almost certainly not " +
                             "the stutter source, but included for completeness.");

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

            BGHBearBonusMeat = cat.CreateEntry("BGHBearBonusMeat", 220,
                display_name: "BGH Bear Bonus — Meat",
                description: "Bonus meat (ItemMeat) deposited directly into the killing BGH hunter's " +
                             "cabin when a bear is killed. Stacks on top of the carcass-butcher yield " +
                             "(vanilla generic carcass ≈ 30 meat). Default 220 + 30 ≈ 250 meat/bear. " +
                             "Only applies to Hunting Lodge (BGH) hunters — T1 and Trapper paths " +
                             "use vanilla carcass-only yield.");

            BGHBearBonusPelt = cat.CreateEntry("BGHBearBonusPelt", 3,
                display_name: "BGH Bear Bonus — Pelt",
                description: "Bonus pelts (ItemHide — FF calls pelts 'hides' internally) deposited " +
                             "into the killing BGH hunter's cabin when a bear is killed. Stacks on " +
                             "the butcher yield (≈ 2 hide). Default 3 + 2 ≈ 5 pelts/bear. BGH only.");

            BGHBearBonusTallow = cat.CreateEntry("BGHBearBonusTallow", 5,
                display_name: "BGH Bear Bonus — Tallow",
                description: "Bonus tallow (ItemTallow) deposited into the killing BGH hunter's " +
                             "cabin when a bear is killed. Stacks on the butcher yield (≈ 3 tallow). " +
                             "Default 5 + 3 ≈ 8 tallow/bear. BGH only.");

            TrapperWaterBonus = cat.CreateEntry("TrapperWaterBonus", 1.25f,
                display_name: "Trapper Lodge Water Proximity Bonus",
                description: "Pelt yield bonus when a Trapper Lodge is near a water body. " +
                             "Trappers set lines along rivers and streams — historically the richest " +
                             "trapping grounds. 1.25 = 25% more pelt output near water.");

            TrapperWaterBonusRadius = cat.CreateEntry("TrapperWaterBonusRadius", 60f,
                display_name: "Trapper Water Bonus Detection Radius",
                description: "World-unit radius within which a water body triggers the Trapper " +
                             "Lodge water proximity bonus.");

            TrapMasterSpeedMult = cat.CreateEntry("TrapMasterSpeedMult", 1.25f,
                display_name: "Trap Master Speed Multiplier",
                description: "Trap Master traps tick this much faster than vanilla. Stacks with " +
                             "pelt mult to shorten spawn interval. 1.25 = 25% faster trap ticks " +
                             "(a 26-day vanilla interval becomes ~21 days before pelt mult).");

            TrapperWaterTileThreshold = cat.CreateEntry("TrapperWaterTileThreshold", 15,
                display_name: "Trap Master Water Tile Threshold",
                description: "Minimum water tiles within the cabin's hunting radius required " +
                             "to activate the water pelt bonus. Sampled on a grid at path " +
                             "selection time. Maps with extensive lakefront or rivers reward " +
                             "placing the lodge near water. 15 tiles ≈ a moderate lake edge.");

            TrapMasterBearChance = cat.CreateEntry("TrapMasterBearChance", 0.03f,
                display_name: "Trap Master Bear Trap Chance",
                description: "Chance (0.0–1.0) per trap carcass spawn that the Trap Master " +
                             "catches a bear instead of the usual small game. On success, " +
                             "bonus meat/hide/tallow (same amounts as BGH bear bonus config) " +
                             "are deposited directly into the cabin. Passive bear income — " +
                             "no combat required. 0.03 = 3% per catch.");

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

            HunterRallyKeyName = cat.CreateEntry("HunterRallyKey", "Alt+K",
                display_name: "Hunter Rally Hotkey",
                description: "Press this key to rally ALL hunters to the current cursor " +
                             "world position. Addresses the early-game pain of finding and " +
                             "sending each hunter one at a time during raids. Accepts Unity " +
                             "KeyCode names AND modifier combos like 'Alt+R' or 'Ctrl+H'. " +
                             "AVOID single numbers 1-9 (Control Groups), WASD/QE (camera), " +
                             "G F I H Z R B P M (vanilla UI/overlay toggles), and Tab/Space " +
                             "(rotate build / pause). Safe single letters confirmed: K, U. " +
                             "Or use 'Keypad0-9', 'F10', 'F11', or any Alt+letter combo.");

            HunterReturnHomeKeyName = cat.CreateEntry("HunterReturnHomeKey", "F10",
                display_name: "Hunter Return-Home Hotkey",
                description: "Send all hunters back to their assigned cabins. Useful for " +
                             "pulling them out of a fight when raiders escalate. Same key " +
                             "format as the rally key.");

            HunterSelectAllKeyName = cat.CreateEntry("HunterSelectAllKey", "Ctrl+K",
                display_name: "Hunter Select-All Hotkey",
                description: "Selects every hunter into vanilla's multi-selection. Then " +
                             "right-click terrain to move them normally (vanilla click-to-move " +
                             "works on civilians once selected). Preserves existing non-hunter " +
                             "selections if you want to mix.");

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

            // Fishing — mode system
            FishingDockOutputMult = cat.CreateEntry("FishingDockOutputMult", 1.5f,
                display_name: "Fishing Dock Output Multiplier",
                description: "Fish output multiplier at Tier 2 Fishing Dock (separate building). " +
                             "1.5 = 50% more fish. Legacy config for FishingDock buildings.");

            AnglerCatchMult = cat.CreateEntry("AnglerCatchMult", 1.5f,
                display_name: "Angler Catch Multiplier",
                description: "Catch output multiplier for Angler mode workers. Applied per " +
                             "fishing cycle via GetNumFishCaught. 1.5 = 50% more fish per catch.");

            AnglerTimerReduction = cat.CreateEntry("AnglerTimerReduction", 0.65f,
                display_name: "Angler Timer Reduction",
                description: "Timer multiplier for Angler mode. Vanilla timer is 28-32s between " +
                             "catches. 0.65 = 35% faster (~18-21s). Lower = faster fishing cycles.");

            AnglerCapacityBonus = cat.CreateEntry("AnglerCapacityBonus", 15,
                display_name: "Angler Carry Capacity Bonus",
                description: "Extra fish carry capacity for Angler workers. Vanilla = 25. " +
                             "15 bonus = 40 total. Fewer return trips = more time fishing.");

            CreelerRodMult = cat.CreateEntry("CreelerRodMult", 0.5f,
                display_name: "Creeler Rod Fishing Multiplier",
                description: "Rod-fishing output multiplier for Creeler workers. Creelers spend " +
                             "time maintaining traps, reducing their rod output. Real income comes " +
                             "from the daily-tick trap system. 0.5 = 50% reduced rod fishing.");

            CrabTrapSpawnDays = cat.CreateEntry("CrabTrapSpawnDays", 8,
                display_name: "Crab Trap Spawn Interval (days)",
                description: "Days between Creeler trap production ticks. Each Creeler worker slot " +
                             "produces CrabTrapFishPerSpawn fish every this many days, auto-deposited " +
                             "into the shack's storage (abstracting fisherman collection time). " +
                             "Water tile bonus divides this further on water-heavy maps. " +
                             "Default 8 days accounts for collection time; lower = faster income.");

            CrabTrapFishPerSpawn = cat.CreateEntry("CrabTrapFishPerSpawn", 4,
                display_name: "Crab Trap Fish Per Spawn",
                description: "Fish produced per Creeler slot per spawn event. 2 Creeler slots " +
                             "at 4 fish / 5 days = ~584 fish/year passive. Tune down if too strong.");

            CreelerTrapCount = cat.CreateEntry("CreelerTrapCount", 5,
                display_name: "Creeler Trap Count",
                description: "Number of physical creel traps a Creeler-mode fishing shack " +
                             "deploys in water within its fishing radius. Recommended range 4-6. " +
                             "Each trap independently accumulates fish on a 5-day cycle and is " +
                             "harvested by the shack's fishermen.");

            CreelerRadiusMult = cat.CreateEntry("CreelerRadiusMult", 2.0f,
                display_name: "Creeler Radius Multiplier",
                description: "Fishing radius multiplier when any Creeler slot is active. " +
                             "2.0 = 60u (vanilla 30u). Creeler traps spread across wider water. " +
                             "More water tiles in range = better water bonus.");

            CreelerWaterTileThreshold = cat.CreateEntry("CreelerWaterTileThreshold", 15,
                display_name: "Creeler Water Tile Threshold",
                description: "Minimum water tiles within expanded radius to activate the " +
                             "Creeler water bonus. 15 on 8u grid = moderate lake edge. " +
                             "Big deep lakes easily exceed this — the Creeler's home turf.");

            CreelerWaterTileBonus = cat.CreateEntry("CreelerWaterTileBonus", 1.25f,
                display_name: "Creeler Water Tile Bonus",
                description: "Trap spawn interval divisor when water bonus active. " +
                             "1.25 = 25% faster trap ticks. At defaults: 5d / 1.25 = 4d interval.");

            FishReplenishOverride = cat.CreateEntry("FishReplenishOverride", 0.5f,
                display_name: "Fish Replenish Rate (Tech Node)",
                description: "Overrides the Sustainable Fishing tech node's fish replenish " +
                             "rate bonus. Vanilla = 0.3 (30%). Default 0.5 (50%).");

            FishingShackStorageCap = cat.CreateEntry("FishingShackStorageCap", 200,
                display_name: "Fishing Shack Storage Capacity",
                description: "Fish storage capacity per shack. Vanilla = 100. " +
                             "Set to 200 to match vanilla wagon load (~200 fish at weight 2).");

            HunterPathKeyName = cat.CreateEntry("HunterPathKey", "P",
                display_name: "Hunter Lodge Path Cycle Key",
                description: "While a T2 Hunter Lodge is selected, press this key to cycle " +
                             "between Vanilla / Trapper Lodge / Hunting Lodge specialisations. " +
                             "Use Unity KeyCode name (e.g. P, Y, Tab).");

            // ── Parse keybinds ────────────────────────────────────────────────
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
            FishingShackPatches.ApplyPatches(HarmonyInstance);
            FishingShackLoadPatches.Register(HarmonyInstance);

            if (FishingUIEnabled.Value)
                FishingModeSliderPatches.Register(HarmonyInstance);
            else
                Log.Msg("[WotW] FishingModeSliderPatches SKIPPED (FishingUIEnabled=false)");

            if (HunterUIEnabled.Value)
            {
                HunterModeButtonPatches.Register(HarmonyInstance);
                TrapSliderPinPatches.Register(HarmonyInstance);
            }
            else
            {
                Log.Msg("[WotW] HunterUI patches SKIPPED (HunterUIEnabled=false)");
            }

            LocalizationPatches.Apply(HarmonyInstance);

            if (TechTreePatchEnabled.Value)
                TechResearchPatches.Register(HarmonyInstance);
            else
                Log.Msg("[WotW] TechResearchPatches SKIPPED (TechTreePatchEnabled=false)");

            Log.Msg($"[WotW] Warden of the Wilds 0.1.0 loaded." +
                    $" TendedWilds: {TendedWildsActive}" +
                    $" | Hunter: {HunterOverhaulEnabled.Value}" +
                    $" | Fishing: {FishingOverhaulEnabled.Value}");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != "Map") return;

            Log.Msg("[WotW] Map scene loaded — initialising systems.");

            // Reset per-scene state in all systems
            HunterCabinEnhancement.OnMapLoaded();
            FishingShackEnhancement.OnMapLoaded();
            HuntingBlindSystem.OnMapLoaded();
            HunterCombatPatches.OnMapLoaded();

            // Unlock fox + groundhog spawns (override vanilla 1500-day delays)
            SmallGameUnlockSystem.OnMapLoaded();

            // Scale bear/wolf/boar/deer population caps via config multipliers
            AnimalSpawnTuningSystem.OnMapLoaded();

            MelonCoroutines.Start(LateInit());

            // Tech tree modifications (Sustainable Fishing node)
            _techTreePatched = false;
            SustainableFishingResearched = false;
            if (FishingOverhaulEnabled.Value && TechTreePatchEnabled.Value)
                MelonCoroutines.Start(PatchTechTreeDelayed());

        }

        /// <summary>
        /// Per-frame hook. Used for hotkey polling (rally, return-home).
        /// Gated on HunterOverhaulEnabled so a disabled install incurs
        /// zero per-frame cost.
        /// </summary>
        public override void OnUpdate()
        {
            if (!HunterOverhaulEnabled.Value) return;
            if (!HunterRallyEnabled.Value) return;
            HunterRallySystem.Tick();
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

        // ── Tech Tree: Sustainable Fishing ──────────────────────────────────
        private IEnumerator PatchTechTreeDelayed()
        {
            yield return new WaitForSeconds(5f);

            int attempts = 0;
            const int maxAttempts = 600; // 600 × 3s = 30 min
            while (!_techTreePatched && attempts < maxAttempts)
            {
                attempts++;
                bool done = TryPatchTechTree(attempts);
                if (done) break;
                yield return new WaitForSeconds(3f);
            }

            if (!_techTreePatched)
                Log.Error("[WotW] PatchTechTree: Failed to patch Sustainable Fishing after 30 minutes.");
        }

        private bool TryPatchTechTree(int attempt)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var gameManager = GameObject.FindObjectOfType<GameManager>();
                if (gameManager == null) return false;

                var techTreeManager = gameManager.techTreeManager;
                if (techTreeManager == null) return false;

                var nodeDataList = techTreeManager.techTreeNodeData;
                if (nodeDataList == null || nodeDataList.Count == 0)
                {
                    Log.Warning($"[WotW] PatchTechTree: techTreeNodeData empty (attempt {attempt})");
                    return false;
                }

                TechTreeNodeData fishingNode = null;
                foreach (var node in nodeDataList)
                {
                    if (node.GetTechName() == "Sustainable Fishing")
                    {
                        fishingNode = node;
                        break;
                    }
                }

                if (fishingNode == null)
                {
                    Log.Warning($"[WotW] PatchTechTree: 'Sustainable Fishing' not found (attempt {attempt})");
                    return false;
                }

                // ── Check if already researched ──
                int curRank = 0;
                int numRanks = fishingNode.GetNumRanks();
                try
                {
                    var crField = fishingNode.GetType().GetField("<curRank>k__BackingField", F);
                    if (crField != null) curRank = (int)crField.GetValue(fishingNode);
                }
                catch { }
                bool researched = curRank >= numRanks && numRanks > 0;
                SetSustainableFishingResearched(researched);
                Log.Msg($"[WotW] PatchTechTree: Sustainable Fishing — ranks={numRanks}, curRank={curRank}, researched={researched}");

                // ── Bump replenish value ──
                if (fishingNode.gameEffectsEntries != null)
                {
                    var valueField = typeof(GameEffectEntry).GetField("_value", F);
                    foreach (var entry in fishingNode.gameEffectsEntries)
                    {
                        if (entry.gameEffect != null &&
                            entry.gameEffect.GetType().Name == "FishReplenishRateModify")
                        {
                            if (valueField != null)
                            {
                                float oldVal = entry.value;
                                float newVal = FishReplenishOverride.Value;
                                valueField.SetValue(entry, newVal);
                                Log.Msg($"[WotW] PatchTechTree: Fish replenish {oldVal} -> {newVal}");
                            }
                            break;
                        }
                    }
                }

                // ── Register localization tag and set description override ──
                const string descTag = "WotW_SustainableFishingDesc";
                const string g = "<color=#9bff3a>"; // vanilla green highlight
                const string w = "</color>";
                string descText =
                    $"{g}+50%{w} Fish Replenishment\n" +
                    $"Unlocks {g}Angler{w} + {g}Creeler{w}\n" +
                    $"{g}2x{w} Creeler Radius\n" +
                    $"{g}+35%{w} Fishing Speed\n" +
                    $"{g}+50%{w} Carry Capacity";

                LocalizationPatches.Register(descTag, descText);

                var descField = typeof(TechTreeNodeData).GetField("_descriptionTagOverride", F);
                if (descField != null)
                {
                    descField.SetValue(fishingNode, descTag);
                    // Hide the auto-generated "+50% Fish Replenishment" line so it's not duplicated
                    var skipField = typeof(GameEffectEntry).GetField("_skipDescriptionDisplay", F);
                    if (skipField != null && fishingNode.gameEffectsEntries != null)
                    {
                        foreach (var entry in fishingNode.gameEffectsEntries)
                            skipField.SetValue(entry, true);
                    }
                    Log.Msg($"[WotW] PatchTechTree: registered '{descTag}' and set descriptionTagOverride");
                }
                else
                {
                    Log.Warning("[WotW] PatchTechTree: _descriptionTagOverride field not found");
                }

                _techTreePatched = true;
                Log.Msg("[WotW] PatchTechTree: Sustainable Fishing modifications complete.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[WotW] PatchTechTree error (attempt {attempt}): {ex}");
                return false;
            }
        }

        /// <summary>
        /// Defensive re-check of research state. Normally the event-driven
        /// path (TechResearchPatches → SetSustainableFishingResearched)
        /// handles state changes automatically, but callers can invoke this
        /// to force a resync in edge cases.
        /// </summary>
        public static void RefreshFishingTechState()
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                var gm = GameObject.FindObjectOfType<GameManager>();
                if (gm?.techTreeManager == null) return;
                var ttm = gm.techTreeManager;

                var field = ttm.GetType().GetField("geFishReplenishRateMultiplier", F);
                if (field != null)
                {
                    float val = (float)field.GetValue(ttm);
                    SetSustainableFishingResearched(val > 0f);
                }
            }
            catch { }
        }

        private bool TryAttachComponents()
        {
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
            }

            return foundAny;
        }
    }
}
