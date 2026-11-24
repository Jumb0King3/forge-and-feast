using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>Fuel slot plus up to 8 firing slots. Lower tiers use fewer slots.</summary>
    public class InventoryKiln : InventoryBase
    {
        public const int MaxFiringSlots = 8;
        ItemSlot[] slots;
        public int ActiveSlots = MaxFiringSlots;

        public InventoryKiln(string inventoryID, ICoreAPI api) : base(inventoryID, api)
        {
            slots = GenEmptySlots(1 + MaxFiringSlots);
            baseWeight = 4f;
        }

        public ItemSlot FuelSlot => slots[0];
        public ItemSlot FiringSlot(int i) => slots[1 + i];

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
            return new ItemSlotSurvival(this);
        }

        public override bool CanContain(ItemSlot sinkSlot, ItemSlot sourceSlot)
        {
            int id = GetSlotId(sinkSlot);
            if (id > ActiveSlots) return false;
            return base.CanContain(sinkSlot, sourceSlot);
        }

        public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
        {
            int id = GetSlotId(targetSlot);
            CombustibleProperties props = sourceSlot.Itemstack?.Collectible?.CombustibleProps;
            if (id == 0)
            {
                if (props == null || props.BurnTemperature <= 0) return 0;
            }
            else
            {
                if (id > ActiveSlots) return 0;
                if (props != null && props.SmeltingType == EnumSmeltType.Fire) return 2.2f;
            }
            return base.GetSuitability(sourceSlot, targetSlot, isMerge);
        }

        public override void FromTreeAttributes(ITreeAttribute tree)
        {
            SlotTreeHelper.ReadSlots(this, tree, slots);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            SlotsToTreeAttributes(slots, tree);
        }
    }

    /// <summary>
    /// The Kiln Oven fires raw clay - tool molds, ingot molds, pots, bricks and anything else the
    /// game marks as needing a kiln - in its firing slots once it's hot enough.
    /// </summary>
    public class BEKilnOven : BEFuelHeater
    {
        readonly InventoryKiln inventory;
        public int FiringSlots = 4;
        /// <summary>Firing takes this many times the item's own firing duration (before the tier speed bonus).</summary>
        public float DurationMul = 4f;

        readonly float[] progress = new float[InventoryKiln.MaxFiringSlots];
        readonly string[] lastCode = new string[InventoryKiln.MaxFiringSlots];

        public BEKilnOven()
        {
            inventory = new InventoryKiln(null, null);
            inventory.SlotModified += OnSlotModified;
        }

        public override InventoryBase Inventory => inventory;
        public override string InventoryClassName => "kilnoven";
        public override ItemSlot FuelSlot => inventory.FuelSlot;

        protected override void ReadTierStats()
        {
            base.ReadTierStats();
            FiringSlots = GameMath.Clamp(Block?.Attributes?["firingSlots"].AsInt(4) ?? 4, 1, InventoryKiln.MaxFiringSlots);
            DurationMul = Block?.Attributes?["firingDurationMul"].AsFloat(4f) ?? 4f;
            inventory.ActiveSlots = FiringSlots;
        }

        void OnSlotModified(int slotId)
        {
            if (Api == null) return;
            MarkDirty(Api.Side == EnumAppSide.Server);
        }

        static bool IsFireable(ItemStack stack)
        {
            CombustibleProperties props = stack?.Collectible?.CombustibleProps;
            return props != null
                && props.SmeltingType == EnumSmeltType.Fire
                && props.SmeltedStack?.ResolvedItemstack != null
                && stack.StackSize >= Math.Max(1, props.SmeltedRatio);
        }

        protected override bool HasActiveWork()
        {
            for (int i = 0; i < FiringSlots; i++) if (IsFireable(inventory.FiringSlot(i).Itemstack)) return true;
            return false;
        }

        protected override void ProcessContents(float dt)
        {
            for (int i = 0; i < FiringSlots; i++)
            {
                ItemSlot slot = inventory.FiringSlot(i);
                ItemStack stack = slot.Itemstack;
                string code = stack?.Collectible?.Code?.ToString();
                if (code != lastCode[i])
                {
                    progress[i] = 0;
                    lastCode[i] = code;
                }

                if (!IsFireable(stack))
                {
                    progress[i] = 0;
                    continue;
                }

                CombustibleProperties props = stack.Collectible.CombustibleProps;
                if (temperature < props.MeltingPoint)
                {
                    if (progress[i] > 0) progress[i] = Math.Max(0, progress[i] - dt);
                    continue;
                }

                float bonus = GameMath.Clamp(temperature / Math.Max(1, props.MeltingPoint), 1f, 2f);
                progress[i] += dt * SpeedMul * VentMul * bonus;

                if (progress[i] >= RequiredTime(props))
                {
                    int ratio = Math.Max(1, props.SmeltedRatio);
                    ItemStack result = props.SmeltedStack.ResolvedItemstack.Clone();
                    result.StackSize *= stack.StackSize / ratio;
                    slot.Itemstack = result;
                    slot.MarkDirty();
                    progress[i] = 0;
                    lastCode[i] = result.Collectible.Code?.ToString();
                    MarkDirty(true);
                }
            }
        }

        float RequiredTime(CombustibleProperties props)
        {
            return Math.Max(5f, props.MeltingDuration * DurationMul);
        }

        /// <summary>Status line for one firing slot, shown in the GUI.</summary>
        public string GetSlotText(int i)
        {
            ItemStack stack = inventory.FiringSlot(i).Itemstack;
            if (stack == null) return "";
            if (!IsFireable(stack)) return Lang.Get("forgeandfeast:kiln-done");
            CombustibleProperties props = stack.Collectible.CombustibleProps;
            if (temperature < props.MeltingPoint) return Lang.Get("forgeandfeast:kiln-needs", (int)props.MeltingPoint);
            int pct = (int)GameMath.Clamp(100 * progress[i] / RequiredTime(props), 0, 100);
            return Lang.Get("forgeandfeast:kiln-firing", pct);
        }

        /// <summary>Draws the moulds and pots in the firing slots on the shelves behind the front opening.</summary>
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            string side = Block?.Variant?["side"] ?? "north";
            float rot = 0;
            switch (side)
            {
                case "east": rot = 270; break;
                case "south": rot = 180; break;
                case "west": rot = 90; break;
            }

            for (int i = 0; i < FiringSlots; i++)
            {
                ItemStack s = inventory.FiringSlot(i).Itemstack;
                if (s == null) continue;

                MeshData mesh = null;
                try
                {
                    if (s.Block != null && s.Block.Id != 0) tesselator.TesselateBlock(s.Block, out mesh);
                    else if (s.Item != null) tesselator.TesselateItem(s.Item, out mesh);
                }
                catch (Exception)
                {
                    mesh = null;
                }
                if (mesh == null) continue;

                int row = i / 4, col = i % 4;
                float x = (3.125f + 3.25f * col) / 16f - 0.5f;
                float z = 2.4f / 16f - 0.5f;
                float y = (row == 0 ? 5.4f : 9.0f) / 16f;
                float xr = x, zr = z;
                switch (side)
                {
                    case "east": xr = -z; zr = x; break;
                    case "south": xr = -x; zr = -z; break;
                    case "west": xr = z; zr = -x; break;
                }

                mesh = mesh.Clone();
                if (rot != 0) mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, rot * GameMath.DEG2RAD, 0);
                mesh.Scale(new Vec3f(0.5f, 0, 0.5f), 0.3f, 0.3f, 0.3f);
                mesh.Translate(xr, y, zr);
                mesher.AddMeshData(mesh);
            }
            return false;
        }

        public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                toggleInventoryDialogClient(byPlayer, () =>
                {
                    return new GuiDialogKilnOven(Lang.Get("forgeandfeast:kiln-title"), Inventory, Pos, Api as ICoreClientAPI, this);
                });
            }
            return true;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            for (int i = 0; i < InventoryKiln.MaxFiringSlots; i++)
            {
                tree.SetFloat("pkProg" + i, progress[i]);
                tree.SetString("pkCode" + i, lastCode[i] ?? "");
            }
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            for (int i = 0; i < InventoryKiln.MaxFiringSlots; i++)
            {
                progress[i] = tree.GetFloat("pkProg" + i);
                string c = tree.GetString("pkCode" + i, "");
                lastCode[i] = c.Length == 0 ? null : c;
            }
        }
    }
}
