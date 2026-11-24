using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>Fuel slot, one slot for firewood or logs, and one output slot for the charcoal.</summary>
    public class InventoryRetort : InventoryBase
    {
        ItemSlot[] slots;

        public InventoryRetort(string inventoryID, ICoreAPI api) : base(inventoryID, api)
        {
            slots = GenEmptySlots(3);
            baseWeight = 4f;
        }

        public ItemSlot FuelSlot => slots[0];
        public ItemSlot InputSlot => slots[1];
        public ItemSlot OutputSlot => slots[2];

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
            if (i == 2) return new ItemSlotOutput(this);
            return new ItemSlotSurvival(this);
        }

        public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
        {
            int id = GetSlotId(targetSlot);
            ItemStack stack = sourceSlot.Itemstack;
            if (id == 2) return 0;
            if (id == 1)
            {
                if (!BERetort.IsRetortable(stack)) return 0;
                return 2.2f;
            }
            // Fuel slot: charcoal and coal before wood, and not the firewood meant for the retort itself
            CombustibleProperties props = stack?.Collectible?.CombustibleProps;
            if (props == null || props.BurnTemperature <= 0) return 0;
            if (BERetort.IsRetortable(stack)) return 0.5f;
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
    /// The Charcoal Retort cooks firewood and logs without air to make charcoal. It needs fuel to heat it,
    /// attaches to the same stovepipes and chimneys as the stoves, and makes a lot more smoke.
    /// </summary>
    public class BERetort : BEFuelHeater
    {
        public const float MinTemp = 250f;
        const float BatchSeconds = 40f;

        readonly InventoryRetort inventory;
        float progress;

        public BERetort()
        {
            inventory = new InventoryRetort(null, null);
            inventory.SlotModified += id => { if (Api != null) MarkDirty(Api.Side == EnumAppSide.Server); };
        }

        public override InventoryBase Inventory => inventory;
        public override string InventoryClassName => "charcoalretort";
        public override ItemSlot FuelSlot => inventory.FuelSlot;

        /// <summary>Firewood or logs. Returns how many pieces make one batch via NeededFor.</summary>
        public static bool IsRetortable(ItemStack stack)
        {
            if (stack?.Collectible?.Code == null) return false;
            if (stack.Collectible.Code.Domain != "game") return false;
            string path = stack.Collectible.Code.Path;
            return path.StartsWith("firewood") || path.StartsWith("log-");
        }

        static int NeededFor(ItemStack stack)
        {
            return stack.Collectible.Code.Path.StartsWith("firewood") ? 2 : 1;
        }

        Item Charcoal => Api.World.GetItem(new AssetLocation("game:charcoal"));

        bool CanWork()
        {
            ItemStack input = inventory.InputSlot.Itemstack;
            if (!IsRetortable(input) || input.StackSize < NeededFor(input)) return false;
            Item coal = Charcoal;
            if (coal == null) return false;
            ItemStack outp = inventory.OutputSlot.Itemstack;
            if (outp == null) return true;
            return outp.Collectible == coal && outp.StackSize < outp.Collectible.MaxStackSize;
        }

        bool Working => IsBurning && temperature >= MinTemp && Api != null && CanWork();

        public override float CurrentSmokeMul => Working ? SmokeMul * 1.5f : SmokeMul;

        protected override bool HasActiveWork() => IsRetortable(inventory.InputSlot.Itemstack);

        protected override void ProcessContents(float dt)
        {
            if (!Working)
            {
                if (progress > 0) progress = Math.Max(0, progress - dt);
                return;
            }

            progress += dt * SpeedMul * VentMul;
            if (progress < BatchSeconds) return;

            ItemStack input = inventory.InputSlot.Itemstack;
            int need = NeededFor(input);
            inventory.InputSlot.TakeOut(need);
            inventory.InputSlot.MarkDirty();

            ItemSlot outSlot = inventory.OutputSlot;
            if (outSlot.Itemstack == null) outSlot.Itemstack = new ItemStack(Charcoal, 1);
            else outSlot.Itemstack.StackSize += 1;
            outSlot.MarkDirty();
            progress = 0;
            MarkDirty(true);
        }

        public string GetStatusText()
        {
            ItemStack input = inventory.InputSlot.Itemstack;
            if (!IsRetortable(input)) return Lang.Get("forgeandfeast:retort-empty");
            if (!CanWork()) return Lang.Get("forgeandfeast:retort-full");
            if (temperature < MinTemp) return Lang.Get("forgeandfeast:retort-needs", (int)MinTemp);
            int pct = (int)GameMath.Clamp(100 * progress / BatchSeconds, 0, 100);
            return Lang.Get("forgeandfeast:retort-working", pct);
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            if ((StovePipes.ConnectionMask(Api.World.BlockAccessor, Pos) & (1 << BlockFacing.UP.Index)) != 0)
            {
                MeshData body = GetMeshWithoutStub(tesselator);
                if (body != null)
                {
                    mesher.AddMeshData(body);
                    return true;
                }
            }
            return false;
        }

        public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                toggleInventoryDialogClient(byPlayer, () =>
                {
                    return new GuiDialogRetort(Lang.Get("forgeandfeast:retort-title"), Inventory, Pos, Api as ICoreClientAPI, this);
                });
            }
            return true;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("pkRetProg", progress);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            progress = tree.GetFloat("pkRetProg");
        }
    }

    public class GuiDialogRetort : GuiDialogBlockEntity
    {
        readonly BERetort be;
        long tickId;

        public GuiDialogRetort(string title, InventoryBase inventory, BlockPos pos, ICoreClientAPI capi, BERetort be)
            : base(title, inventory, pos, capi)
        {
            if (IsDuplicate) return;
            this.be = be;
        }

        void Compose()
        {
            ElementBounds fuelBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, 1, 1);
            ElementBounds fuelTextBounds = ElementBounds.Fixed(60, 40, 400, 30);
            ElementBounds inBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 100, 1, 1);
            ElementBounds outBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 200, 100, 1, 1);
            ElementBounds statusBounds = ElementBounds.Fixed(0, 160, 440, 30);
            ElementBounds contentBounds = ElementBounds.Fixed(0, 0, 440, 190);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(contentBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            SingleComposer = capi.Gui
                .CreateCompo("charcoalretort" + BlockEntityPosition, dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 0 }, fuelBounds, "fuelslot")
                    .AddDynamicText("", CairoFont.WhiteDetailText(), fuelTextBounds, "fueltext")
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 1 }, inBounds, "inslot")
                    .AddStaticText("→", CairoFont.WhiteMediumText(), ElementBounds.Fixed(110, 110, 40, 30))
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 2 }, outBounds, "outslot")
                    .AddDynamicText("", CairoFont.WhiteDetailText().WithFontSize(13), statusBounds, "status")
                .EndChildElements()
                .Compose();
            Refresh(0);
        }

        void Refresh(float dt)
        {
            if (SingleComposer == null || be == null) return;
            SingleComposer.GetDynamicText("fueltext")?.SetNewText(be.GetFuelText());
            SingleComposer.GetDynamicText("status")?.SetNewText(be.GetStatusText());
        }

        void OnTitleBarClose()
        {
            TryClose();
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            Compose();
            tickId = capi.Event.RegisterGameTickListener(Refresh, 500);
        }

        public override void OnGuiClosed()
        {
            if (tickId != 0) capi.Event.UnregisterGameTickListener(tickId);
            tickId = 0;
            SingleComposer?.GetSlotGrid("fuelslot")?.OnGuiClosed(capi);
            SingleComposer?.GetSlotGrid("inslot")?.OnGuiClosed(capi);
            SingleComposer?.GetSlotGrid("outslot")?.OnGuiClosed(capi);
            base.OnGuiClosed();
        }
    }
}
