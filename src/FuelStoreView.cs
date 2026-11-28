using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ForgeAndFeast
{
    /// <summary>
    /// Shows a heater's fuel store in its GUI as a row of slots beside the fuel slot (one per extra stack the
    /// tier holds). Click one with fuel on the mouse to put it in (fuel slot first, then the store), or with
    /// an empty hand to take a stack back out. The store feeds the fuel slot by itself.
    /// </summary>
    /// <summary>A store slot: click with fuel on the mouse to put it in, with an empty hand to take a stack out.</summary>
    public class GuiElementFuelStoreSlot : GuiElementPassiveItemSlot
    {
        readonly System.Action onClick;

        public GuiElementFuelStoreSlot(ICoreClientAPI capi, ElementBounds bounds, IInventory inv, ItemSlot slot, System.Action onClick)
            : base(capi, bounds, inv, slot, true)
        {
            this.onClick = onClick;
        }

        public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
        {
            args.Handled = true;
            onClick?.Invoke();
        }
    }

    public class FuelStoreView
    {
        public const int PacketId = 7311;
        readonly ICoreClientAPI capi;
        public const double Pitch = 51;
        public const double Gap = 8;

        readonly BEFuelHeater be;
        readonly DummyInventory inv;
        public readonly int Count;

        public FuelStoreView(ICoreClientAPI capi, BEFuelHeater be)
        {
            this.be = be;
            this.capi = capi;
            Count = be == null ? 0 : Math.Max(0, be.TierIndex);
            if (Count > 0) inv = new DummyInventory(capi, Count);
        }

        /// <summary>How far the fuel text has to move over to make room.</summary>
        public double Width => Count > 0 ? Gap + Count * Pitch : 0;

        public void Add(GuiComposer c, double fuelX, double y)
        {
            for (int i = 0; i < Count; i++)
            {
                ElementBounds b = ElementStdBounds.SlotGrid(EnumDialogArea.None, fuelX + Pitch + Gap + i * Pitch, y, 1, 1);
                c.AddInteractiveElement(new GuiElementFuelStoreSlot(capi, b, inv, inv[i], Clicked), "fuelstore" + i);
            }
            Refresh();
        }

        void Clicked()
        {
            if (be?.Pos != null) capi.Network.SendBlockEntityPacket(be.Pos, PacketId, null);
        }

        public void Refresh()
        {
            if (Count == 0) return;
            ItemStack[] stacks = be.FuelStoreStacks(Count);
            for (int i = 0; i < Count; i++) inv[i].Itemstack = stacks[i];
        }
    }

    public static class FuelStoreViewExtensions
    {
        public static GuiComposer AddFuelStore(this GuiComposer c, FuelStoreView v, double fuelX, double y)
        {
            v?.Add(c, fuelX, y);
            return c;
        }
    }
}
