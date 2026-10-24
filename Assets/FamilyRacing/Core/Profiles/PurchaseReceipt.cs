using System;

namespace FamilyRacing.Core.Profiles
{
    // Durable proof that one purchase was charged to this profile. The
    // receipt is written inside the same atomic profile save as the
    // granted goods and the coin debit (Task 8 owns the transaction), so
    // a crash can never produce a charge without goods or goods without
    // a charge. Retries of the same purchaseId compare the stored tuple:
    // identical means already-applied, different means a conflicting
    // retry that must be rejected even after a process restart.
    [Serializable]
    public sealed class PurchaseReceipt
    {
        public string purchaseId = "";
        // "upgrade" or "cosmetic" — Task 8 owns the closed set.
        public string kind = "";
        // Upgrade purchases: the vehicle and category the level was
        // bought on. Cosmetic purchases carry "" here and the cosmetic
        // id in `itemId`.
        public string vehicleId = "";
        public string category = "";
        public string itemId = "";
        // Upgrade purchases: the granted level; 0 for cosmetics.
        public int level;
        public int costCoins;

        public PurchaseReceipt Copy()
        {
            return (PurchaseReceipt)MemberwiseClone();
        }
    }
}
