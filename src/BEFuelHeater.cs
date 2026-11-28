using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
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

        /// <summary>Players with this stove's window open. While any are, its doors stand open.</summary>
        readonly HashSet<string> openers = new HashSet<string>();
        public bool DoorsOpen => Block?.Variant?["doors"] == "open";
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
        /// <summary>A well-insulated firebox runs hotter than the fuel would in the open (kilns only).</summary>
        public float FuelTempMul = 1f;
        /// <summary>Set by the smoke/ventilation behaviour: 1.25 with a chimney, 0.6 in a smoky room.</summary>
        public float VentMul = 1f;
        /// <summary>How fast this station fills a closed room with smoke (1 = normal).</summary>
        public float SmokeMul = 1f;
        /// <summary>Smoke multiplier right now. Stations can smoke more while they work.</summary>
        public virtual float CurrentSmokeMul => SmokeMul;
        /// <summary>How smoky the fuel burning right now is: wood 1, charcoal and coke very little, peat and hay a lot.</summary>
        public float FuelSmoke = 1f;

        float prevTemperature = 20;

        /// <summary>
        /// Draws the range without its built-in stovepipe stub, for when a real stovepipe is attached.
        /// Returns null if that can't be done, so the normal model is used.
        /// </summary>
        protected MeshData GetMeshWithoutStub(ITesselatorAPI tesselator)
        {
            return GetMeshWithout(tesselator, new[] { "pipe" });
        }

        /// <summary>The block's model with the named elements left out. Null if that can't be done.</summary>
        protected MeshData GetMeshWithout(ITesselatorAPI tesselator, string[] hide)
        {
            CompositeShape cs = Block?.Shape;
            if (cs?.Base == null) return null;
            Dictionary<int, MeshData> cache = ObjectCacheUtil.GetOrCreate(Api, "forgeandfeast-without-" + string.Join(",", hide), () => new Dictionary<int, MeshData>());
            lock (cache)
            {
                MeshData mesh;
                if (cache.TryGetValue(Block.Id, out mesh)) return mesh;

                AssetLocation loc = cs.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                Shape shape = Shape.TryGet(Api, loc);
                if (shape?.Elements == null) return null;

                List<string> keep = new List<string>();
                for (int i = 0; i < shape.Elements.Length; i++)
                {
                    string name = shape.Elements[i].Name;
                    if (Array.IndexOf(hide, name) >= 0) continue;
                    keep.Add(name);
                }

                tesselator.TesselateShape(Block, shape, out mesh, new Vec3f(cs.rotateX, cs.rotateY, cs.rotateZ), null, keep.ToArray());
                cache[Block.Id] = mesh;
                return mesh;
            }
        }

        public bool IsBurning => fuelBurnTime > 0;

        /// <summary>Height (in blocks) the smoke wisp leaves the stove's own flue stub at.</summary>
        public virtual double StubTop => 0.95;

        public abstract ItemSlot FuelSlot { get; }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            ReadTierStats();

            if (api.Side == EnumAppSide.Server)
            {
                RegisterGameTickListener(OnServerTick, 100);
                RegisterGameTickListener(On500msTick, 500);
                RegisterGameTickListener(PruneOpeners, 2000);
                RegisterGameTickListener(dt => RefillFromStore(), 1000);
                // Safety net: a lit-looking block that isn't actually burning goes dark again
                RegisterDelayedCallback(dt =>
                {
                    if (Block?.Variant?["burnstate"] == "lit" && !IsBurning)
                    {
                        SetBurnState("cold");
                        MarkDirty(true);
                    }
                }, 1000);
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
            SmokeMul = attr["smokeMul"].AsFloat(1f);
            FuelTempMul = attr["fuelTempMul"].AsFloat(1f);
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

            float target = IsBurning ? Math.Min(fuelTemperature * FuelTempMul, MaxTemp) : EnvironmentTemperature();
            float rate = IsBurning ? HeatMul * VentMul : 1f;
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

        // ---- Fuel store: bronze, iron and steel stoves hold 1, 2 or 3 more stacks of fuel behind the fuel slot ----

        ItemStack fuelStore;

        /// <summary>0 for copper, 1 bronze, 2 iron, 3 steel.</summary>
        public int TierIndex
        {
            get
            {
                string t = Block?.Variant?["tier"];
                return t == "tinbronze" ? 1 : t == "iron" ? 2 : t == "steel" ? 3 : 0;
            }
        }

        public int FuelStoreCount => fuelStore?.StackSize ?? 0;

        /// <summary>The store split into full stacks, for showing as slots.</summary>
        public ItemStack[] FuelStoreStacks(int count)
        {
            ItemStack[] r = new ItemStack[count];
            if (fuelStore == null) return r;
            int left = fuelStore.StackSize, max = Math.Max(1, fuelStore.Collectible.MaxStackSize);
            for (int i = 0; i < count && left > 0; i++)
            {
                int n = i == count - 1 ? left : Math.Min(max, left);
                r[i] = fuelStore.Clone();
                r[i].StackSize = n;
                left -= n;
            }
            return r;
        }

        int FuelStoreCap(CollectibleObject c) => TierIndex * Math.Max(1, c?.MaxStackSize ?? 64);

        /// <summary>
        /// Puts a whole held stack of fuel in: the fuel slot first, then whatever is left into the fuel store, up to
        /// what this tier holds. Returns how many went in.
        /// </summary>
        public int AddFuel(ItemSlot from, IWorldAccessor world)
        {
            if (from?.Itemstack == null) return 0;
            int moved = 0;
            ItemStackMoveOperation op = new ItemStackMoveOperation(world, EnumMouseButton.Left, 0, EnumMergePriority.DirectMerge, from.StackSize);
            from.TryPutInto(FuelSlot, ref op);
            moved += op.MovedQuantity;

            if (from.Itemstack != null && TierIndex > 0)
            {
                ItemStack held = from.Itemstack;
                bool same = fuelStore == null || fuelStore.Equals(world, held, GlobalConstants.IgnoredStackAttributes);
                if (same)
                {
                    int n = Math.Min(FuelStoreCap(held.Collectible) - FuelStoreCount, held.StackSize);
                    if (n > 0)
                    {
                        ItemStack taken = from.TakeOut(n);
                        if (fuelStore == null) fuelStore = taken;
                        else fuelStore.StackSize += n;
                        moved += n;
                    }
                }
            }
            if (moved > 0)
            {
                from.MarkDirty();
                MarkDirty(true);
            }
            return moved;
        }

        /// <summary>Keeps the fuel slot topped up from the store.</summary>
        void RefillFromStore()
        {
            if (fuelStore == null || Api == null) return;
            ItemSlot slot = FuelSlot;
            if (slot == null) return;
            int room;
            if (slot.Empty) room = fuelStore.Collectible.MaxStackSize;
            else if (slot.Itemstack.Equals(Api.World, fuelStore, GlobalConstants.IgnoredStackAttributes)) room = slot.Itemstack.Collectible.MaxStackSize - slot.StackSize;
            else return;
            int n = Math.Min(room, fuelStore.StackSize);
            if (n <= 0) return;
            if (slot.Empty)
            {
                ItemStack put = fuelStore.Clone();
                put.StackSize = n;
                slot.Itemstack = put;
            }
            else slot.Itemstack.StackSize += n;
            fuelStore.StackSize -= n;
            if (fuelStore.StackSize <= 0) fuelStore = null;
            slot.MarkDirty();
            MarkDirty(true);
        }

        public override void OnBlockBroken(IPlayer byPlayer = null)
        {
            base.OnBlockBroken(byPlayer);
            if (fuelStore == null || Api == null || Api.Side != EnumAppSide.Server) return;
            int max = Math.Max(1, fuelStore.Collectible.MaxStackSize);
            while (fuelStore.StackSize > 0)
            {
                int n = Math.Min(max, fuelStore.StackSize);
                ItemStack drop = fuelStore.Clone();
                drop.StackSize = n;
                fuelStore.StackSize -= n;
                Api.World.SpawnItemEntity(drop, Pos.ToVec3d().Add(0.5, 0.5, 0.5));
            }
            fuelStore = null;
        }

        public bool HasBurnableFuel()
        {
            CombustibleProperties props = FuelSlot.Itemstack?.Collectible?.CombustibleProps;
            return props != null && props.BurnTemperature > 0 && props.BurnDuration > 0;
        }

        /// <summary>
        /// Burns through fuel for a stretch of time that was skipped (e.g. /time add), lighting the next piece
        /// from the fuel slot and store as each runs out. Returns how many of those seconds the fire lasted.
        /// </summary>
        protected float BurnFor(float seconds)
        {
            float covered = 0;
            int guard = 0;
            while (seconds > 0 && fuelBurnTime > 0 && guard++ < 10000)
            {
                float use = Math.Min(seconds, fuelBurnTime);
                fuelBurnTime -= use;
                seconds -= use;
                covered += use;
                if (fuelBurnTime > 0) break;
                fuelBurnTime = 0;
                maxFuelBurnTime = 0;
                RefillFromStore();
                if (!TryBurnNextFuel())
                {
                    SetBurnState("cold");
                    MarkDirty(true);
                    break;
                }
            }
            return covered;
        }

        /// <summary>Lights the next piece of fuel. Returns false if there's nothing burnable left.</summary>
        public bool TryBurnNextFuel()
        {
            if (!HasBurnableFuel()) return false;

            CombustibleProperties props = FuelSlot.Itemstack.Collectible.CombustibleProps;
            maxFuelBurnTime = fuelBurnTime = props.BurnDuration * BurnMul;
            fuelTemperature = props.BurnTemperature;
            FuelSmoke = SmokeSystem.FuelSmoke(FuelSlot.Itemstack.Collectible);

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

        public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
        {
            base.OnReceivedClientPacket(fromPlayer, packetid, data);
            if (packetid == (int)EnumBlockEntityPacketId.Open || packetid == (int)EnumBlockContainerPacketId.OpenInventory)
            {
                openers.Add(fromPlayer.PlayerUID);
                SetDoors(true, true);
                XSkillsCompat.SetOwner(this, fromPlayer);
            }
            else if (packetid == FuelStoreView.PacketId)
            {
                ItemSlot mouse = fromPlayer?.InventoryManager?.MouseItemSlot;
                if (mouse == null) return;
                if (!mouse.Empty) AddFuel(mouse, Api.World);
                else if (fuelStore != null)
                {
                    int n = Math.Min(Math.Max(1, fuelStore.Collectible.MaxStackSize), fuelStore.StackSize);
                    ItemStack take = fuelStore.Clone();
                    take.StackSize = n;
                    fuelStore.StackSize -= n;
                    if (fuelStore.StackSize <= 0) fuelStore = null;
                    mouse.Itemstack = take;
                    mouse.MarkDirty();
                    MarkDirty(true);
                }
            }
            else if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                openers.Remove(fromPlayer.PlayerUID);
                if (openers.Count == 0) SetDoors(false, true);
            }
        }

        /// <summary>Closes the doors when nobody is using the stove any more (or after a reload).</summary>
        void PruneOpeners(float dt)
        {
            if (openers.Count > 0)
            {
                string[] ids = new string[openers.Count];
                openers.CopyTo(ids);
                foreach (string id in ids)
                {
                    IPlayer p = Api.World.PlayerByUid(id);
                    if (p?.Entity == null || p.Entity.Pos.DistanceTo(Pos.ToVec3d()) > 10) openers.Remove(id);
                }
                if (openers.Count == 0) SetDoors(false, true);
            }
            else if (DoorsOpen)
            {
                SetDoors(false, false);
            }
        }

        public void SetDoors(bool open, bool sound)
        {
            if (Block == null || Api == null || Api.Side != EnumAppSide.Server) return;
            if (Block.Variant?["doors"] == null) return;   // no doors on this station
            string want = open ? "open" : "closed";
            if (Block.Variant["doors"] == want) return;
            Block block = Api.World.GetBlock(Block.CodeWithVariant("doors", want));
            if (block == null) return;
            Api.World.BlockAccessor.ExchangeBlock(block.Id, Pos);
            Block = block;
            MarkDirty(true);
            if (sound)
            {
                Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/metaldoor"),
                    Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f, 0.9f);
            }
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
                return Lang.Get(HasBurnableFuel() ? "forgeandfeast:fuel-notlit" : "forgeandfeast:fuel-none", temp) + StoreText();
            }
            int pct = maxFuelBurnTime > 0 ? (int)(100 * fuelBurnTime / maxFuelBurnTime) : 0;
            return Lang.Get("forgeandfeast:fuel-lit", temp, pct) + StoreText();
        }

        string StoreText()
        {
            if (TierIndex == 0) return "";
            return "\n" + Lang.Get("forgeandfeast:fuel-store-hint");
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
            tree.SetFloat("pkFuelSmoke", FuelSmoke);
            if (fuelStore != null) tree.SetItemstack("ffFuelStore", fuelStore);
            else tree.RemoveAttribute("ffFuelStore");
        }

        string lastDisplayKey;

        /// <summary>Codes of everything drawn as part of the stove, so the client knows when to redraw.</summary>
        protected virtual string DisplayKey() => "";

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            if (Api != null) Inventory.AfterBlocksLoaded(Api.World);
            if (Api != null && Api.Side == EnumAppSide.Client)
            {
                // Redraw when what's shown on or in the stove changes
                string key = DisplayKey();
                if (key != lastDisplayKey)
                {
                    lastDisplayKey = key;
                    Api.World.BlockAccessor.MarkBlockDirty(Pos);
                }
            }

            temperature = tree.GetFloat("pkTemp", 20);
            fuelTemperature = tree.GetInt("pkFuelTemp");
            fuelBurnTime = tree.GetFloat("pkBurn");
            maxFuelBurnTime = tree.GetFloat("pkMaxBurn");
            FuelSmoke = tree.GetFloat("pkFuelSmoke", 1f);
            fuelStore = tree.GetItemstack("ffFuelStore");
            if (fuelStore != null && worldForResolving != null && !fuelStore.ResolveBlockOrItem(worldForResolving)) fuelStore = null;
        }

        /// <summary>Extra "time left" lines for what this stove is doing, shown when you look at it.</summary>
        public virtual string GetTimeLeftText() { return ""; }

        float StackBurnSeconds(ItemStack stack)
        {
            CombustibleProperties props = stack?.Collectible?.CombustibleProps;
            if (props == null || props.BurnTemperature <= 0 || props.BurnDuration <= 0) return 0;
            return props.BurnDuration * BurnMul * stack.StackSize;
        }

        /// <summary>Seconds the fire will keep going: the piece burning now, the fuel slot and the spare store.</summary>
        public float FuelSecondsLeft()
        {
            return fuelBurnTime + StackBurnSeconds(FuelSlot?.Itemstack) + StackBurnSeconds(fuelStore);
        }

        /// <summary>"45 s", "3 min 20 s" or "1 h 5 min".</summary>
        public static string FormatSeconds(float seconds)
        {
            int s = (int)Math.Ceiling(Math.Max(0, seconds));
            if (s < 60) return Lang.Get("forgeandfeast:time-seconds", s);
            int m = s / 60;
            if (m < 60) return Lang.Get("forgeandfeast:time-minutes", m, s % 60);
            return Lang.Get("forgeandfeast:time-hours", m / 60, m % 60);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            dsc.AppendLine(GetFuelText());
            if (IsBurning || HasBurnableFuel()) dsc.AppendLine(Lang.Get("forgeandfeast:fuel-left", FormatSeconds(FuelSecondsLeft())));
            string left = GetTimeLeftText();
            if (left.Length > 0) dsc.AppendLine(left);
            GetBehavior<BEBehaviorSmokeVent>()?.GetBlockInfo(forPlayer, dsc);
        }
    }
}
