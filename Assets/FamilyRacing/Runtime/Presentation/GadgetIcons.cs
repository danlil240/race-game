using System.Collections.Generic;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // Resources keeps the original artwork available in every build flavor.
    public static class GadgetIcons
    {
        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        public static Texture2D TextureFor(string kind)
        {
            if (!GadgetKind.IsValid(kind)) return null;
            if (!textures.TryGetValue(kind, out var texture))
            {
                texture = Resources.Load<Texture2D>("GadgetIcons/" + kind);
                textures[kind] = texture;
            }
            return texture;
        }
    }
}
