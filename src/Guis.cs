using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ForgeAndFeast
{
    /// <summary>
    /// Kitchen Range window: fuel at the top, then one row per burner -
    /// input (pot or food), the pot's 4 ingredient slots, output, and a status line.
    /// </summary>
    public class GuiDialogKitchenRange : GuiDialogBlockEntity
    {
        readonly BEKitchenRange be;
        long tickId;

        public GuiDialogKitchenRange(string title, InventoryBase inventory, BlockPos pos, ICoreClientAPI capi, BEKitchenRange be)
            : base(title, inventory, pos, capi)
        {
            if (IsDuplicate) return;
            this.be = be;
        }

        void Compose()
        {
            int burners = be.Burners;
            int ovens = be.Ovens;
            const double rowH = 64;
            const double top = 75;

            ElementBounds fuelBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, 1, 1);
            ElementBounds fuelTextBounds = ElementBounds.Fixed(60, 40, 480, 30);

            ElementBounds contentBounds = ElementBounds.Fixed(0, 0, 560, top + rowH * (burners + ovens));

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(contentBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            GuiComposer composer = capi.Gui
                .CreateCompo("forgeandfeastrange" + BlockEntityPosition, dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 0 }, fuelBounds, "fuelslot")
                    .AddDynamicText("", CairoFont.WhiteDetailText(), fuelTextBounds, "fueltext");

            for (int b = 0; b < burners; b++)
            {
                double y = top + rowH * b;
                ElementBounds inBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, y, 1, 1);
                ElementBounds cookBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 65, y, 4, 1);
                ElementBounds outBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 300, y, 1, 1);
                ElementBounds textBounds = ElementBounds.Fixed(360, y + 2, 200, 50);

                int c = InventoryRange.CookingSlotId(b, 0);
                composer
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { InventoryRange.InputSlotId(b) }, inBounds, "in" + b)
                    .AddItemSlotGrid(Inventory, DoSendPacket, 4, new int[] { c, c + 1, c + 2, c + 3 }, cookBounds, "cook" + b)
                    .AddStaticText("→", CairoFont.WhiteMediumText(), ElementBounds.Fixed(272, y + 10, 25, 30))
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { InventoryRange.OutputSlotId(b) }, outBounds, "out" + b)
                    .AddDynamicText("", CairoFont.WhiteDetailText().WithFontSize(13), textBounds, "status" + b);
            }

            for (int o = 0; o < ovens; o++)
            {
                int u = InventoryRange.MaxBurners + o;
                double y = top + rowH * (burners + o);
                ElementBounds inBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, y, 1, 1);
                ElementBounds outBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 300, y, 1, 1);
                ElementBounds textBounds = ElementBounds.Fixed(360, y + 2, 200, 50);
                composer
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { InventoryRange.InputSlotId(u) }, inBounds, "in" + u)
                    .AddStaticText(Lang.Get("forgeandfeast:oven-label"), CairoFont.WhiteDetailText(), ElementBounds.Fixed(65, y + 14, 200, 30))
                    .AddStaticText("→", CairoFont.WhiteMediumText(), ElementBounds.Fixed(272, y + 10, 25, 30))
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { InventoryRange.OutputSlotId(u) }, outBounds, "out" + u)
                    .AddDynamicText("", CairoFont.WhiteDetailText().WithFontSize(13), textBounds, "status" + u);
            }

            SingleComposer = composer.EndChildElements().Compose();
            Refresh(0);
        }

        void Refresh(float dt)
        {
            if (SingleComposer == null || be == null) return;
            SingleComposer.GetDynamicText("fueltext")?.SetNewText(be.GetFuelText());
            for (int b = 0; b < be.Burners; b++)
            {
                SingleComposer.GetDynamicText("status" + b)?.SetNewText(be.GetBurnerText(b));
            }
            for (int o = 0; o < be.Ovens; o++)
            {
                int u = InventoryRange.MaxBurners + o;
                SingleComposer.GetDynamicText("status" + u)?.SetNewText(be.GetBurnerText(u));
            }
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
            for (int b = 0; b < InventoryRange.MaxUnits; b++)
            {
                SingleComposer?.GetSlotGrid("in" + b)?.OnGuiClosed(capi);
                SingleComposer?.GetSlotGrid("cook" + b)?.OnGuiClosed(capi);
                SingleComposer?.GetSlotGrid("out" + b)?.OnGuiClosed(capi);
            }
            base.OnGuiClosed();
        }
    }

    /// <summary>Kiln Oven window: fuel at the top, then the firing slots with a status line each.</summary>
    public class GuiDialogKilnOven : GuiDialogBlockEntity
    {
        readonly BEKilnOven be;
        long tickId;

        public GuiDialogKilnOven(string title, InventoryBase inventory, BlockPos pos, ICoreClientAPI capi, BEKilnOven be)
            : base(title, inventory, pos, capi)
        {
            if (IsDuplicate) return;
            this.be = be;
        }

        void Compose()
        {
            int count = be.FiringSlots;
            const double rowH = 56;
            const double top = 75;
            int rows = (count + 1) / 2;

            ElementBounds fuelBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, 1, 1);
            ElementBounds fuelTextBounds = ElementBounds.Fixed(60, 40, 400, 30);
            ElementBounds contentBounds = ElementBounds.Fixed(0, 0, 440, top + rowH * rows);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(contentBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            GuiComposer composer = capi.Gui
                .CreateCompo("kilnoven" + BlockEntityPosition, dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 0 }, fuelBounds, "fuelslot")
                    .AddDynamicText("", CairoFont.WhiteDetailText(), fuelTextBounds, "fueltext");

            for (int i = 0; i < count; i++)
            {
                double x = (i % 2) * 220;
                double y = top + rowH * (i / 2);
                ElementBounds slotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, x, y, 1, 1);
                ElementBounds textBounds = ElementBounds.Fixed(x + 60, y + 12, 155, 40);
                composer
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 1 + i }, slotBounds, "fire" + i)
                    .AddDynamicText("", CairoFont.WhiteDetailText().WithFontSize(13), textBounds, "status" + i);
            }

            SingleComposer = composer.EndChildElements().Compose();
            Refresh(0);
        }

        void Refresh(float dt)
        {
            if (SingleComposer == null || be == null) return;
            SingleComposer.GetDynamicText("fueltext")?.SetNewText(be.GetFuelText());
            for (int i = 0; i < be.FiringSlots; i++)
            {
                SingleComposer.GetDynamicText("status" + i)?.SetNewText(be.GetSlotText(i));
            }
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
            for (int i = 0; i < InventoryKiln.MaxFiringSlots; i++)
            {
                SingleComposer?.GetSlotGrid("fire" + i)?.OnGuiClosed(capi);
            }
            base.OnGuiClosed();
        }
    }
}
