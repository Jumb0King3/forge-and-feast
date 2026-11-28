using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>Adds "Firing: about X left" to the base game pit kiln's own tooltip while it is lit.</summary>
    public class BlockBehaviorPitKilnTimeLeft : BlockBehavior
    {
        public BlockBehaviorPitKilnTimeLeft(Block block) : base(block) { }

        public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos pos, IPlayer forPlayer)
        {
            BlockEntityPitKiln kiln = world.BlockAccessor.GetBlockEntity(pos) as BlockEntityPitKiln;
            if (kiln == null || !kiln.Lit) return "";
            double left = kiln.BurningUntilTotalHours - world.Calendar.TotalHours;
            return Lang.Get("forgeandfeast:pitkiln-left", TimeLeftHud.FormatHours(left)) + "\n";
        }
    }
}
