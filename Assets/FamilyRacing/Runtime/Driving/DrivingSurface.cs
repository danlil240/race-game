using UnityEngine;

namespace FamilyRacing
{
    // Collision-authored surface, independent of scenery names/material tint.
    // Paved meshes omit this marker; grass, sand and gravel carry it.
    public sealed class DrivingSurface : MonoBehaviour
    {
        public bool offRoad = true;
    }
}
