using System;
using System.Collections.Generic;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // Authored gadget catalog converted into a validated Core
    // GadgetDefinitionCatalog. Data only — authoritative behavior is
    // Task 5; this asset pins the authored numbers.
    [CreateAssetMenu(menuName = "FamilyRacing/Gadget Catalog",
        fileName = "gadget-catalog")]
    public sealed class GadgetCatalogAsset : ScriptableObject
    {
        [Serializable]
        public sealed class GadgetRow
        {
            public string gadgetId = "";
            // One of the recognized GadgetKind values.
            public string kind = "";
            // Turbo.
            public int durationTicks;
            public float accelerationMultiplier = 1f;
            public float speedCapMultiplier = 1f;
            // Bubble shield.
            public int charges;
            // Foam trap.
            public float patchRadiusMeters;
            public int patchLifetimeTicks;
            public float slowMultiplier = 1f;
            public int slowDurationTicks;
            public int immunityTicks;
            public float projectileSpeedMetersPerSecond = 45f;
            public float targetingRangeMeters = 70f;
            public float turnRadiansPerSecond = 3f;
            public int armingTicks = 18;
        }

        public int pickupRespawnTicks = 300;
        public int heldSlotCapacity = 1;
        public GadgetRow[] gadgets = new GadgetRow[0];

        public GadgetDefinitionCatalog ToCatalog(out string error)
        {
            var catalog = new GadgetDefinitionCatalog
            {
                pickupRespawnTicks = pickupRespawnTicks,
                heldSlotCapacity = heldSlotCapacity
            };
            if (gadgets != null)
                foreach (var row in gadgets)
                {
                    if (row == null)
                    {
                        error = name + ": null gadget row";
                        return null;
                    }
                    catalog.gadgets.Add(new GadgetDefinition
                    {
                        id = row.gadgetId,
                        kind = row.kind,
                        durationTicks = row.durationTicks,
                        accelerationMultiplier = row.accelerationMultiplier,
                        speedCapMultiplier = row.speedCapMultiplier,
                        charges = row.charges,
                        patchRadiusMeters = row.patchRadiusMeters,
                        patchLifetimeTicks = row.patchLifetimeTicks,
                        slowMultiplier = row.slowMultiplier,
                        slowDurationTicks = row.slowDurationTicks,
                        immunityTicks = row.immunityTicks,
                        projectileSpeedMetersPerSecond = row.projectileSpeedMetersPerSecond,
                        targetingRangeMeters = row.targetingRangeMeters,
                        turnRadiansPerSecond = row.turnRadiansPerSecond,
                        armingTicks = row.armingTicks
                    });
                }
            if (!catalog.Validate(out error))
            {
                error = name + ": invalid gadget field " + error;
                return null;
            }
            error = null;
            return catalog;
        }
    }
}
