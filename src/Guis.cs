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
        FuelStoreView fuelStoreView;
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
            const double rowH = 74;
            const double top = 112;                       // first burner row, well clear of the fuel slot
            double ovenTop = top + rowH * burners + (ovens > 0 ? 34 : 0);

            ElementBounds fuelBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 32, 1, 1);
            fuelStoreView = new FuelStoreView(capi, be);
            ElementBounds fuelTextBounds = ElementBounds.Fixed(68 + fuelStoreView.Width, 40, 480 - fuelStoreView.Width, 44);

            ElementBounds contentBounds = ElementBounds.Fixed(0, 0, 580, ovenTop + rowH * ovens);

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
                    .AddFuelStore(fuelStoreView, fuelBounds.fixedX, fuelBounds.fixedY)
                    .AddDynamicText("", CairoFont.WhiteDetailText(), fuelTextBounds, "fueltext")
                    .AddStaticText(Lang.Get("forgeandfeast:range-burners"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, top - 26, 300, 22));

            for (int b = 0; b < burners; b++)
            {
                double y = top + rowH * b;
                ElementBounds inBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, y, 1, 1);
                ElementBounds cookBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 80, y, 4, 1);
                ElementBounds outBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 340, y, 1, 1);
                ElementBounds textBounds = ElementBounds.Fixed(405, y + 4, 175, 50);

                int c = InventoryRange.CookingSlotId(b, 0);
                composer
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { InventoryRange.InputSlotId(b) }, inBounds, "in" + b)
                    .AddItemSlotGrid(Inventory, DoSendPacket, 4, new int[] { c, c + 1, c + 2, c + 3 }, cookBounds, "cook" + b)
                    .AddStaticText("→", CairoFont.WhiteMediumText(), ElementBounds.Fixed(308, y + 10, 25, 30))
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { InventoryRange.OutputSlotId(b) }, outBounds, "out" + b)
                    .AddDynamicText("", CairoFont.WhiteDetailText().WithFontSize(13), textBounds, "status" + b);
            }

            if (ovens > 0)
                composer.AddStaticText(Lang.Get("forgeandfeast:range-ovens"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, ovenTop - 26, 300, 22));
            for (int o = 0; o < ovens; o++)
            {
                int u = InventoryRange.MaxBurners + o;
                double y = ovenTop + rowH * o;
                ElementBounds inBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, y, 1, 1);
                ElementBounds outBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 340, y, 1, 1);
                ElementBounds textBounds = ElementBounds.Fixed(405, y + 4, 175, 50);
                composer
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { InventoryRange.InputSlotId(u) }, inBounds, "in" + u)
                    .AddStaticText(Lang.Get("forgeandfeast:oven-label"), CairoFont.WhiteDetailText(), ElementBounds.Fixed(80, y + 14, 200, 30))
                    .AddStaticText("→", CairoFont.WhiteMediumText(), ElementBounds.Fixed(308, y + 10, 25, 30))
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
            fuelStoreView?.Refresh();
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
        FuelStoreView fuelStoreView;
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
            const double rowH = 66;
            const double top = 112;
            bool smelt = be.CanSmelt;
            int cols = smelt ? 1 : 2;
            int rows = (count + cols - 1) / cols;

            ElementBounds fuelBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 32, 1, 1);
            fuelStoreView = new FuelStoreView(capi, be);
            ElementBounds fuelTextBounds = ElementBounds.Fixed(68 + fuelStoreView.Width, 40, 430 - fuelStoreView.Width, 44);
            ElementBounds contentBounds = ElementBounds.Fixed(0, 0, smelt ? 540 : 500, top + rowH * rows);

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
                    .AddFuelStore(fuelStoreView, fuelBounds.fixedX, fuelBounds.fixedY)
                    .AddDynamicText("", CairoFont.WhiteDetailText(), fuelTextBounds, "fueltext")
                    .AddStaticText(Lang.Get("forgeandfeast:kiln-firing-slots"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, top - 26, 240, 22));
            if (smelt) composer.AddStaticText(Lang.Get("forgeandfeast:kiln-crucible-ingredients"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(265, top - 26, 260, 22));

            for (int i = 0; i < count; i++)
            {
                double x = (i % cols) * 235;
                double y = top + rowH * (i / cols);
                ElementBounds slotBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, x, y, 1, 1);
                ElementBounds textBounds = ElementBounds.Fixed(x + 68, y + 10, smelt ? 175 : 160, 40);
                composer
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 1 + i }, slotBounds, "fire" + i)
                    .AddDynamicText("", CairoFont.WhiteDetailText().WithFontSize(13), textBounds, "status" + i);
                if (smelt)
                {
                    // Crucible ingredients: these slots only accept items while a crucible sits in the firing slot beside them
                    ElementBounds ing = ElementStdBounds.SlotGrid(EnumDialogArea.None, 265, y, 4, 1);
                    composer.AddItemSlotGrid(Inventory, DoSendPacket, 4,
                        new int[] { InventoryKiln.CookingSlotId(i, 0), InventoryKiln.CookingSlotId(i, 1), InventoryKiln.CookingSlotId(i, 2), InventoryKiln.CookingSlotId(i, 3) },
                        ing, "cook" + i);
                }
            }

            SingleComposer = composer.EndChildElements().Compose();
            Refresh(0);
        }

        void Refresh(float dt)
        {
            if (SingleComposer == null || be == null) return;
            SingleComposer.GetDynamicText("fueltext")?.SetNewText(be.GetFuelText());
            fuelStoreView?.Refresh();
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
