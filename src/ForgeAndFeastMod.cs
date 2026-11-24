using Vintagestory.API.Common;

namespace ForgeAndFeast
{
    /// <summary>Registers the Kitchen Range and Kiln Oven block and block entity classes.</summary>
    public class ForgeAndFeastMod : ModSystem
    {
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockClass("FuelHeaterBlock", typeof(BlockFuelHeater));
            api.RegisterBlockEntityClass("KitchenRange", typeof(BEKitchenRange));
            api.RegisterBlockEntityClass("KilnOven", typeof(BEKilnOven));
            api.RegisterBlockEntityBehaviorClass("ForgeSmokeVent", typeof(BEBehaviorSmokeVent));
            api.RegisterBlockClass("StovePipeBlock", typeof(BlockStovePipe));
            api.RegisterBlockEntityClass("StovePipe", typeof(BEStovePipe));
        }
    }
}
