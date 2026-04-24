using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  CreelerTrapMarker
//
//  Attached to AnimalTrapResource instances that were spawned by a
//  FishingShack in Creeler mode. Harmony patches and task routing check for
//  this component to distinguish Creeler traps from hunter traps (which use
//  the same prefab).
//
//  Phase 1: simple marker with a back-reference to the owning shack so teardown
//  can find its own traps. Later phases will extend this (fish count, last
//  harvest day, etc.) as the production + collection paths come online.
// ─────────────────────────────────────────────────────────────────────────────

namespace WardenOfTheWilds.Components
{
    public class CreelerTrapMarker : MonoBehaviour
    {
        /// <summary>
        /// The FishingShack GameObject that placed this trap. Kept as a plain
        /// Component reference (not a typed FishingShack) so this component
        /// can live in the mod without a compile-time dependency on any
        /// specific field of FishingShack beyond what's already referenced
        /// elsewhere in the mod.
        /// </summary>
        public Component OwnerShack { get; set; }
    }
}
