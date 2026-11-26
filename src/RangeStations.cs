using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    public enum RangeKind { None, Oven, Burner, Both }

    /// <summary>
    /// Which part of the Kitchen Range a food belongs in: the oven slots (baked, roasted, dried), the burners
    /// (fried, sauteed, boiled) or either. Foods not in the table follow what they can do: an item that only
    /// bakes goes in the oven, an item that only cooks goes on a burner, and an item that does both can go in either.
    /// </summary>
    public static class RangeStations
    {
        static readonly Dictionary<string, RangeKind> Table = Build();

        static Dictionary<string, RangeKind> Build()
        {
            var t = new Dictionary<string, RangeKind>();
            void Add(RangeKind k, params string[] keys) { foreach (string key in keys) t[key] = k; }

            // Base game foods that Expanded Foods lets you bake as well as fry
            Add(RangeKind.Both, "redmeat", "bushmeat", "poultry", "fish", "saltedmeat", "vegetable",
                "catttailroot", "papyrusroot", "tuleroot", "agedmeat", "ribs", "bisonleg", "cockatrice-tail");
            Add(RangeKind.Burner, "pickledvegetable", "mushroom", "grain", "choppedvegetable", "choppedmushroom",
                "fishnugget", "soyprep", "meatnugget", "saltedmeatnugget", "gozinaki", "crabnugget", "snakenugget",
                "sausage", "sausagefish", "sausagesnake", "sausagecrab", "dumpling", "acorndumpling",
                "breadedmushroom", "breadedfishnugget", "breadedmeatnugget", "breadedvegetable", "breadedball",
                "acornbreadedfishnugget", "acornbreadedvegetable", "acornbreadedmushroom", "acornbreadedball",
                "acornbreadedmeatnugget");
            Add(RangeKind.Oven, "fruit", "legume", "acorns", "bread", "dough", "driedseaweed", "fruitleather",
                "pasta", "acornpasta", "hardtack", "acornhardtack", "plaindoughball", "acorndoughball",
                "fishpemmican", "pemmican", "crabpemmican", "snakepemmican", "berrybread", "acornberrybread",
                "muffin", "acornmuffin", "stuffedpepper", "acornstuffedpepper", "acornportion", "saltcrystal");
            return t;
        }

        /// <summary>Where this food belongs, by the longest code prefix in the table, else by what it can do.</summary>
        public static RangeKind Of(ItemStack stack)
        {
            string path = stack?.Collectible?.Code?.Path;
            if (path == null) return RangeKind.None;

            string key = path;
            while (true)
            {
                RangeKind k;
                if (Table.TryGetValue(key, out k)) return k;
                int i = key.LastIndexOf('-');
                if (i < 0) break;
                key = key.Substring(0, i);
            }

            bool bake = InventoryRange.IsBakeable(stack);
            bool cook = InventoryRange.IsCookable(stack);
            if (bake && cook) return RangeKind.Both;
            if (bake) return RangeKind.Oven;
            if (cook) return RangeKind.Burner;
            return RangeKind.None;
        }

        /// <summary>True if the stack may sit in an oven slot (oven slots take only bakeable food).</summary>
        public static bool AllowedInOven(ItemStack stack)
        {
            if (stack == null) return true;
            if (!InventoryRange.IsBakeable(stack)) return false;
            RangeKind k = Of(stack);
            return k == RangeKind.Oven || k == RangeKind.Both;
        }

        /// <summary>
        /// True if the stack may sit on a burner. Food that bakes or cooks only goes where the table puts it;
        /// pots and everything else are not food and are left to the other rules.
        /// </summary>
        public static bool FoodAllowedOnBurner(ItemStack stack)
        {
            if (stack == null) return true;
            if (stack.Collectible is BlockCookingContainer) return true;
            if (!InventoryRange.IsBakeable(stack) && !InventoryRange.IsCookable(stack)) return true;
            RangeKind k = Of(stack);
            return k == RangeKind.Burner || k == RangeKind.Both;
        }
    }
}
