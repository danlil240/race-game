using UnityEngine;

namespace FamilyRacing
{
    // Authored animal racer (M4 Task 4): one of the four supported
    // profile avatars (fox/rabbit/panda/raccoon) as a seated prefab.
    // All four prefabs share one skeleton/mount naming convention so a
    // single RacerVisualRig poses any of them:
    //   Rig/Hips            — sits on the vehicle's SeatMount
    //   Rig/Hips/LegL|LegR  — stubby legs, posed per vehicle class
    //   Rig/Hips/Tail       — species signature
    //   Rig/Hips/Spine/ArmL|ArmR — aimed at the vehicle grips each frame
    //   Rig/Hips/Spine/Head — chunky head carrying the species parts
    //   Rig/Hips/Spine/Scarf — accent band tinted by colorIndex
    // Limbs are single-piece capsules (the toy-like proportions of the
    // art guide); signature parts (ears, muzzle, eye patches, tail shape)
    // hang off the named nodes. No colliders anywhere in the prefab.
    [CreateAssetMenu(menuName = "FamilyRacing/Character Definition",
        fileName = "character-def")]
    public sealed class CharacterDefinitionAsset : ScriptableObject
    {
        // Matches the profile avatar id exactly (ProfileValidation's
        // fox/rabbit/panda/raccoon) — that identity is how solo and LAN
        // cosmetics resolve to this asset.
        public string characterId = "fox";
        public GameObject prefab;

        public bool Validate(out string error)
        {
            if (string.IsNullOrEmpty(characterId))
            {
                error = name + ": character definition has no characterId";
                return false;
            }
            if (prefab == null)
            {
                error = name + ": character '" + characterId
                    + "' has no prefab";
                return false;
            }
            error = null;
            return true;
        }
    }
}
