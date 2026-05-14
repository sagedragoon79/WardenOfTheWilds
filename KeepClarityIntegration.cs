using System;
using System.Reflection;
using MelonLoader;

namespace WardenOfTheWilds
{
    /// <summary>
    /// Optional integration with Keep Clarity's settings panel. If
    /// KeepClarity.dll isn't installed, every method here is a no-op and
    /// WotW runs unchanged. If it IS installed, our prefs show up with rich
    /// labels, tooltips, sliders with proper ranges, sub-categories, and
    /// VisibleWhen gating instead of one flat alphabetical list.
    ///
    /// All access to Keep Clarity is reflective — no compile-time reference,
    /// so this file ships standalone without adding KeepClarity.dll as a
    /// hard build dependency.
    /// </summary>
    internal static class KeepClarityIntegration
    {
        private static bool _resolved;
        private static bool _present;
        private static MethodInfo? _registerMod;
        private static MethodInfo? _registerEntry;
        private static Type? _settingsMetaType;

        private const string ModId = "WardenOfTheWilds";
        private const string ModDisplayName = "Warden of the Wilds";

        public static void TryRegisterAll()
        {
            if (!ResolveApi()) return;
            try
            {
                RegisterMod();
                RegisterEntries();
                MelonLogger.Msg("[WotW] Registered with Keep Clarity settings panel");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] Keep Clarity registration failed: {ex.Message}");
            }
        }

        private static bool ResolveApi()
        {
            if (_resolved) return _present;
            _resolved = true;

            var apiType = Type.GetType("FFUIOverhaul.Settings.SettingsAPI, KeepClarity");
            if (apiType == null) { _present = false; return false; }
            _settingsMetaType = Type.GetType("FFUIOverhaul.Settings.SettingsMeta, KeepClarity");
            if (_settingsMetaType == null) { _present = false; return false; }

            _registerMod = apiType.GetMethod("RegisterMod", BindingFlags.Public | BindingFlags.Static);
            foreach (var m in apiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "Register" && m.IsGenericMethodDefinition) { _registerEntry = m; break; }

            _present = _registerMod != null && _registerEntry != null;
            return _present;
        }

        private static void RegisterMod()
        {
            _registerMod!.Invoke(null, new object?[] {
                ModId,
                ModDisplayName,
                "Hunter overhaul: T2 branching paths, kiting AI, fishing/smokehouse tuning, big-game spawn multipliers.",
                /*version*/ null,
                /*iconResourcePath*/ null,
                /*accentRgb — moss/forest green*/ new[] { 0.30f, 0.50f, 0.25f, 1f },
                /*order*/ 20
            });
        }

        private static object NewMeta(string? label = null, string? tooltip = null,
            object? min = null, object? max = null, string? group = null,
            bool restartRequired = false, int order = 0, Func<bool>? visibleWhen = null)
        {
            var m = Activator.CreateInstance(_settingsMetaType!);
            void Set(string field, object? value)
            {
                var f = _settingsMetaType!.GetField(field);
                if (f != null) f.SetValue(m, value);
            }
            Set("Label", label);
            Set("Tooltip", tooltip);
            Set("Min", min);
            Set("Max", max);
            Set("Group", group);
            Set("RestartRequired", restartRequired);
            Set("Order", order);
            Set("VisibleWhen", visibleWhen);
            return m!;
        }

        private static void Reg<T>(string category, MelonPreferences_Entry<T> entry, object meta)
        {
            var closed = _registerEntry!.MakeGenericMethod(typeof(T));
            closed.Invoke(null, new object?[] { ModId, ModDisplayName, category, entry, meta });
        }

        private static void RegisterEntries()
        {
            var P = typeof(WardenOfTheWildsMod);

            // === Master Toggles ===
            Reg("Master Toggles", WardenOfTheWildsMod.HunterOverhaulEnabled,
                NewMeta("Hunter Overhaul", "T2 branching paths, kiting AI, blind range bonuses", restartRequired: true));
            Reg("Master Toggles", WardenOfTheWildsMod.FishingOverhaulEnabled,
                NewMeta("Fishing Overhaul", "Angler/Creeler modes, Fishing Dock, Sustainable Fishing tech buff", restartRequired: true));
            Reg("Master Toggles", WardenOfTheWildsMod.SmokehouseOverhaulEnabled,
                NewMeta("Smokehouse Overhaul", "Smokehouse radius/worker/storage tuning", restartRequired: true));
            Reg("Master Toggles", WardenOfTheWildsMod.DiagnosticsEnabled,
                NewMeta("Diagnostics Logging", "Verbose logging for development — leave off in normal play"));

            // === Sub-system toggles (gated by master toggles) ===
            Reg("Sub-systems", WardenOfTheWildsMod.HunterCombatEnabled,
                NewMeta("Hunter Combat AI", "Kiting, retreat, multi-predator avoidance — combat-era heavy lifter"));
            Reg("Sub-systems", WardenOfTheWildsMod.HunterRallyEnabled,
                NewMeta("Hunter Rally Hotkeys", "Per-frame keybind polling for rally/return/select-all"));
            Reg("Sub-systems", WardenOfTheWildsMod.HunterUIEnabled,
                NewMeta("Hunter UI Injection", "Big Game Hunter / Trap Master button + slider injection"));
            Reg("Sub-systems", WardenOfTheWildsMod.FishingUIEnabled,
                NewMeta("Fishing UI Injection", "Angler / Creeler mode UI"));
            Reg("Sub-systems", WardenOfTheWildsMod.TechTreePatchEnabled,
                NewMeta("Tech Tree Patch", "Sustainable Fishing value override"));

            // === Smokehouse ===
            Reg("Smokehouse", WardenOfTheWildsMod.SmokehouseRadiusEnabled,
                NewMeta("Custom Work Radius"));
            Reg("Smokehouse", WardenOfTheWildsMod.SmokehouseRadiusEnforce,
                NewMeta("Enforce Radius", "Refuse work outside the configured radius",
                    visibleWhen: () => WardenOfTheWildsMod.SmokehouseRadiusEnabled.Value));
            Reg("Smokehouse", WardenOfTheWildsMod.SmokehouseWorkRadius,
                NewMeta("Work Radius", min: 10f, max: 200f,
                    visibleWhen: () => WardenOfTheWildsMod.SmokehouseRadiusEnabled.Value));
            Reg("Smokehouse", WardenOfTheWildsMod.SmokehouseMaxWorkers, NewMeta("Max Workers", min: 1, max: 4));
            Reg("Smokehouse", WardenOfTheWildsMod.SmokehouseRawMeatStorageCap, NewMeta("Raw Meat Storage Cap", min: 50, max: 1000));
            Reg("Smokehouse", WardenOfTheWildsMod.SmokehouseSmokedMeatStorageCap, NewMeta("Smoked Meat Storage Cap", min: 50, max: 1000));

            // === Hunter Cabin ===
            Reg("Hunter Cabin", WardenOfTheWildsMod.HunterCabinOutputStorageCap,
                NewMeta("Output Buffer Cap", min: 50, max: 500,
                    tooltip: "Per-item capacity in the cabin's manufacturing storage. Higher = more breathing room before stalls."));

            // === Hunting Lodge (T2 path) ===
            Reg("Hunting Lodge", WardenOfTheWildsMod.HuntingLodgeRadiusMult, NewMeta("Radius Multiplier", min: 0.5f, max: 3.0f));
            Reg("Hunting Lodge", WardenOfTheWildsMod.HuntingLodgeMeatMult, NewMeta("Meat Multiplier", min: 0.5f, max: 3.0f));
            Reg("Hunting Lodge", WardenOfTheWildsMod.HuntingLodgeSpeedMult, NewMeta("Off-road Speed Multiplier", min: 0.5f, max: 3.0f));
            Reg("Hunting Lodge", WardenOfTheWildsMod.HuntingLodgeBigGameShootMult,
                NewMeta("Big-game Shoot Interval Mult.", min: 0.25f, max: 2.0f, tooltip: "Lower = faster fire rate vs Boar/Wolf/Bear"));
            Reg("Hunting Lodge", WardenOfTheWildsMod.HuntingLodgeKitingEnabled,
                NewMeta("Kiting AI", "Hunters fall back to Hunting Blind / cabin between shots vs. dangerous prey"));
            Reg("Hunting Lodge", WardenOfTheWildsMod.HuntingLodgeButcherSpeedMult,
                NewMeta("Butcher Speed Multiplier", min: 0.5f, max: 3.0f, tooltip: "Per-worker — combined with 2 workers"));

            // === Trapper Lodge (T2 path) ===
            Reg("Trapper Lodge", WardenOfTheWildsMod.TrapperLodgePeltMult, NewMeta("Pelt Multiplier", min: 0.5f, max: 3.0f));
            Reg("Trapper Lodge", WardenOfTheWildsMod.TrapperLodgeTrapCount, NewMeta("Max Deployed Traps", min: 1, max: 10));
            Reg("Trapper Lodge", WardenOfTheWildsMod.TrapMasterSpeedMult,
                NewMeta("Trap Tick Speed Multiplier", min: 1.0f, max: 3.0f, tooltip: "1.25 = 25% faster trap intervals"));
            Reg("Trapper Lodge", WardenOfTheWildsMod.TrapperWaterBonus,
                NewMeta("Water-adjacent Bonus", min: 1.0f, max: 2.0f));
            Reg("Trapper Lodge", WardenOfTheWildsMod.TrapperWaterBonusRadius,
                NewMeta("Water Bonus Radius", min: 10f, max: 200f));
            Reg("Trapper Lodge", WardenOfTheWildsMod.TrapperWaterTileThreshold,
                NewMeta("Water Tile Threshold", min: 1, max: 100, tooltip: "Min sampled water tiles for the bonus to apply"));
            Reg("Trapper Lodge", WardenOfTheWildsMod.TrapMasterBearChance,
                NewMeta("Bear Trap Chance", min: 0f, max: 0.25f, tooltip: "Per trap-spawn chance the trap catches a bear"));
            Reg("Trapper Lodge", WardenOfTheWildsMod.TrapperLodgeButcherSpeedMult,
                NewMeta("Butcher Speed Multiplier", min: 0.5f, max: 3.0f));

            // === Big Game / Yields ===
            Reg("Big Game Yields", WardenOfTheWildsMod.BearMegaYieldMult,
                NewMeta("Bear Yield Multiplier", min: 0.5f, max: 5.0f, tooltip: "Stacks on top of other bonuses for bear carcasses"));
            Reg("Big Game Yields", WardenOfTheWildsMod.BGHBearBonusMeat, NewMeta("BGH Bear Bonus Meat", min: 0, max: 500));
            Reg("Big Game Yields", WardenOfTheWildsMod.BGHBearBonusPelt, NewMeta("BGH Bear Bonus Pelts", min: 0, max: 20));
            Reg("Big Game Yields", WardenOfTheWildsMod.BGHBearBonusTallow, NewMeta("BGH Bear Bonus Tallow", min: 0, max: 20));

            // === Animal Spawn Multipliers ===
            Reg("Spawn Multipliers", WardenOfTheWildsMod.BearSpawnMultiplier, NewMeta("Bear Population", min: 0.25f, max: 3.0f));
            Reg("Spawn Multipliers", WardenOfTheWildsMod.WolfSpawnMultiplier, NewMeta("Wolf Population", min: 0.25f, max: 3.0f));
            Reg("Spawn Multipliers", WardenOfTheWildsMod.BoarSpawnMultiplier, NewMeta("Boar Population", min: 0.25f, max: 3.0f));
            Reg("Spawn Multipliers", WardenOfTheWildsMod.DeerSpawnMultiplier, NewMeta("Deer Population", min: 0.25f, max: 3.0f));

            // === Hunter AI / Survival ===
            Reg("Hunter AI", WardenOfTheWildsMod.HunterLowHealthThreshold,
                NewMeta("Retreat Health %", min: 0f, max: 1f, tooltip: "Disengage below this fraction of max HP"));
            Reg("Hunter AI", WardenOfTheWildsMod.HunterMinArrows, NewMeta("Min Arrows Before Hunt", min: 0, max: 50));
            Reg("Hunter AI", WardenOfTheWildsMod.HunterDangerRadius, NewMeta("Danger Awareness Radius", min: 10f, max: 100f));
            Reg("Hunter AI", WardenOfTheWildsMod.HunterShelterSearchRadius,
                NewMeta("Shelter Threat Scan Radius", min: 30f, max: 150f, tooltip: "Hunter waits in shelter until threats leave this radius"));
            Reg("Hunter AI", WardenOfTheWildsMod.T1MeleeThreshold,
                NewMeta("T1 Melee HP Threshold", min: 0f, max: 1f, tooltip: "T1 hunter commits to melee below this target HP %"));
            Reg("Hunter AI", WardenOfTheWildsMod.HunterPursuitLeashMult,
                NewMeta("Pursuit Leash Multiplier", min: 1.0f, max: 3.0f, tooltip: "Break off chase beyond (cabin radius × this)"));
            Reg("Hunter AI", WardenOfTheWildsMod.HunterAmbushScanRadius,
                NewMeta("Ambush Scan Radius", min: 10f, max: 100f));
            Reg("Hunter AI", WardenOfTheWildsMod.HunterAmbushThreshold,
                NewMeta("Ambush Threshold", min: 1, max: 99, tooltip: "Hostile count near hunter that triggers retreat"));
            Reg("Hunter AI", WardenOfTheWildsMod.BowReloadSeconds, NewMeta("Bow Reload (seconds)", min: 0.5f, max: 10f));
            Reg("Hunter AI", WardenOfTheWildsMod.CrossbowReloadSeconds, NewMeta("Crossbow Reload (seconds)", min: 0.5f, max: 10f));

            // === Hunting Blind ===
            Reg("Hunting Blind", WardenOfTheWildsMod.HuntingBlindRangeBonus,
                NewMeta("Range Bonus (tiles)", min: 0f, max: 30f));
            Reg("Hunting Blind", WardenOfTheWildsMod.HuntingBlindGoldUpkeep,
                NewMeta("Gold Upkeep", min: 0f, max: 5f));

            // === Fishing — Angler ===
            Reg("Fishing — Angler", WardenOfTheWildsMod.AnglerCatchMult,
                NewMeta("Catch Multiplier", min: 0.5f, max: 3.0f));
            Reg("Fishing — Angler", WardenOfTheWildsMod.AnglerTimerReduction,
                NewMeta("Catch Timer Multiplier", min: 0.25f, max: 1.5f, tooltip: "Lower = faster catches"));
            Reg("Fishing — Angler", WardenOfTheWildsMod.AnglerCapacityBonus,
                NewMeta("Carry Capacity Bonus", min: 0, max: 100));

            // === Fishing — Creeler ===
            Reg("Fishing — Creeler", WardenOfTheWildsMod.CreelerRodMult,
                NewMeta("Rod Output Multiplier", min: 0f, max: 1.5f));
            Reg("Fishing — Creeler", WardenOfTheWildsMod.CrabTrapSpawnDays,
                NewMeta("Trap Spawn Interval (days)", min: 1, max: 30));
            Reg("Fishing — Creeler", WardenOfTheWildsMod.CrabTrapFishPerSpawn,
                NewMeta("Fish per Trap Spawn", min: 1, max: 20));
            Reg("Fishing — Creeler", WardenOfTheWildsMod.CreelerTrapCount,
                NewMeta("Deployed Trap Count", min: 1, max: 10));
            Reg("Fishing — Creeler", WardenOfTheWildsMod.CreelerRadiusMult,
                NewMeta("Fishing Radius Multiplier", min: 1.0f, max: 3.0f));
            Reg("Fishing — Creeler", WardenOfTheWildsMod.CreelerWaterTileThreshold,
                NewMeta("Water Tile Threshold", min: 1, max: 100));
            Reg("Fishing — Creeler", WardenOfTheWildsMod.CreelerWaterTileBonus,
                NewMeta("Water Bonus Multiplier", min: 1.0f, max: 2.0f));

            // === Fishing — Shared / Tech ===
            Reg("Fishing — Shared", WardenOfTheWildsMod.FishingDockOutputMult,
                NewMeta("Fishing Dock Output", min: 0.5f, max: 3.0f));
            Reg("Fishing — Shared", WardenOfTheWildsMod.FishReplenishOverride,
                NewMeta("Sustainable Fishing %", min: 0f, max: 1f, tooltip: "Replenish rate from the tech node (vanilla 0.30)"));
            Reg("Fishing — Shared", WardenOfTheWildsMod.FishingShackStorageCap,
                NewMeta("Shack Storage Cap", min: 50, max: 500));

            // === Hotkeys ===
            Reg("Hotkeys", WardenOfTheWildsMod.HunterRallyKeyName,
                NewMeta("Rally Hunters", "Unity KeyCode name (G, H, R, etc.)"));
            Reg("Hotkeys", WardenOfTheWildsMod.HunterReturnHomeKeyName,
                NewMeta("Hunters Return Home"));
            Reg("Hotkeys", WardenOfTheWildsMod.HunterSelectAllKeyName,
                NewMeta("Select All Hunters"));
            Reg("Hotkeys", WardenOfTheWildsMod.HunterPathKeyName,
                NewMeta("Cycle T2 Path", "Cycle Vanilla / Trapper / Hunting Lodge while a T2 cabin is selected"));
        }
    }
}
