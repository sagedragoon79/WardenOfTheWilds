using UnityEngine;

namespace WardenOfTheWilds.Components
{
    /// <summary>
    /// Marker MonoBehaviour attached to Fox instances WotW spawns as
    /// roaming wildlife (not as DLC chicken-coop raiders). Used by
    /// <see cref="WardenOfTheWilds.Patches.WildFoxDespawnPatch"/> to
    /// distinguish a wild fox from a vanilla chicken-raid fox so that
    /// the despawn-when-done behavior only affects vanilla raiders,
    /// leaving WotW's wildlife population stable.
    ///
    /// No state — presence is the signal.
    /// </summary>
    public class WotWWildFoxMarker : MonoBehaviour { }
}
