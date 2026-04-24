using System.Collections.Generic;
using HarmonyLib;

namespace WardenOfTheWilds.Patches
{
    /// <summary>
    /// Intercepts LocalizationManager.Localize() for WotW's custom tooltip tags.
    /// Tech tree nodes resolve their _descriptionTagOverride through this manager,
    /// so we register fake tags and return formatted text (with rich-text color
    /// tags for vanilla-style green number highlights).
    /// </summary>
    public static class LocalizationPatches
    {
        /// <summary>Map of WotW custom tag keys → localized text with rich-text formatting.</summary>
        public static readonly Dictionary<string, string> WotWTags = new Dictionary<string, string>();

        /// <summary>Tag prefix used for all WotW custom entries.</summary>
        public const string TagPrefix = "WotW_";

        /// <summary>
        /// Registers a tag → text mapping. Call this once during tech tree patching.
        /// </summary>
        public static void Register(string tag, string text)
        {
            WotWTags[tag] = text;
        }

        /// <summary>
        /// Applies the Harmony patches. Called once on mod init.
        /// </summary>
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                var lmType = AccessTools.TypeByName("LocalizationManager");
                if (lmType == null)
                {
                    WardenOfTheWildsMod.Log.Warning("[WotW] LocalizationPatches: LocalizationManager type not found.");
                    return;
                }

                // Localize(string tag) — the single-arg overload used for simple tag lookups
                var localizeMethod = AccessTools.Method(lmType, "Localize", new[] { typeof(string) });
                if (localizeMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(LocalizationPatches), nameof(LocalizePrefix));
                    harmony.Patch(localizeMethod, prefix: prefix);
                    WardenOfTheWildsMod.Log.Msg("[WotW] LocalizationPatches: patched LocalizationManager.Localize(string).");
                }
                else
                {
                    WardenOfTheWildsMod.Log.Warning("[WotW] LocalizationPatches: Localize(string) not found.");
                }

                // IsLocalized(string tag) — the tooltip builder checks this before calling Localize
                var isLocalizedMethod = AccessTools.Method(lmType, "IsLocalized", new[] { typeof(string) });
                if (isLocalizedMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(LocalizationPatches), nameof(IsLocalizedPrefix));
                    harmony.Patch(isLocalizedMethod, prefix: prefix);
                    WardenOfTheWildsMod.Log.Msg("[WotW] LocalizationPatches: patched LocalizationManager.IsLocalized(string).");
                }
            }
            catch (System.Exception ex)
            {
                WardenOfTheWildsMod.Log.Error($"[WotW] LocalizationPatches.Apply error: {ex}");
            }
        }

        /// <summary>Prefix for Localize(string tag). Returns our text and skips original if tag is ours.</summary>
        private static bool LocalizePrefix(string tag, ref string __result)
        {
            if (!string.IsNullOrEmpty(tag) && tag.StartsWith(TagPrefix) &&
                WotWTags.TryGetValue(tag, out var text))
            {
                __result = text;
                return false; // skip original
            }
            return true;
        }

        /// <summary>Prefix for IsLocalized(string tag). Returns true for our tags so the game calls Localize.</summary>
        private static bool IsLocalizedPrefix(string tag, ref bool __result)
        {
            if (!string.IsNullOrEmpty(tag) && tag.StartsWith(TagPrefix) &&
                WotWTags.ContainsKey(tag))
            {
                __result = true;
                return false;
            }
            return true;
        }
    }
}
