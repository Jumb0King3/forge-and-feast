using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>Loads slot contents from a saved tree (the game's own helper can't be called from a source mod).</summary>
    public static class SlotTreeHelper
    {
        public static void ReadSlots(InventoryBase inv, ITreeAttribute tree, ItemSlot[] slots)
        {
            if (tree == null) return;
            ITreeAttribute slotsTree = tree.GetTreeAttribute("slots");
            for (int i = 0; i < slots.Length; i++)
            {
                ItemStack stack = slotsTree?.GetItemstack(i.ToString());
                if (stack != null && inv.Api?.World != null) stack.ResolveBlockOrItem(inv.Api.World);
                slots[i].Itemstack = stack;
            }
        }
    }

    /// <summary>The four ingredient slots of one burner, handed to the pot's cooking code.</summary>
    public class BurnerSlots : ISlotProvider
    {
        readonly ItemSlot[] slots;
        public BurnerSlots(ItemSlot[] slots) { this.slots = slots; }
        public ItemSlot[] Slots => slots;
    }

    /// <summary>
    /// Slot 0 is fuel. Each burner then has 6 slots: input (pot or food), output, and 4 ingredient
    /// slots used when a pot is in the input. The inventory always holds 4 burners; lower tiers
    /// just don't use the extra ones.
    /// </summary>
    public class InventoryRange : InventoryBase
    {
        public const int MaxBurners = 4;
        public const int MaxOvens = 3;
        /// <summary>Burners come first, then the oven slots (bake only).</summary>
        public const int MaxUnits = MaxBurners + MaxOvens;
        public const int PerBurner = 6;

        ItemSlot[] slots;
        BurnerSlots[] providers;

        public BlockPos Pos;
        public int ActiveBurners = MaxBurners;
        public int ActiveOvens = MaxOvens;

        public static bool IsOven(int unit) => unit >= MaxBurners;
        public bool IsActiveUnit(int unit) => unit < MaxBurners ? unit < ActiveBurners : unit - MaxBurners < ActiveOvens;
        public static bool IsBakeable(ItemStack stack)
        {
            if (stack == null) return false;
            BakingProperties bp = BakingProperties.ReadFrom(stack);
            return bp != null && bp.ResultCode != null;
        }

        public InventoryRange(string inventoryID, ICoreAPI api) : base(inventoryID, api)
        {
            slots = GenEmptySlots(1 + MaxUnits * PerBurner);
            BuildProviders();
            baseWeight = 4f;
        }

        void BuildProviders()
        {
            providers = new BurnerSlots[MaxUnits];
            for (int b = 0; b < MaxUnits; b++)
            {
                int c = CookingSlotId(b, 0);
                providers[b] = new BurnerSlots(new ItemSlot[] { slots[c], slots[c + 1], slots[c + 2], slots[c + 3] });
            }
        }

        public static int InputSlotId(int burner) => 1 + burner * PerBurner;
        public static int OutputSlotId(int burner) => 2 + burner * PerBurner;
        public static int CookingSlotId(int burner, int i) => 3 + burner * PerBurner + i;

        /// <summary>Which burner a slot belongs to, or -1 for the fuel slot.</summary>
        public static int BurnerOf(int slotId) => slotId <= 0 ? -1 : (slotId - 1) / PerBurner;

        public ItemSlot FuelSlot => slots[0];
        public ItemSlot InputSlot(int burner) => slots[InputSlotId(burner)];
        public ItemSlot OutputSlot(int burner) => slots[OutputSlotId(burner)];
        public BurnerSlots Burner(int burner) => providers[burner];

        public bool HasPot(int burner)
        {
            return InputSlot(burner).Itemstack?.ItemAttributes?.KeyExists("cookingContainerSlots") == true;
        }

        public override int Count => slots.Length;

        public override ItemSlot this[int slotId]
        {
            get
            {
                if (slotId < 0 || slotId >= Count) return null;
                return slots[slotId];
            }
            set
            {
                if (slotId < 0 || slotId >= Count) throw new ArgumentOutOfRangeException(nameof(slotId));
                if (value == null) throw new ArgumentNullException(nameof(value));
                slots[slotId] = value;
            }
        }

        protected override ItemSlot NewSlot(int i)
        {
            if (i == 0) return new ItemSlotSurvival(this);
            int rel = (i - 1) % PerBurner;
            if (rel == 0) return new ItemSlotSurvival(this);
            if (rel == 1) return new ItemSlotOutput(this);
            return new ItemSlotWatertight(this, 6);
        }

        public override bool CanContain(ItemSlot sinkSlot, ItemSlot sourceSlot)
        {
            int id = GetSlotId(sinkSlot);
            int burner = BurnerOf(id);
            if (burner >= 0 && !IsActiveUnit(burner)) return false;
            if (burner >= 0)
            {
                int rel = (id - 1) % PerBurner;
                if (rel >= 2 && !HasPot(burner)) return false;
                if (rel == 0 && !InputAccepts(burner, sourceSlot?.Itemstack)) return false;
            }
            return base.CanContain(sinkSlot, sourceSlot);
        }

        /// <summary>No crucibles or ores - this is a kitchen. Pots go in only if the output slot is clear.</summary>
        bool InputAccepts(int burner, ItemStack stack)
        {
            if (stack == null) return true;
            // The oven slots only take things to bake; the burners take everything else
            if (IsOven(burner)) return IsBakeable(stack);
            if (IsBakeable(stack)) return false;
            if (stack.Collectible is BlockSmeltingContainer) return false;
            if (stack.Collectible.CombustibleProps?.RequiresContainer == true) return false;
            if (stack.Collectible is BlockCookingContainer && !OutputSlot(burner).Empty) return false;
            return true;
        }

        public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
        {
            ItemStack stack = sourceSlot.Itemstack;
            int id = GetSlotId(targetSlot);
            if (id == 0)
            {
                CombustibleProperties props = stack?.Collectible?.CombustibleProps;
                if (props == null || props.BurnTemperature <= 0) return 0;
            }
            int burner = BurnerOf(id);
            if (burner >= 0)
            {
                int rel = (id - 1) % PerBurner;
                if (rel == 1) return 0;
                if (rel >= 2 && !HasPot(burner)) return 0;
                if (rel == 0 && IsOven(burner)) return IsBakeable(stack) ? 2.2f : 0;
                if (rel == 0 && IsBakeable(stack)) return 0;
                if (rel == 0 && stack?.Collectible is BlockCookingContainer) return 2.2f;
            }
            return base.GetSuitability(sourceSlot, targetSlot, isMerge);
        }

        public override void LateInitialize(string inventoryID, ICoreAPI api)
        {
            base.LateInitialize(inventoryID, api);
            for (int b = 0; b < MaxUnits; b++) UpdateCookingSlots(b);
        }

        public override void DidModifyItemSlot(ItemSlot slot, ItemStack extractedStack = null)
        {
            base.DidModifyItemSlot(slot, extractedStack);

            int id = GetSlotId(slot);
            int burner = BurnerOf(id);
            if (burner >= 0 && (id - 1) % PerBurner == 0)
            {
                if (slot.Itemstack?.ItemAttributes?["storageType"].Exists != true)
                {
                    DiscardCookingSlots(burner);
                }
                UpdateCookingSlots(burner);
            }
        }

        /// <summary>Ingredient slot size follows the pot in the burner, like the vanilla firepit.</summary>
        public void UpdateCookingSlots(int burner)
        {
            ItemStack pot = InputSlot(burner).Itemstack;
            int maxStack = pot?.ItemAttributes?.KeyExists("cookingContainerSlots") == true ? pot.ItemAttributes["maxContainerSlotStackSize"].AsInt(999) : 0;
            float litres = pot?.ItemAttributes?["cookingSlotCapacityLitres"].AsFloat(6) ?? 6;
            for (int i = 0; i < 4; i++)
            {
                ItemSlot s = slots[CookingSlotId(burner, i)];
                s.MaxSlotStackSize = maxStack;
                ItemSlotWatertight wt = s as ItemSlotWatertight;
                if (wt != null) wt.capacityLitres = litres;
            }
        }

        void DiscardCookingSlots(int burner)
        {
            if (Api == null || Api.Side != EnumAppSide.Server || Pos == null) return;
            Vec3d droppos = Pos.ToVec3d().Add(0.5, 1.1, 0.5);
            for (int i = 0; i < 4; i++)
            {
                ItemSlot s = slots[CookingSlotId(burner, i)];
                if (s.Empty) continue;
                Api.World.SpawnItemEntity(s.Itemstack, droppos);
                s.Itemstack = null;
                s.MarkDirty();
            }
        }

        public override void FromTreeAttributes(ITreeAttribute tree)
        {
            SlotTreeHelper.ReadSlots(this, tree, slots);
            BuildProviders();
            if (Api != null)
            {
                for (int b = 0; b < MaxUnits; b++) UpdateCookingSlots(b);
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            SlotsToTreeAttributes(slots, tree);
        }
    }
}
