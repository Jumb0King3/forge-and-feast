using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;

namespace ForgeAndFeast
{
    /// <summary>Slot 0 fuel, slot 1 the crucible, slots 2-5 the crucible's ingredients.</summary>
    public class InventoryCrucibleFurnace : InventoryBase
    {
        readonly ItemSlot[] slots;
        readonly BurnerSlots cook;
        /// <summary>Bigger furnaces fit more in the crucible: its slot size times this.</summary>
        public float CapacityMul = 1f;

        public InventoryCrucibleFurnace(string inventoryID, ICoreAPI api) : base(inventoryID, api)
        {
            slots = GenEmptySlots(6);
            cook = new BurnerSlots(this, new ItemSlot[] { slots[2], slots[3], slots[4], slots[5] });
            baseWeight = 4f;
        }

        public ItemSlot FuelSlot => slots[0];
        public ItemSlot CrucibleSlot => slots[1];
        public BurnerSlots Cook => cook;
        public bool HasCrucible => CrucibleSlot.Itemstack?.Collectible is BlockSmeltingContainer;
        public bool HasMolten => CrucibleSlot.Itemstack?.Collectible is BlockSmeltedContainer;

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
            return new ItemSlotSurvival(this);
        }

        public override bool CanContain(ItemSlot sinkSlot, ItemSlot sourceSlot)
        {
            int id = GetSlotId(sinkSlot);
            CollectibleObject c = sourceSlot?.Itemstack?.Collectible;
            if (id >= 2 && !HasCrucible) return false;
            if (id == 1 && c != null && !(c is BlockSmeltingContainer) && !(c is BlockSmeltedContainer)) return false;
            return base.CanContain(sinkSlot, sourceSlot);
        }

        public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
        {
            int id = GetSlotId(targetSlot);
            ItemStack stack = sourceSlot.Itemstack;
            CombustibleProperties props = stack?.Collectible?.CombustibleProps;
            if (id == 0) return props != null && props.BurnTemperature > 0 ? 2.2f : 0;
            if (id == 1) return stack?.Collectible is BlockSmeltingContainer || stack?.Collectible is BlockSmeltedContainer ? 2.4f : 0;
            if (!HasCrucible || props == null || props.SmeltedStack == null) return 0;
            return 2.2f;
        }

        public override void LateInitialize(string inventoryID, ICoreAPI api)
        {
            base.LateInitialize(inventoryID, api);
            UpdateCookingSlots();
        }

        public override void DidModifyItemSlot(ItemSlot slot, ItemStack extractedStack = null)
        {
            base.DidModifyItemSlot(slot, extractedStack);
            if (GetSlotId(slot) == 1)
            {
                if (!HasCrucible) DiscardCookingSlots();
                UpdateCookingSlots();
            }
        }

        /// <summary>Ingredient slot size follows the crucible, like the vanilla firepit, times the furnace's capacity.</summary>
        public void UpdateCookingSlots()
        {
            ItemStack cr = CrucibleSlot.Itemstack;
            int maxStack = 0;
            if (cr?.Collectible is BlockSmeltingContainer)
                maxStack = (int)Math.Round((cr.ItemAttributes?["maxContainerSlotStackSize"].AsInt(32) ?? 32) * CapacityMul);
            for (int j = 0; j < 4; j++) slots[2 + j].MaxSlotStackSize = maxStack;
        }

        void DiscardCookingSlots()
        {
            if (Api == null || Api.Side != EnumAppSide.Server || Pos == null) return;
            Vec3d droppos = Pos.ToVec3d().Add(0.5, 1.1, 0.5);
            for (int j = 0; j < 4; j++)
            {
                ItemSlot s = slots[2 + j];
                if (s.Empty) continue;
                Api.World.SpawnItemEntity(s.Itemstack, droppos);
                s.Itemstack = null;
                s.MarkDirty();
            }
        }

        public override void FromTreeAttributes(ITreeAttribute tree)
        {
            SlotTreeHelper.ReadSlots(this, tree, slots);
            if (Api != null) UpdateCookingSlots();
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            SlotsToTreeAttributes(slots, tree);
        }
    }

    /// <summary>
    /// The Crucible Furnace melts metal in a crucible, then tips forward and pours it into whatever mould
    /// stands in front of its spout (anything the game lets you pour into: tool and ingot moulds, and other
    /// mods' moulds). Pour by hand with the crank on its gearbox, or plug an axle into the gearbox, engage the
    /// clutch, and it pours on its own whenever the metal is molten and an empty mould is waiting.
    /// </summary>
    public class BECrucibleFurnace : BEFuelHeater
    {
        public const float MaxTilt = 35f;
        public const float PourTilt = 28f;
        /// <summary>Metal units poured per server tick (100 ms).</summary>
        const int PourPerTick = 6;

        readonly InventoryCrucibleFurnace inventory;
        float progress;
        float tilt;
        long crankUntil;
        bool fillBegun;
        int pourSide;
        int noProgressTicks;
        int sideTries;
        long retryAt;
        long mouldSyncAt;
        ILiquidMetalSink lastSink;

        /// <summary>Synced to clients.</summary>
        public float TiltTarget;
        public bool Pouring;
        public bool ClutchEngaged;
        public bool Slag;
        public bool Powered;

        BEBehaviorMPConsumer mpc;
        FurnaceDrumRenderer renderer;
        long lastCrankSound;

        public BECrucibleFurnace()
        {
            inventory = new InventoryCrucibleFurnace(null, null);
            inventory.SlotModified += id => { if (Api != null) MarkDirty(Api.Side == EnumAppSide.Server); };
        }

        public override InventoryBase Inventory => inventory;
        public override string InventoryClassName => "cruciblefurnace";
        public override ItemSlot FuelSlot => inventory.FuelSlot;
        public InventoryCrucibleFurnace FurnaceInventory => inventory;

        /// <summary>The way the spout faces.</summary>
        public BlockFacing Front
        {
            get
            {
                string side = Block?.Variant?["side"];
                return (side == null ? null : BlockFacing.FromCode(side)) ?? BlockFacing.NORTH;
            }
        }
        public float RotYDeg => Block?.Shape?.rotateY ?? 0;

        protected override void ReadTierStats()
        {
            base.ReadTierStats();
            inventory.CapacityMul = Block?.Attributes?["capacityMul"].AsFloat(1f) ?? 1f;
            inventory.Pos = Pos;
            inventory.UpdateCookingSlots();
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            inventory.Pos = Pos;
            mpc = GetBehavior<BEBehaviorMPConsumer>();
            if (api.Side == EnumAppSide.Client)
            {
                ICoreClientAPI capi = (ICoreClientAPI)api;
                renderer = new FurnaceDrumRenderer(capi, this);
                capi.Event.RegisterRenderer(renderer, EnumRenderStage.Opaque, "forgeandfeast-furnacedrum");
                RegisterGameTickListener(OnClientTick, 50);
            }
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            DisposeRenderer();
        }

        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();
            DisposeRenderer();
        }

        void DisposeRenderer()
        {
            if (renderer == null) return;
            (Api as ICoreClientAPI)?.Event.UnregisterRenderer(renderer, EnumRenderStage.Opaque);
            renderer.Dispose();
            renderer = null;
        }

        // ---------------------------------------------------------------- melting

        protected override bool HasActiveWork()
        {
            return (inventory.HasCrucible && HasIngredients()) || inventory.HasMolten || tilt > 0 || TiltTarget > 0;
        }

        bool HasIngredients()
        {
            ItemSlot[] cs = inventory.Cook.Slots;
            for (int j = 0; j < cs.Length; j++) if (!cs[j].Empty) return true;
            return false;
        }

        protected override void ProcessContents(float dt)
        {
            ItemSlot slot = inventory.CrucibleSlot;
            ItemStack cr = slot.Itemstack;
            if (cr?.Collectible is BlockSmeltingContainer)
            {
                Melt(dt);
            }
            else
            {
                progress = 0;
                if (cr?.Collectible is BlockSmeltedContainer)
                {
                    // Molten metal stays as hot as the furnace
                    float t = cr.Collectible.GetTemperature(Api.World, cr);
                    float nt = ChangeTemperature(t, temperature, dt * 2f);
                    if (Math.Abs(nt - t) > 0.05f) cr.Collectible.SetTemperature(Api.World, cr, nt);
                }
            }
            UpdateTilt(dt);
            warmTimer += dt;
            if (warmTimer >= 2f)
            {
                KeepMouldWarm();
                warmTimer = 0;
            }
        }

        float warmTimer;

        /// <summary>
        /// A big mould (an anvil takes 900 units) needs more than one crucible of metal. While the furnace burns,
        /// the mould in front of the spout is kept hot enough that its metal doesn't set until it's full, so it can
        /// be filled over several melts. A full mould is left to cool as normal.
        /// </summary>
        void KeepMouldWarm()
        {
            if (!IsBurning || Api?.Side != EnumAppSide.Server) return;
            BlockEntity be = Api.World.BlockAccessor.GetBlockEntity(Pos.AddCopy(Front));
            if (be is BlockEntityToolMold tm)
            {
                int need = tm.Block?.Attributes?["requiredUnits"].AsInt(100) ?? 100;
                if (!tm.Shattered && tm.MetalContent != null && tm.FillLevel > 0 && tm.FillLevel < need && Warm(tm.MetalContent)) tm.MarkDirty(true);
            }
            else if (be is BlockEntityIngotMold im)
            {
                bool changed = false;
                int need = im.RequiredUnits > 0 ? im.RequiredUnits : 100;
                if (!im.ShatteredLeft && im.ContentsLeft != null && im.FillLevelLeft > 0 && im.FillLevelLeft < need) changed |= Warm(im.ContentsLeft);
                if (!im.ShatteredRight && im.ContentsRight != null && im.FillLevelRight > 0 && im.FillLevelRight < need) changed |= Warm(im.ContentsRight);
                if (changed) im.MarkDirty(true);
            }
        }

        /// <summary>Holds a mould's metal just above its melting point (as far as the furnace's own heat allows).</summary>
        bool Warm(ItemStack metal)
        {
            float melt = metal.Collectible.CombustibleProps?.MeltingPoint ?? 0;
            if (melt <= 0) return false;
            float target = Math.Min(melt + 100, temperature);
            if (target <= melt) return false;
            float t = metal.Collectible.GetTemperature(Api.World, metal);
            if (t >= target) return false;
            metal.Collectible.SetTemperature(Api.World, metal, target);
            return true;
        }

        void Melt(float dt)
        {
            ItemSlot slot = inventory.CrucibleSlot;
            BurnerSlots prov = inventory.Cook;
            ItemStack cr = slot.Itemstack;

            bool any = false;
            float minTemp = float.MaxValue;
            for (int j = 0; j < prov.Slots.Length; j++)
            {
                ItemStack s = prov.Slots[j].Itemstack;
                if (s == null) continue;
                any = true;
                float t = s.Collectible.GetTemperature(Api.World, s);
                float nt = ChangeTemperature(t, temperature, dt * 2f);
                if (Math.Abs(nt - t) > 0.05f) s.Collectible.SetTemperature(Api.World, s, nt);
                minTemp = Math.Min(minTemp, nt);
            }

            if (!any || !cr.Collectible.CanSmelt(Api.World, prov, cr, null))
            {
                progress = 0;
                return;
            }

            float meltingPoint = cr.Collectible.GetMeltingPoint(Api.World, prov, slot);
            float duration = Math.Max(5f, cr.Collectible.GetMeltingDuration(Api.World, prov, slot));

            if (minTemp >= meltingPoint) progress += dt * SpeedMul * VentMul;
            else if (progress > 0) progress = Math.Max(0, progress - dt);

            if (progress >= duration)
            {
                // The game puts the molten metal in a new container; keep it in the furnace
                DummySlot result = new DummySlot();
                cr.Collectible.DoSmelt(Api.World, prov, slot, result);
                slot.Itemstack = result.Itemstack;
                slot.MarkDirty();
                for (int j = 0; j < prov.Slots.Length; j++) prov.Slots[j].MarkDirty();
                progress = 0;
                if (ForgeAndFeastConfig.Current.CrucibleFurnaceSlag && inventory.HasMolten) Slag = true;
                MarkDirty(true);
            }
        }

        // ---------------------------------------------------------------- pouring

        /// <summary>Molten metal ready to pour: what it is, how many units, and how hot.</summary>
        bool Molten(out KeyValuePair<ItemStack, int> contents, out float temp)
        {
            contents = default(KeyValuePair<ItemStack, int>);
            temp = 0;
            ItemStack st = inventory.CrucibleSlot.Itemstack;
            BlockSmeltedContainer c = st?.Collectible as BlockSmeltedContainer;
            if (c == null) return false;
            contents = c.GetContents(Api.World, st);
            if (contents.Key == null || contents.Value <= 0) return false;
            if (c.HasSolidifed(st, contents.Key, Api.World)) return false;
            temp = st.Collectible.GetTemperature(Api.World, st);
            return true;
        }

        ILiquidMetalSink FrontSink()
        {
            return Api.World.BlockAccessor.GetBlockEntity(Pos.AddCopy(Front)) as ILiquidMetalSink;
        }

        void UpdateTilt(float dt)
        {
            long now = Api.World.ElapsedMilliseconds;
            KeyValuePair<ItemStack, int> contents;
            float temp;
            bool molten = Molten(out contents, out temp);
            ILiquidMetalSink sink = molten ? FrontSink() : null;
            bool sinkReady = sink != null && now >= retryAt && sink.CanReceiveAny && sink.CanReceive(contents.Key);

            bool crank = now < crankUntil;
            float speed = mpc?.TrueSpeed ?? 0;
            bool powered = speed > 0.05f;
            bool auto = ClutchEngaged && powered && sinkReady;

            float target = crank || auto ? MaxTilt : 0;
            float rate = auto && !crank ? 30f * GameMath.Clamp(speed, 0.4f, 1.5f) : 45f;
            if (tilt < target) tilt = Math.Min(target, tilt + rate * dt);
            else if (tilt > target) tilt = Math.Max(target, tilt - 45f * dt);
            if (mpc != null) mpc.Resistance = auto ? 0.3f : 0.02f;

            bool pouring = false;
            if (tilt >= PourTilt && sinkReady) pouring = PourStep(sink, contents, temp, now);
            if (tilt < PourTilt || !sinkReady) fillBegun = false;

            if (pouring && !Pouring)
            {
                // One soft pour sound as the metal starts to run, not one per retry
                Api.World.PlaySoundAt(new AssetLocation("game:sounds/pourmetal"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 8f, 0.3f);
            }
            if (!pouring && Pouring && lastSink != null)
            {
                lastSink.OnPourOver();
                lastSink = null;
            }

            if (target != TiltTarget || pouring != Pouring || powered != Powered)
            {
                TiltTarget = target;
                Pouring = pouring;
                Powered = powered;
                MarkDirty();
            }
        }

        bool PourStep(ILiquidMetalSink sink, KeyValuePair<ItemStack, int> contents, float temp, long now)
        {
            ItemSlot slot = inventory.CrucibleSlot;
            ItemStack st = slot.Itemstack;
            int units = contents.Value;

            if (!fillBegun)
            {
                if (Slag)
                {
                    // Skipped clearing the slag: it goes in with the pour and the casting comes out short
                    units = Math.Max(0, units - (int)Math.Ceiling(units * 0.1));
                    st.Attributes.SetInt("units", units);
                    Slag = false;
                    if (units <= 0) { EmptyCrucible(); return false; }
                }
                // An ingot mould can hold two ingots side by side; try the other half if this one is full
                sink.BeginFill(new Vec3d(pourSide == 0 ? 0.3 : 0.7, 0.2, 0.5));
                fillBegun = true;
                noProgressTicks = 0;
            }

            int give = Math.Min(PourPerTick, units);
            int left = give;
            sink.ReceiveLiquidMetal(contents.Key, ref left, temp);
            int moved = give - left;
            if (moved <= 0)
            {
                if (++noProgressTicks >= 5)
                {
                    pourSide ^= 1;
                    fillBegun = false;
                    if (++sideTries >= 2)
                    {
                        // Nothing takes the metal: wait a few seconds before trying again
                        sideTries = 0;
                        retryAt = now + 5000;
                    }
                }
                return false;
            }
            noProgressTicks = 0;
            sideTries = 0;

            // A pour by hand runs on both sides; this one only runs on the server, so send the mould's new
            // fill level to players a few times a second (and once more when the pour stops)
            lastSink = sink;
            if (now >= mouldSyncAt)
            {
                (sink as BlockEntity)?.MarkDirty(true);
                mouldSyncAt = now + 300;
            }

            units -= moved;
            st.Attributes.SetInt("units", units);
            if (units <= 0) EmptyCrucible();
            slot.MarkDirty();
            MarkDirty();
            return true;
        }

        /// <summary>The crucible is poured out: it goes back to being an empty fired crucible.</summary>
        void EmptyCrucible()
        {
            ItemSlot slot = inventory.CrucibleSlot;
            CollectibleObject c = slot.Itemstack?.Collectible;
            string emptied = c?.Attributes?["emptiedBlockCode"].AsString(null);
            Block eb = emptied == null ? null : Api.World.GetBlock(AssetLocation.Create(emptied, c.Code.Domain));
            slot.Itemstack = eb != null ? new ItemStack(eb, 1) : null;
            slot.MarkDirty();
            Slag = false;
            MarkDirty(true);
        }

        /// <summary>Called every tick while a player holds the crank (both sides).</summary>
        public void TurnCrank(IPlayer byPlayer)
        {
            if (Api.Side == EnumAppSide.Server)
            {
                crankUntil = Api.World.ElapsedMilliseconds + 300;
                return;
            }
            long now = Api.World.ElapsedMilliseconds;
            if (now - lastCrankSound > 1500)
            {
                lastCrankSound = now;
                Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/gearbox_turn"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, false, 8f, 0.2f);
            }
        }

        public void ReleaseCrank()
        {
            if (Api?.Side == EnumAppSide.Server) crankUntil = 0;
        }

        public void ToggleClutch()
        {
            ClutchEngaged = !ClutchEngaged;
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/latch"), Pos.X + 0.5, Pos.Y + 0.7, Pos.Z + 0.5, null, true, 8f, 0.5f);
            MarkDirty(true);
        }

        /// <summary>Skims the slag off with tongs. Uses one point of the tongs' durability.</summary>
        public void ClearSlag(IPlayer byPlayer, ItemSlot tongs)
        {
            if (!Slag) return;
            Slag = false;
            tongs.Itemstack?.Collectible.DamageItem(Api.World, byPlayer.Entity, tongs, 1, true);
            tongs.MarkDirty();
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/anvilhit1"), Pos.X + 0.5, Pos.Y + 0.9, Pos.Z + 0.5, null, true, 12f, 0.5f);
            MarkDirty(true);
        }

        // ---------------------------------------------------------------- client: stream of metal

        void OnClientTick(float dt)
        {
            if (!Pouring || renderer == null || renderer.Angle < PourTilt) return;

            // Spout tip in model space, tipped with the drum, then turned to the block's facing
            Vec3d tip = ToWorld(TipAt(renderer.Angle));
            BlockFacing f = Front;
            Vec3d target = new Vec3d(Pos.X + 0.5 + f.Normali.X * 0.8, Pos.Y + 0.15, Pos.Z + 0.5 + f.Normali.Z * 0.8);

            SimpleParticleProperties stream = new SimpleParticleProperties(4, 6,
                ColorUtil.ToRgba(255, 255, 140, 40),
                tip.AddCopy(-0.03, -0.03, -0.03), tip.AddCopy(0.03, 0, 0.03),
                new Vec3f((float)(target.X - tip.X) * 1.2f, -1.5f, (float)(target.Z - tip.Z) * 1.2f),
                new Vec3f((float)(target.X - tip.X) * 1.4f, -1.2f, (float)(target.Z - tip.Z) * 1.4f),
                0.45f, 1f, 0.25f, 0.4f, EnumParticleModel.Cube);
            stream.VertexFlags = 255;
            Api.World.SpawnParticles(stream);

            SimpleParticleProperties sparks = new SimpleParticleProperties(0, 2,
                ColorUtil.ToRgba(255, 255, 169, 83),
                target.AddCopy(-0.15, 0, -0.15), target.AddCopy(0.15, 0.1, 0.15),
                new Vec3f(-1.5f, 1f, -1.5f), new Vec3f(1.5f, 3f, 1.5f),
                0.6f, 1f, 0.1f, 0.2f, EnumParticleModel.Cube);
            sparks.VertexFlags = 255;
            Api.World.SpawnParticles(sparks);
        }

        /// <summary>The spout's lip in block sixteenths, tipped forward by the given angle about the trunnions.</summary>
        static Vec3d TipAt(float angleDeg)
        {
            double a = -angleDeg * GameMath.DEG2RAD;
            double y = 10.6 - 8.5, z = -1.0 - 8.0;
            double ny = y * Math.Cos(a) - z * Math.Sin(a);
            double nz = y * Math.Sin(a) + z * Math.Cos(a);
            return new Vec3d(8, ny + 8.5, nz + 8.0);
        }

        /// <summary>Model coordinates (sixteenths, facing north) to world coordinates for this block.</summary>
        Vec3d ToWorld(Vec3d m)
        {
            double x = m.X / 16.0 - 0.5, z = m.Z / 16.0 - 0.5;
            double r = RotYDeg * GameMath.DEG2RAD;
            double wx = x * Math.Cos(r) + z * Math.Sin(r);
            double wz = -x * Math.Sin(r) + z * Math.Cos(r);
            return new Vec3d(Pos.X + 0.5 + wx, Pos.Y + m.Y / 16.0, Pos.Z + 0.5 + wz);
        }

        // ---------------------------------------------------------------- drawing

        static string[] drumNames;

        /// <summary>The model's tipping parts ("d_" elements), drawn by the renderer instead of the chunk.</summary>
        string[] DrumNames()
        {
            if (drumNames != null) return drumNames;
            Shape shape = LoadShape();
            if (shape?.Elements == null) return new string[0];
            List<string> names = new List<string>();
            foreach (ShapeElement e in shape.Elements) if (e.Name != null && e.Name.StartsWith("d_")) names.Add(e.Name);
            drumNames = names.ToArray();
            return drumNames;
        }

        Shape LoadShape()
        {
            CompositeShape cs = Block?.Shape;
            if (cs?.Base == null) return null;
            AssetLocation loc = cs.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
            return Shape.TryGet(Api, loc);
        }

        /// <summary>The drum on its own, unrotated, for the renderer.</summary>
        public MeshData DrumMesh()
        {
            Shape shape = LoadShape();
            if (shape == null) return null;
            MeshData mesh;
            ((ICoreClientAPI)Api).Tesselator.TesselateShape(Block, shape, out mesh, new Vec3f(0, 0, 0), null, DrumNames());
            return mesh;
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            List<string> hide = new List<string>(DrumNames());
            hide.Add("cl_lever");
            hide.Add("cl_knob");
            MeshData body = GetMeshWithout(tesselator, hide.ToArray());
            if (body == null) return false;
            mesher.AddMeshData(body);

            // The clutch lever stands upright when off and leans over when engaged
            MeshData lever = ClutchMesh(tesselator);
            if (lever != null)
            {
                lever = lever.Clone();
                if (ClutchEngaged) lever.Rotate(new Vec3f(0.8f / 16f, 11.9f / 16f, 8f / 16f), 35 * GameMath.DEG2RAD, 0, 0);
                lever.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, RotYDeg * GameMath.DEG2RAD, 0);
                mesher.AddMeshData(lever);
            }
            return true;
        }

        /// <summary>Just the clutch lever, unrotated (cached per block).</summary>
        MeshData ClutchMesh(ITesselatorAPI tesselator)
        {
            Dictionary<int, MeshData> cache = ObjectCacheUtil.GetOrCreate(Api, "forgeandfeast-furnaceclutch", () => new Dictionary<int, MeshData>());
            lock (cache)
            {
                MeshData mesh;
                if (cache.TryGetValue(Block.Id, out mesh)) return mesh;
                Shape shape = LoadShape();
                if (shape == null) return null;
                tesselator.TesselateShape(Block, shape, out mesh, new Vec3f(0, 0, 0), null, new string[] { "cl_lever", "cl_knob" });
                cache[Block.Id] = mesh;
                return mesh;
            }
        }

        public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                toggleInventoryDialogClient(byPlayer, () =>
                {
                    return new GuiDialogCrucibleFurnace(Lang.Get("forgeandfeast:furnace-title"), Inventory, Pos, Api as ICoreClientAPI, this);
                });
            }
            return true;
        }

        // ---------------------------------------------------------------- text

        /// <summary>What the crucible is doing, for the window and the block info.</summary>
        public string GetMeltText()
        {
            ItemStack st = inventory.CrucibleSlot.Itemstack;
            if (st == null) return Lang.Get("forgeandfeast:furnace-nocrucible");
            if (st.Collectible is BlockSmeltedContainer)
            {
                KeyValuePair<ItemStack, int> c = ((BlockSmeltedContainer)st.Collectible).GetContents(Api.World, st);
                if (c.Key == null) return "";
                if (((BlockSmeltedContainer)st.Collectible).HasSolidifed(st, c.Key, Api.World)) return Lang.Get("forgeandfeast:furnace-solid", c.Key.GetName());
                return Lang.Get("forgeandfeast:furnace-molten", c.Value, c.Key.GetName());
            }
            if (!(st.Collectible is BlockSmeltingContainer)) return "";
            if (!HasIngredients()) return Lang.Get("forgeandfeast:kiln-crucible-empty");
            BurnerSlots prov = inventory.Cook;
            if (!st.Collectible.CanSmelt(Api.World, prov, st, null)) return Lang.Get("forgeandfeast:kiln-crucible-nomatch");
            float mp = st.Collectible.GetMeltingPoint(Api.World, prov, inventory.CrucibleSlot);
            if (temperature < mp) return Lang.Get("forgeandfeast:kiln-needs", (int)mp);
            float dur = Math.Max(5f, st.Collectible.GetMeltingDuration(Api.World, prov, inventory.CrucibleSlot));
            return Lang.Get("forgeandfeast:kiln-smelting", (int)GameMath.Clamp(100 * progress / dur, 0, 100));
        }

        /// <summary>Crank, clutch, power and slag, for the window and the block info.</summary>
        public string GetPourText()
        {
            StringBuilder sb = new StringBuilder();
            if (Slag) sb.AppendLine(Lang.Get("forgeandfeast:furnace-slag"));
            sb.AppendLine(Lang.Get(ClutchEngaged ? "forgeandfeast:furnace-clutch-on" : "forgeandfeast:furnace-clutch-off"));
            if (ClutchEngaged) sb.AppendLine(Lang.Get(Powered ? "forgeandfeast:furnace-powered" : "forgeandfeast:furnace-unpowered"));
            if (Pouring) sb.AppendLine(Lang.Get("forgeandfeast:furnace-pouring"));
            return sb.ToString().TrimEnd();
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            string melt = GetMeltText();
            if (melt.Length > 0) dsc.AppendLine(melt);
            dsc.AppendLine(GetPourText());
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("pkProg", progress);
            tree.SetFloat("ffTiltTarget", TiltTarget);
            tree.SetBool("ffPouring", Pouring);
            tree.SetBool("ffClutch", ClutchEngaged);
            tree.SetBool("ffSlag", Slag);
            tree.SetBool("ffPowered", Powered);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            bool oldClutch = ClutchEngaged;
            base.FromTreeAttributes(tree, worldForResolving);
            progress = tree.GetFloat("pkProg");
            TiltTarget = tree.GetFloat("ffTiltTarget");
            Pouring = tree.GetBool("ffPouring");
            ClutchEngaged = tree.GetBool("ffClutch");
            Slag = tree.GetBool("ffSlag");
            Powered = tree.GetBool("ffPowered");
            if (Api != null && Api.Side == EnumAppSide.Client && oldClutch != ClutchEngaged) Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }
    }

    /// <summary>Draws the furnace's drum, tipped forward by the current pour angle.</summary>
    public class FurnaceDrumRenderer : IRenderer
    {
        readonly ICoreClientAPI capi;
        readonly BECrucibleFurnace be;
        FuelStoreView fuelStoreView;
        readonly Matrixf mat = new Matrixf();
        MeshRef mesh;
        int meshBlockId;
        public float Angle;

        public FurnaceDrumRenderer(ICoreClientAPI capi, BECrucibleFurnace be)
        {
            this.capi = capi;
            this.be = be;
        }

        int atlasId;

        /// <summary>
        /// The block texture atlas the furnace's textures are on. Found from the block's own textures rather than
        /// the atlas list, whose List type the game's mod compiler can't see.
        /// </summary>
        int AtlasId(Block block)
        {
            if (atlasId != 0) return atlasId;
            if (block.Textures != null)
            {
                foreach (CompositeTexture ct in block.Textures.Values)
                {
                    if (ct?.Baked == null) continue;
                    TextureAtlasPosition tp = capi.BlockTextureAtlas.Positions[ct.Baked.TextureSubId];
                    if (tp != null) { atlasId = tp.atlasTextureId; break; }
                }
            }
            if (atlasId == 0) atlasId = capi.BlockTextureAtlas.UnknownTexturePosition.atlasTextureId;
            return atlasId;
        }

        public double RenderOrder => 0.5;
        public int RenderRange => 32;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            Block block = be.Block;
            if (block == null || be.Pos == null) return;
            if (mesh == null || meshBlockId != block.Id)
            {
                mesh?.Dispose();
                mesh = null;
                MeshData md = be.DrumMesh();
                if (md == null) return;
                mesh = capi.Render.UploadMesh(md);
                meshBlockId = block.Id;
            }

            float target = be.TiltTarget;
            float step = 45f * deltaTime;
            if (Angle < target) Angle = Math.Min(target, Angle + step);
            else if (Angle > target) Angle = Math.Max(target, Angle - step);

            IRenderAPI rpi = capi.Render;
            Vec3d cam = capi.World.Player.Entity.CameraPos;
            BlockPos pos = be.Pos;

            rpi.GlDisableCullFace();
            IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z, null);
            prog.Tex2D = AtlasId(block);
            prog.ModelMatrix = mat.Identity()
                .Translate(pos.X - cam.X, pos.Y - cam.Y, pos.Z - cam.Z)
                .Translate(0.5f, 0f, 0.5f)
                .RotateY(be.RotYDeg * GameMath.DEG2RAD)
                .Translate(-0.5f, 0f, -0.5f)
                .Translate(8f / 16f, 8.5f / 16f, 8f / 16f)
                .RotateX(-Angle * GameMath.DEG2RAD)
                .Translate(-8f / 16f, -8.5f / 16f, -8f / 16f)
                .Values;
            prog.ViewMatrix = rpi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
            rpi.RenderMesh(mesh);
            prog.Stop();
            rpi.GlEnableCullFace();
        }

        public void Dispose()
        {
            mesh?.Dispose();
            mesh = null;
        }
    }

    /// <summary>
    /// The Crucible Furnace block: a heater like the others (fuel, lighting, smoke, stovepipes) that is also a
    /// mechanical power consumer. Its gearbox, on the left as you face the spout, takes an axle.
    /// </summary>
    public class BlockCrucibleFurnace : BlockMPConsumer, IIgnitable, ISmokeEmitter
    {
        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            // Shift + right-click with fuel has to reach the furnace (the game skips it while sneaking otherwise)
            PlacedPriorityInteract = true;
        }

        public const int CrankBox = 1;
        public const int ClutchBox = 2;

        /// <summary>The gearbox side: left of the spout.</summary>
        public BlockFacing GearboxFace => (Facing ?? BlockFacing.NORTH).GetCCW();

        public override bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face, BlockMPBase forBlock)
        {
            return Facing != null && face == Facing.GetCCW();
        }

        /// <summary>The spout faces the player when placed; an axle already beside the gearbox joins up.</summary>
        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;
            BlockFacing[] hv = SuggestedHVOrientation(byPlayer, blockSel);
            BlockCrucibleFurnace placed = world.GetBlock(CodeWithVariant("side", hv[0].Opposite.Code)) as BlockCrucibleFurnace;
            if (placed == null) return false;
            placed.DoPlaceBlock(world, byPlayer, blockSel, itemstack);
            placed.tryConnect(world, byPlayer, blockSel.Position, placed.GearboxFace);
            return true;
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (blockSel == null) return false;
            if (!world.Claims.TryAccess(byPlayer, blockSel.Position, EnumBlockAccessFlags.Use)) return false;

            BECrucibleFurnace be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BECrucibleFurnace;
            if (be == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);

            if (blockSel.SelectionBoxIndex == CrankBox)
            {
                be.TurnCrank(byPlayer);
                return true;
            }
            if (blockSel.SelectionBoxIndex == ClutchBox)
            {
                if (world.Side == EnumAppSide.Server) be.ToggleClutch();
                return true;
            }

            ItemSlot hotbar = byPlayer.InventoryManager.ActiveHotbarSlot;
            ItemStack stack = hotbar?.Itemstack;

            if (stack != null && IsIgniter(stack)) return false;

            // Tongs in hand skim the slag off the metal
            if (stack != null && be.Slag && stack.Collectible.Code?.Path?.StartsWith("tongs") == true)
            {
                if (world.Side == EnumAppSide.Server) be.ClearSlag(byPlayer, hotbar);
                return true;
            }

            // Shift + right-click with fuel tops up the fuel slot
            if (stack != null && byPlayer.Entity.Controls.ShiftKey)
            {
                CombustibleProperties props = stack.Collectible.CombustibleProps;
                if (props != null && props.BurnTemperature > 0)
                {
                    // The whole stack: the fuel slot, then the fuel store on bronze, iron and steel tiers
                    if (be.AddFuel(hotbar, world) > 0)
                    {
                        (byPlayer as IClientPlayer)?.TriggerFpAnimation(EnumHandInteract.HeldItemInteract);
                        return true;
                    }
                }
            }
            // Anything else while sneaking (placing a block against the stove) goes on as normal
            if (byPlayer.Entity.Controls.ShiftKey) return false;

            return be.OnPlayerRightClick(byPlayer, blockSel);
        }

        public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (blockSel == null || blockSel.SelectionBoxIndex != CrankBox) return false;
            BECrucibleFurnace be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BECrucibleFurnace;
            if (be == null) return false;
            be.TurnCrank(byPlayer);
            return true;
        }

        public override void OnBlockInteractStop(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (blockSel == null) return;
            (world.BlockAccessor.GetBlockEntity(blockSel.Position) as BECrucibleFurnace)?.ReleaseCrank();
        }

        public override bool OnBlockInteractCancel(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, EnumItemUseCancelReason cancelReason)
        {
            if (blockSel != null) (world.BlockAccessor.GetBlockEntity(blockSel.Position) as BECrucibleFurnace)?.ReleaseCrank();
            return true;
        }

        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);
            if (world.Side == EnumAppSide.Client) world.BlockAccessor.MarkBlockDirty(pos);
        }

        public bool EmitsSmoke(BlockPos pos)
        {
            BEFuelHeater be = api.World.BlockAccessor.GetBlockEntity(pos) as BEFuelHeater;
            return be?.IsBurning == true;
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
            BEFuelHeater be = api.World.BlockAccessor.GetBlockEntity(pos) as BEFuelHeater;
            if (be != null && be.IsBurning) return secondsIgniting > 2 ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
            return EnumIgniteState.NotIgnitable;
        }
    }

    /// <summary>Crucible Furnace window: fuel, the crucible and its four ingredient slots, and status lines.</summary>
    public class GuiDialogCrucibleFurnace : GuiDialogBlockEntity
    {
        readonly BECrucibleFurnace be;
        FuelStoreView fuelStoreView;
        long tickId;

        public GuiDialogCrucibleFurnace(string title, InventoryBase inventory, BlockPos pos, ICoreClientAPI capi, BECrucibleFurnace be)
            : base(title, inventory, pos, capi)
        {
            if (IsDuplicate) return;
            this.be = be;
        }

        void Compose()
        {
            const double top = 112;
            ElementBounds fuelBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 32, 1, 1);
            fuelStoreView = new FuelStoreView(capi, be);
            ElementBounds fuelTextBounds = ElementBounds.Fixed(68 + fuelStoreView.Width, 40, 420 - fuelStoreView.Width, 44);
            ElementBounds crucibleBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, top, 1, 1);
            ElementBounds cookBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 80, top, 4, 1);
            ElementBounds meltBounds = ElementBounds.Fixed(0, top + 66, 500, 24);
            ElementBounds pourBounds = ElementBounds.Fixed(0, top + 94, 500, 80);
            ElementBounds contentBounds = ElementBounds.Fixed(0, 0, 500, top + 180);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(contentBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            SingleComposer = capi.Gui
                .CreateCompo("cruciblefurnace" + BlockEntityPosition, dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 0 }, fuelBounds, "fuelslot")
                    .AddFuelStore(fuelStoreView, fuelBounds.fixedX, fuelBounds.fixedY)
                    .AddDynamicText("", CairoFont.WhiteDetailText(), fuelTextBounds, "fueltext")
                    .AddStaticText(Lang.Get("forgeandfeast:furnace-crucible"), CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, top - 26, 240, 22))
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 1 }, crucibleBounds, "crucible")
                    .AddItemSlotGrid(Inventory, DoSendPacket, 4, new int[] { 2, 3, 4, 5 }, cookBounds, "cook")
                    .AddDynamicText("", CairoFont.WhiteDetailText(), meltBounds, "melttext")
                    .AddDynamicText("", CairoFont.WhiteDetailText().WithFontSize(13), pourBounds, "pourtext")
                .EndChildElements()
                .Compose();
            Refresh(0);
        }

        void Refresh(float dt)
        {
            if (SingleComposer == null || be == null) return;
            SingleComposer.GetDynamicText("fueltext")?.SetNewText(be.GetFuelText());
            fuelStoreView?.Refresh();
            SingleComposer.GetDynamicText("melttext")?.SetNewText(be.GetMeltText());
            SingleComposer.GetDynamicText("pourtext")?.SetNewText(be.GetPourText());
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
            SingleComposer?.GetSlotGrid("crucible")?.OnGuiClosed(capi);
            SingleComposer?.GetSlotGrid("cook")?.OnGuiClosed(capi);
            base.OnGuiClosed();
        }
    }
}
