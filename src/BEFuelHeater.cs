using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>
    /// Shared fuel and heat handling for the Kitchen Range and the Kiln Oven.
    /// Fuel burns one piece at a time like the vanilla firepit. While lit, the next piece is
    /// lit automatically when the current one runs out. The block swaps between its
    /// "cold" and "lit" burnstate variants so it glows and gives off light while burning.
    /// Tier stats come from the block's attributes (set per tier in the block JSON).
    /// </summary>
    public abstract class BEFuelHeater : BlockEntityOpenableContainer, IHeatSource
    {
        public float temperature = 20;
        public int fuelTemperature;
        public float fuelBurnTime;
        public float maxFuelBurnTime;

        /// <summary>How fast things cook, bake or fire, compared to a vanilla firepit.</summary>
        public float SpeedMul = 1f;
        /// <summary>How long each piece of fuel lasts, compared to a vanilla firepit.</summary>
        public float BurnMul = 1f;
        /// <summary>How fast the station itself heats up.</summary>
        public float HeatMul = 1f;
        /// <summary>The hottest this station can get, whatever the fuel.</summary>
        public int MaxTemp = 1000;

        float prevTemperature = 20;

        public bool IsBurning => fuelBurnTime > 0;

        public abstract ItemSlot FuelSlot { get; }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            ReadTierStats();

            if (api.Side == EnumAppSide.Server)
            {
                RegisterGameTickListener(OnServerTick, 100);
                RegisterGameTickListener(On500msTick, 500);
            }
        }

        protected virtual void ReadTierStats()
        {
            JsonObject attr = Block?.Attributes;
            if (attr == null) return;
            SpeedMul = attr["speedMul"].AsFloat(1f);
            BurnMul = attr["burnMul"].AsFloat(1f);
            HeatMul = attr["heatMul"].AsFloat(1f);
            MaxTemp = attr["maxTemp"].AsInt(1000);
        }

        void On500msTick(float dt)
        {
            if (IsBurning || prevTemperature != temperature || HasActiveWork())
            {
                MarkDirty();
            }
            prevTemperature = temperature;
        }

        void OnServerTick(float dt)
        {
            if (fuelBurnTime > 0)
            {
                fuelBurnTime -= dt;
                if (fuelBurnTime <= 0)
                {
                    fuelBurnTime = 0;
                    maxFuelBurnTime = 0;
                    if (!TryBurnNextFuel())
                    {
                        SetBurnState("cold");
                        MarkDirty(true);
                    }
                }
            }

            float target = IsBurning ? Math.Min(fuelTemperature, MaxTemp) : EnvironmentTemperature();
            float rate = IsBurning ? HeatMul : 1f;
            temperature = ChangeTemperature(temperature, target, dt * rate);

            ProcessContents(dt);
        }

        /// <summary>Called every 100ms on the server to cook, bake or fire whatever is inside.</summary>
        protected abstract void ProcessContents(float dt);

        /// <summary>True while something inside is being worked on, so clients keep getting progress updates.</summary>
        protected abstract bool HasActiveWork();

        protected virtual int EnvironmentTemperature()
        {
            return 20;
        }

        public static float ChangeTemperature(float fromTemp, float toTemp, float dt)
        {
            float diff = Math.Abs(fromTemp - toTemp);
            dt = dt + dt * (diff / 28);
            if (diff < dt) return toTemp;
            if (fromTemp > toTemp) dt = -dt;
            if (Math.Abs(fromTemp - toTemp) < 1) return toTemp;
            return fromTemp + dt;
        }

        public bool HasBurnableFuel()
        {
            CombustibleProperties props = FuelSlot.Itemstack?.Collectible?.CombustibleProps;
            return props != null && props.BurnTemperature > 0 && props.BurnDuration > 0;
        }

        /// <summary>Lights the next piece of fuel. Returns false if there's nothing burnable left.</summary>
        public bool TryBurnNextFuel()
        {
            if (!HasBurnableFuel()) return false;

            CombustibleProperties props = FuelSlot.Itemstack.Collectible.CombustibleProps;
            maxFuelBurnTime = fuelBurnTime = props.BurnDuration * BurnMul;
            fuelTemperature = props.BurnTemperature;

            FuelSlot.TakeOut(1);
            FuelSlot.MarkDirty();

            SetBurnState("lit");
            MarkDirty(true);
            return true;
        }

        public EnumIgniteState GetIgnitableState(float secondsIgniting)
        {
            if (IsBurning || !HasBurnableFuel()) return EnumIgniteState.NotIgnitablePreventDefault;
            return secondsIgniting > 3 ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
        }

        /// <summary>Lights the station from a firestarter or torch.</summary>
        public void Ignite()
        {
            if (Api?.Side != EnumAppSide.Server) return;
            if (IsBurning) return;
            TryBurnNextFuel();
        }

        /// <summary>Puts the fire out, e.g. when it's doused with water.</summary>
        public void Extinguish()
        {
            fuelBurnTime = 0;
            maxFuelBurnTime = 0;
            SetBurnState("cold");
            MarkDirty(true);
        }

        public void SetBurnState(string state)
        {
            if (Block == null || Block.Variant["burnstate"] == state) return;
            Block block = Api.World.GetBlock(Block.CodeWithVariant("burnstate", state));
            if (block == null) return;
            Api.World.BlockAccessor.ExchangeBlock(block.Id, Pos);
            Block = block;
        }

        public float GetHeatStrength(IWorldAccessor world, BlockPos heatSourcePos, BlockPos heatReceiverPos)
        {
            return IsBurning ? 10 : 0;
        }

        /// <summary>Text for the fuel line of the GUI.</summary>
        public string GetFuelText()
        {
            string temp = temperature <= 25 ? Lang.Get("forgeandfeast:cold") : Lang.Get("forgeandfeast:degrees", (int)temperature);
            if (!IsBurning)
            {
                return Lang.Get(HasBurnableFuel() ? "forgeandfeast:fuel-notlit" : "forgeandfeast:fuel-none", temp);
            }
            int pct = maxFuelBurnTime > 0 ? (int)(100 * fuelBurnTime / maxFuelBurnTime) : 0;
            return Lang.Get("forgeandfeast:fuel-lit", temp, pct);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            ITreeAttribute invtree = new TreeAttribute();
            Inventory.ToTreeAttributes(invtree);
            tree["inventory"] = invtree;

            tree.SetFloat("pkTemp", temperature);
            tree.SetInt("pkFuelTemp", fuelTemperature);
            tree.SetFloat("pkBurn", fuelBurnTime);
            tree.SetFloat("pkMaxBurn", maxFuelBurnTime);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            if (Api != null) Inventory.AfterBlocksLoaded(Api.World);

            temperature = tree.GetFloat("pkTemp", 20);
            fuelTemperature = tree.GetInt("pkFuelTemp");
            fuelBurnTime = tree.GetFloat("pkBurn");
            maxFuelBurnTime = tree.GetFloat("pkMaxBurn");
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            dsc.AppendLine(GetFuelText());
        }
    }
}
