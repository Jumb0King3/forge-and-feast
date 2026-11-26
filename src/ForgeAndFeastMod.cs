using Vintagestory.API.Common;

namespace ForgeAndFeast
{
    /// <summary>Registers the stoves' block, block entity and behaviour classes.</summary>
    public class ForgeAndFeastMod : ModSystem
    {
        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            Smoking.ClearCache();
            api.RegisterBlockClass("FuelHeaterBlock", typeof(BlockFuelHeater));
            api.RegisterBlockEntityClass("KitchenRange", typeof(BEKitchenRange));
            api.RegisterBlockEntityClass("KilnOven", typeof(BEKilnOven));
            api.RegisterBlockEntityClass("CharcoalRetort", typeof(BERetort));
            api.RegisterBlockEntityClass("Smoker", typeof(BESmoker));
            api.RegisterBlockClass("PotbellyStoveBlock", typeof(BlockPotbellyStove));
            api.RegisterBlockEntityClass("PotbellyStove", typeof(BEPotbellyStove));
            api.RegisterBlockEntityBehaviorClass("ForgeSmokeVent", typeof(BEBehaviorSmokeVent));
            api.RegisterBlockClass("StovePipeBlock", typeof(BlockStovePipe));
            api.RegisterBlockEntityClass("StovePipe", typeof(BEStovePipe));
        }
    }
}
