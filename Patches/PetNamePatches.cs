using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Auto-names new DLC dogs and cats from curated lists instead of the
    /// generic "Dog"/"Cat" default.
    ///
    /// Hook: Cat.Awake / Dog.Awake set base.petName to the localized species
    /// name ("Cat"/"Dog"). We postfix those to assign a random name from the
    /// matching list.
    ///
    /// Save/load safety: for a LOADED pet, vanilla Cat/Dog.Load runs AFTER
    /// Awake and restores the saved petName — so our random name is only
    /// effective for NEWLY created pets. Existing pets (and player-renamed
    /// ones) keep their names. No save-format change; petName already
    /// serializes.
    ///
    /// Gated by PetsDlcActive (DLC owned + WotW pref) and a dedicated
    /// AutoNamePets pref. Pets exist only with the DLC, and pacifist
    /// "Disable Pests" mode doesn't remove pets, so we use PetsDlcActive
    /// (not PestGameplayActive).
    /// </summary>
    internal static class PetNamePatches
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static PropertyInfo _petNameProp;
        private static bool _propTried;
        private static bool _registered;

        // Curated name pools — recognizable, lightly period-flavored to fit
        // FF's medieval setting. Tunable; duplicates across pets are fine.
        private static readonly string[] DogNames =
        {
            "Rex", "Bran", "Fang", "Duke", "Scout", "Hunter", "Bear", "Shadow",
            "Bayard", "Greyfell", "Bruno", "Maple", "Willow", "Ranger", "Bandit",
            "Gunnar", "Hound", "Ash", "Boomer", "Cedar", "Dane", "Finn", "Garm",
            "Jasper", "Koda", "Loki", "Moose", "Oak", "Otis", "Pike", "Rook",
            "Rye", "Saxon", "Thane", "Tucker", "Wulf", "Birch", "Flint", "Hazel"
        };

        private static readonly string[] CatNames =
        {
            "Smoke", "Mittens", "Pip", "Soot", "Tabby", "Clover", "Nutmeg",
            "Willow", "Misty", "Ember", "Ash", "Boots", "Cinder", "Dusty",
            "Fennel", "Ginger", "Hazel", "Ivy", "Juniper", "Lily", "Moss",
            "Olive", "Pepper", "Poppy", "Saffron", "Sage", "Thistle", "Briar",
            "Marigold", "Nettle", "Pumpkin", "Rye", "Sorrel", "Tansy", "Wren"
        };

        public static void Register(HarmonyLib.Harmony harmony)
        {
            if (_registered) return;
            try
            {
                PatchAwake(harmony, "Cat", nameof(CatAwakePostfix));
                PatchAwake(harmony, "Dog", nameof(DogAwakePostfix));
                _registered = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PetNamePatches.Register: {ex.Message}");
            }
        }

        private static void PatchAwake(HarmonyLib.Harmony harmony, string typeName, string postfixName)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null)
            {
                MelonLogger.Msg(
                    $"[WotW] PetNamePatches: {typeName} type not found (pre-DLC build?). Skipping.");
                return;
            }

            // Awake is protected/instance on Cat/Dog.
            var awake = AccessTools.Method(t, "Awake");
            if (awake == null)
            {
                MelonLogger.Warning($"[WotW] PetNamePatches: {typeName}.Awake not found.");
                return;
            }

            harmony.Patch(awake,
                postfix: new HarmonyMethod(typeof(PetNamePatches), postfixName));
            MelonLogger.Msg($"[WotW] PetNamePatches: patched {typeName}.Awake (auto-naming).");
        }

        public static void DogAwakePostfix(object __instance) => ApplyName(__instance, DogNames);
        public static void CatAwakePostfix(object __instance) => ApplyName(__instance, CatNames);

        private static void ApplyName(object pet, string[] pool)
        {
            try
            {
                if (pet == null || pool == null || pool.Length == 0) return;
                if (!WardenOfTheWildsMod.AutoNamePets.Value) return;
                if (!Systems.DlcDetection.PetsDlcActive) return;

                // Resolve the petName property once (it's on the Pet base class).
                if (!_propTried)
                {
                    _propTried = true;
                    Type t = pet.GetType();
                    while (t != null && _petNameProp == null)
                    {
                        _petNameProp = t.GetProperty("petName", AllInstance);
                        t = t.BaseType;
                    }
                }
                if (_petNameProp == null || !_petNameProp.CanWrite) return;

                string name = pool[UnityEngine.Random.Range(0, pool.Length)];
                _petNameProp.SetValue(pet, name);
                // Note: for a loaded pet, vanilla Load runs after this and
                // restores the saved name — so this only sticks for new pets.
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[WotW] PetNamePatches.ApplyName: {ex.Message}");
            }
        }
    }
}
