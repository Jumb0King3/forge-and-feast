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
        public const float CokeTemp = 450f;
        const float CokeSeconds = 60f;

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

        /// <summary>What a retort batch does with an input: how much goes in, what and how much comes out.</summary>
        public class Batch
        {
            public int Needed;
            public string OutputCode;
            public int OutputQty;
            public float MinTemp;
            public float Seconds;
        }

        /// <summary>
        /// Firewood and logs make charcoal. Coal makes coke at the base game's coke oven yields:
        /// black coal 3 coke per 4 coal (0.75), brown coal 1 per 2 (0.5). Coke needs a hotter retort, so Copper can't do it.
        /// </summary>
        public static Batch BatchFor(ItemStack stack)
        {
            if (stack?.Collectible?.Code == null || stack.Collectible.Code.Domain != "game") return null;
            string path = stack.Collectible.Code.Path;
            if (path.StartsWith("firewood")) return new Batch { Needed = 3, OutputCode = "game:charcoal", OutputQty = 1, MinTemp = MinTemp, Seconds = BatchSeconds };
            if (path.StartsWith("log-")) return new Batch { Needed = 3, OutputCode = "game:charcoal", OutputQty = 4, MinTemp = MinTemp, Seconds = BatchSeconds };
            if (path == "ore-bituminouscoal") return new Batch { Needed = 4, OutputCode = "game:coke", OutputQty = 3, MinTemp = CokeTemp, Seconds = CokeSeconds };
            if (path == "ore-lignite") return new Batch { Needed = 2, OutputCode = "game:coke", OutputQty = 1, MinTemp = CokeTemp, Seconds = CokeSeconds };
            return null;
        }

        public static bool IsRetortable(ItemStack stack) => BatchFor(stack) != null;

        bool CanWork()
        {
            ItemStack input = inventory.InputSlot.Itemstack;
            Batch batch = BatchFor(input);
            if (batch == null || input.StackSize < batch.Needed) return false;
            Item product = Api.World.GetItem(new AssetLocation(batch.OutputCode));
            if (product == null) return false;
            ItemStack outp = inventory.OutputSlot.Itemstack;
            if (outp == null) return true;
            return outp.Collectible == product && outp.StackSize + batch.OutputQty <= outp.Collectible.MaxStackSize;
        }

        bool Working
        {
            get
            {
                if (!IsBurning || Api == null || !CanWork()) return false;
                return temperature >= BatchFor(inventory.InputSlot.Itemstack).MinTemp;
            }
        }

        public override float CurrentSmokeMul => Working ? SmokeMul * 1.5f : SmokeMul;

        protected override bool HasActiveWork() => IsRetortable(inventory.InputSlot.Itemstack);

        protected override void ProcessContents(float dt)
        {
            if (!Working)
            {
                if (progress > 0) progress = Math.Max(0, progress - dt);
                return;
            }

            ItemStack input = inventory.InputSlot.Itemstack;
            Batch batch = BatchFor(input);
            progress += dt * SpeedMul * VentMul;
            if (progress < batch.Seconds) return;

            inventory.InputSlot.TakeOut(batch.Needed);
            inventory.InputSlot.MarkDirty();

            Item product = Api.World.GetItem(new AssetLocation(batch.OutputCode));
            ItemSlot outSlot = inventory.OutputSlot;
            if (outSlot.Itemstack == null) outSlot.Itemstack = new ItemStack(product, batch.OutputQty);
            else outSlot.Itemstack.StackSize += batch.OutputQty;
            outSlot.MarkDirty();
            progress = 0;
            MarkDirty(true);
        }

        protected override string DisplayKey()
        {
            ItemStack a = inventory.InputSlot.Itemstack, b = inventory.OutputSlot.Itemstack;
            return a?.Collectible?.Code + ":" + (a == null ? 0 : a.StackSize / 3) + "," + b?.Collectible?.Code + ":" + (b == null ? 0 : b.StackSize / 4);
        }

        public string GetStatusText()
        {
            ItemStack input = inventory.InputSlot.Itemstack;
            if (!IsRetortable(input)) return Lang.Get("forgeandfeast:retort-empty");
            if (!CanWork()) return Lang.Get("forgeandfeast:retort-full");
            Batch batch = BatchFor(input);
            if (temperature < batch.MinTemp)
            {
                if (MaxTemp < batch.MinTemp) return Lang.Get("forgeandfeast:retort-toocool", (int)batch.MinTemp);
                return Lang.Get("forgeandfeast:retort-needs", (int)batch.MinTemp);
            }
            int pct = (int)GameMath.Clamp(100 * progress / batch.Seconds, 0, 100);
            return Lang.Get(batch.OutputCode == "game:coke" ? "forgeandfeast:retort-working-coke" : "forgeandfeast:retort-working", pct);
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            bool drewBody = false;
            if ((StovePipes.ConnectionMask(Api.World.BlockAccessor, Pos) & (1 << BlockFacing.UP.Index)) != 0)
            {
                MeshData body = GetMeshWithoutStub(tesselator);
                if (body != null)
                {
                    mesher.AddMeshData(body);
                    drewBody = true;
                }
            }

            // With the hatch open you can see what is inside: wood waiting at the front, charcoal behind it
            if (DoorsOpen)
            {
                ItemStack wood = inventory.InputSlot.Itemstack;
                ItemStack coal = inventory.OutputSlot.Itemstack;
                if (wood != null) DrawPile(mesher, tesselator, wood, Math.Min(10, 2 + wood.StackSize / 3), 3.2f, 7.2f, true);
                if (coal != null) DrawPile(mesher, tesselator, coal, Math.Min(14, 3 + coal.StackSize / 4), wood != null ? 8.2f : 3.6f, 13.2f, false);
            }
            return drewBody;
        }

        /// <summary>Scatters a few pieces of the stack on the floor of the drum, between zFrom and zTo (in 1/16 blocks).</summary>
        void DrawPile(ITerrainMeshPool mesher, ITesselatorAPI tesselator, ItemStack stack, int pieces, float zFrom, float zTo, bool logs)
        {
            MeshData mesh = ContentMesh.For(Api, tesselator, stack);
            if (mesh == null) return;

            string side = Block?.Variant?["side"] ?? "north";
            float rot = 0;
            switch (side)
            {
                case "east": rot = 270; break;
                case "south": rot = 180; break;
                case "west": rot = 90; break;
            }

            const float floorY = 5.4f / 16f;
            float scale = logs ? 0.34f : 0.26f;
            // Pieces sit in two or three tidy layers, each a little offset so it reads as a heap
            int perLayer = logs ? 4 : 5;
            for (int i = 0; i < pieces; i++)
            {
                int layer = i / perLayer, n = i % perLayer;
                float fx = (n + 0.5f) / perLayer;
                float x = (5.6f + 4.8f * fx + (layer % 2) * 0.5f) / 16f - 0.5f;
                float zSpan = zTo - zFrom;
                float z = (zFrom + zSpan * (((n * 7 + layer * 3) % perLayer) + 0.5f) / perLayer) / 16f - 0.5f;
                float y = floorY + layer * (logs ? 0.045f : 0.04f);
                float xr = x, zr = z;
                switch (side)
                {
                    case "east": xr = -z; zr = x; break;
                    case "south": xr = -x; zr = -z; break;
                    case "west": xr = z; zr = -x; break;
                }

                MeshData m = mesh.Clone();
                float spin = ((n * 53 + layer * 37) % 90) * GameMath.DEG2RAD + (logs && layer % 2 == 1 ? GameMath.PIHALF : 0);
                if (rot != 0) m.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, rot * GameMath.DEG2RAD, 0);
                m.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, spin, 0);
                m.Scale(new Vec3f(0.5f, 0, 0.5f), scale, scale, scale);
                m.Translate(xr, y, zr);
                mesher.AddMeshData(m);
            }
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
