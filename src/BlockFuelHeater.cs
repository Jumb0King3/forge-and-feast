using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>
    /// Block class for the Kitchen Range and Kiln Oven. Right-click opens the window.
    /// Shift + right-click with fuel adds one piece. A firestarter or torch lights it, like a firepit.
    /// </summary>
    public class BlockFuelHeater : Block, IIgnitable
    {
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (blockSel == null) return false;
            if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use)) return false;

            BEFuelHeater be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEFuelHeater;
            if (be == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);

            ItemSlot hotbar = byPlayer.InventoryManager.ActiveHotbarSlot;
            ItemStack stack = hotbar?.Itemstack;

            // Let a firestarter or torch do its lighting
            if (stack != null && IsIgniter(stack) && be.GetIgnitableState(0) == EnumIgniteState.Ignitable)
            {
                return false;
            }

            // Shift + right-click with fuel tops up the fuel slot
            if (stack != null && byPlayer.Entity.Controls.ShiftKey)
            {
                CombustibleProperties props = stack.Collectible.CombustibleProps;
                if (props != null && props.BurnTemperature > 0)
                {
                    ItemStackMoveOperation op = new ItemStackMoveOperation(world, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge, 1);
                    hotbar.TryPutInto(be.FuelSlot, ref op);
                    if (op.MovedQuantity > 0)
                    {
                        (byPlayer as IClientPlayer)?.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);
                        return true;
                    }
                }
            }

            return be.OnPlayerRightClick(byPlayer, blockSel);
        }

        static bool IsIgniter(ItemStack stack)
        {
            if (stack.Collectible is ItemFirestarter) return true;
            if (stack.Block != null && stack.Block.HasBehavior<BlockBehaviorCanIgnite>()) return true;
            return false;
        }

        public EnumIgniteState OnTryIgniteBlock(EntityAgent byEntity, BlockPos pos, float secondsIgniting)
        {
            BEFuelHeater be = api.World.BlockAccessor.GetBlockEntity(pos) as BEFuelHeater;
            if (be == null) return EnumIgniteState.NotIgnitable;
            return be.GetIgnitableState(secondsIgniting);
        }

        public void OnTryIgniteBlockOver(EntityAgent byEntity, BlockPos pos, float secondsIgniting, ref EnumHandling handling)
        {
            BEFuelHeater be = api.World.BlockAccessor.GetBlockEntity(pos) as BEFuelHeater;
            be?.Ignite();
            handling = EnumHandling.PreventDefault;
        }

        public EnumIgniteState OnTryIgniteStack(EntityAgent byEntity, BlockPos pos, ItemSlot slot, float secondsIgniting)
        {
            // Light a torch from the fire while it's burning
            BEFuelHeater be = api.World.BlockAccessor.GetBlockEntity(pos) as BEFuelHeater;
            if (be != null && be.IsBurning) return secondsIgniting > 2 ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
            return EnumIgniteState.NotIgnitable;
        }
    }
}
