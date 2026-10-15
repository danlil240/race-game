using System;
using System.Collections.Generic;
using FamilyRacing.Core.Progression;

namespace FamilyRacing.Core.Profiles
{
    // The terminal verdict of one purchase or equipment attempt. `status`
    // owns the four-state contract (applied | alreadyApplied | rejected |
    // failed); `code` is the stable machine reason; `committed` is true
    // only when durable facts were committed (an idempotent replay counts
    // — the facts are durable even though nothing new was written).
    public sealed class PurchaseResult
    {
        public string status = "";
        public string code = "";
        public bool committed;
        public string purchaseId = "";
        public string vehicleId = "";
        public string category = "";
        public string itemId = "";
        public int level;
        public int costCoins;
    }

    // M5 Task 8: the durable purchase transaction and cosmetic
    // equipment. One purchase commits — inside ONE atomic profile save —
    // the authored coin debit, the per-vehicle category level and the
    // PurchaseReceipt that owns the purchaseId forever. The receipt is
    // consulted BEFORE any other validation: an identical replay is the
    // idempotent no-op, a differing tuple under an owned id is the
    // conflicting retry that rejects even when the money is there.
    // Every refusal happens before the candidate is built, so a rejected
    // purchase provably mutates neither the live profile nor disk; a
    // failed save leaves both byte-identical and the purchaseId free
    // for retry.
    public static class UpgradeService
    {
        public const string StatusApplied = "applied";
        public const string StatusAlreadyApplied = "alreadyApplied";
        public const string StatusRejected = "rejected";
        public const string StatusFailed = "failed";

        public static PurchaseResult Purchase(string purchaseId,
            string vehicleId, string category, int expectedLevel,
            ProfileSave profile, ProfileStore store,
            GameplayCatalog catalog)
        {
            var result = new PurchaseResult
            {
                purchaseId = purchaseId ?? "",
                vehicleId = vehicleId ?? "",
                category = category ?? ""
            };
            if (profile == null)
            {
                result.status = StatusRejected;
                result.code = "requiresSelectedProfile";
                return result;
            }
            if (store == null)
            {
                result.status = StatusRejected;
                result.code = "invalidRequest";
                return result;
            }
            if (catalog == null || catalog.IsLegacy
                || catalog.Driving == null || catalog.Economy == null)
            {
                result.status = StatusRejected;
                result.code = "contentUnavailable";
                return result;
            }
            if (string.IsNullOrEmpty(purchaseId))
            {
                result.status = StatusRejected;
                result.code = "invalidPurchaseId";
                return result;
            }

            // The durable receipt owns this purchaseId. Check it before
            // any freshness validation: the stored tuple decides whether
            // this is a replay (already-applied) or a conflicting retry —
            // the current wallet/levels are irrelevant to both answers.
            var receipt = FindReceipt(profile, purchaseId);
            if (receipt != null)
            {
                if (receipt.kind == "upgrade"
                    && receipt.vehicleId == vehicleId
                    && receipt.category == category
                    && receipt.level == expectedLevel)
                {
                    result.status = StatusAlreadyApplied;
                    result.committed = true;
                    result.level = receipt.level;
                    result.costCoins = receipt.costCoins;
                    return result;
                }
                result.status = StatusRejected;
                result.code = "conflict";
                return result;
            }

            if (string.IsNullOrEmpty(vehicleId)
                || !catalog.Driving.Vehicles.ContainsKey(vehicleId))
            {
                result.status = StatusRejected;
                result.code = "unknownVehicle";
                return result;
            }
            // Owned vehicles only: upgrades spend on what the profile
            // actually holds, never on a locked tier.
            if (profile.unlockedVehicles == null
                || !profile.unlockedVehicles.Contains(vehicleId))
            {
                result.status = StatusRejected;
                result.code = "vehicleLocked";
                return result;
            }
            var definition = catalog.Economy.Category(category);
            var row = FindUpgrade(profile, vehicleId);
            // A category the schema has no level counter for is as
            // unknown as a category absent from the economy.
            var current = row != null ? row.LevelFor(category) : 0;
            if (definition == null || current < 0)
            {
                result.status = StatusRejected;
                result.code = "unknownCategory";
                return result;
            }
            // Only the exact next level may be purchased: a retry that
            // believes the wallet is stale refuses on the level
            // progression itself, independent of receipts.
            if (expectedLevel != current + 1)
            {
                result.status = StatusRejected;
                result.code = "staleExpectedLevel";
                return result;
            }
            if (expectedLevel > definition.maxLevel
                || definition.prices == null
                || expectedLevel > definition.prices.Count)
            {
                result.status = StatusRejected;
                result.code = "levelCap";
                return result;
            }
            var cost = definition.prices[expectedLevel - 1];
            if (profile.coins < cost)
            {
                result.status = StatusRejected;
                result.code = "insufficientCoins";
                return result;
            }

            // The commit: debit + level + receipt inside ONE candidate
            // save. The live profile is touched only after the save
            // commits, so a failed save leaves both untouched and the
            // purchaseId usable for the honest retry.
            var candidate = ProfileCopy.DeepCopy(profile);
            candidate.coins -= cost;
            var candidateRow = FindUpgrade(candidate, vehicleId);
            if (candidateRow == null)
            {
                candidateRow = new VehicleUpgrade { vehicleId = vehicleId };
                candidate.vehicleUpgrades.Add(candidateRow);
            }
            candidateRow.SetLevel(category, expectedLevel);
            candidate.purchaseReceipts.Add(new PurchaseReceipt
            {
                purchaseId = purchaseId,
                kind = "upgrade",
                vehicleId = vehicleId,
                category = category,
                itemId = "",
                level = expectedLevel,
                costCoins = cost
            });
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                result.status = StatusFailed;
                result.code = "saveFailed";
                return result;
            }
            ProfileCopy.CopyProgression(candidate, profile);
            result.status = StatusApplied;
            result.committed = true;
            result.level = expectedLevel;
            result.costCoins = cost;
            return result;
        }

        // Cosmetic equipment: equip checks ownership (cosmetics are
        // earned, never bought — no coin path exists) and replaces
        // within the cosmetic's own slot (the id prefix before its
        // first '-', the same convention vehicle classes use); unequip
        // removes only the named item. The write rides the same one-
        // atomic-save contract purchases use.
        public static PurchaseResult SetCosmeticEquipped(
            ProfileSave profile, string cosmeticId, bool equipped,
            ProfileStore store)
        {
            var result = new PurchaseResult
            {
                itemId = cosmeticId ?? ""
            };
            if (profile == null)
            {
                result.status = StatusRejected;
                result.code = "requiresSelectedProfile";
                return result;
            }
            if (store == null)
            {
                result.status = StatusRejected;
                result.code = "invalidRequest";
                return result;
            }
            if (string.IsNullOrEmpty(cosmeticId))
            {
                result.status = StatusRejected;
                result.code = "invalidParameter";
                return result;
            }
            if (equipped
                && (profile.ownedCosmetics == null
                    || !profile.ownedCosmetics.Contains(cosmeticId)))
            {
                result.status = StatusRejected;
                result.code = "cosmeticLocked";
                return result;
            }

            var candidate = ProfileCopy.DeepCopy(profile);
            var slot = CosmeticSlot(cosmeticId);
            var next = new List<string>();
            if (candidate.equippedCosmetics != null)
                foreach (var item in candidate.equippedCosmetics)
                {
                    if (item == null) continue;
                    // Same-slot items are replaced by an equip; the named
                    // item is removed by either path.
                    if (item == cosmeticId) continue;
                    if (equipped && CosmeticSlot(item) == slot) continue;
                    next.Add(item);
                }
            if (equipped) next.Add(cosmeticId);

            // No change means the facts are already durable — a repeated
            // equip or an unequip of an absent item writes nothing.
            if (candidate.equippedCosmetics != null
                && next.Count == candidate.equippedCosmetics.Count
                && SameItems(next, candidate.equippedCosmetics))
            {
                result.status = StatusApplied;
                result.committed = true;
                return result;
            }
            candidate.equippedCosmetics = next;
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                result.status = StatusFailed;
                result.code = "saveFailed";
                return result;
            }
            ProfileCopy.CopyProgression(candidate, profile);
            result.status = StatusApplied;
            result.committed = true;
            return result;
        }

        // Cosmetic ids carry their slot before the first '-' — paint-*,
        // accessory-*, livery-* — matching the vehicle-class convention.
        public static string CosmeticSlot(string cosmeticId)
        {
            if (string.IsNullOrEmpty(cosmeticId)) return "";
            var dash = cosmeticId.IndexOf('-');
            return dash < 0 ? cosmeticId : cosmeticId.Substring(0, dash);
        }

        private static bool SameItems(List<string> a, List<string> b)
        {
            for (var i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static PurchaseReceipt FindReceipt(ProfileSave profile,
            string purchaseId)
        {
            if (profile.purchaseReceipts != null)
                foreach (var row in profile.purchaseReceipts)
                    if (row != null && row.purchaseId == purchaseId)
                        return row;
            return null;
        }

        private static VehicleUpgrade FindUpgrade(ProfileSave profile,
            string vehicleId)
        {
            if (profile.vehicleUpgrades != null)
                foreach (var row in profile.vehicleUpgrades)
                    if (row != null && row.vehicleId == vehicleId)
                        return row;
            return null;
        }
    }
}
