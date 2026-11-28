using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>
    /// Smoke and ventilation for fires: the Kitchen Range, Kiln Oven, Retort, Smoker, Potbelly Stove,
    /// vanilla firepit and vanilla clay oven. The room and chimney sums are done by <see cref="SmokeSystem"/>;
    /// this works out where one fire's smoke goes, keeps what the client needs and draws the smoke.
    ///
    /// - Chimney: a chimney within 8 blocks above (nothing solid in between), or one reached through
    ///   stovepipes, gives a draft: up to 25% faster, more in wind, less in rain or still air, and less
    ///   when too many fires share one chimney for its height.
    /// - Stovepipes that lead out of the room into open air vent the smoke (no draft bonus).
    /// - Smoke that can't get out collects under the ceiling of a closed room and builds down. Choking
    ///   starts when it reaches your head; the fire smoulders (40% slower) when it reaches the fire.
    /// </summary>
    public class BEBehaviorSmokeVent : BlockEntityBehavior
    {
        public enum Vent { None, Outside, Chimney }

        // Worked out on the server each second
        public bool Burning;
        public float Production;
        public Vent VentKind;
        public BlockPos ChimneyPos;
        public long ChimneyKey;
        public Room FireRoom;
        public string RoomKey;
        public float DraftRatio = 1f, WeatherMul = 1f, Backed, Spare;
        public SmokeSystem.Weather WeatherKind;
        public int ChimneyShare = 1;

        /// <summary>Smoke this fire puts into the room (in fires' worth, not blocks).</summary>
        public float Unvented => !Burning ? 0 : (VentKind == Vent.None ? Production : (VentKind == Vent.Chimney ? Backed : 0));

        // Synced to the client
        public bool HasChimney;
        public bool Smoky;
        public bool Smouldering;
        public bool HeadHigh;
        public bool Leaky;
        public bool Lead;
        public float RoomVolume;
        public float RoomFrac;
        public float Mul = 1f;
        public float SmokeBottom, SmokeTop;
        int minX, minZ, maxX, maxZ;

        float lastCookTime;
        float lastFuelSmoke = 1f;
        float sentBottom, sentVolume;
        int sentState = -1;
        SmokeSystem system;
        bool realSmoke;

        public BEBehaviorSmokeVent(BlockEntity blockentity) : base(blockentity) { }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);
            realSmoke = StovePipes.RealSmoke(api);
            if (api.Side == EnumAppSide.Server)
            {
                system = api.ModLoader.GetModSystem<SmokeSystem>();
                system?.Add(this);
                Blockentity.RegisterGameTickListener(OnFastServerTick, 100);
            }
            else
            {
                Blockentity.RegisterGameTickListener(OnClientTick, 200);
            }
        }

        public override void OnBlockRemoved() { base.OnBlockRemoved(); system?.Remove(this); }
        public override void OnBlockUnloaded() { base.OnBlockUnloaded(); system?.Remove(this); }

        public bool IsBurning()
        {
            BlockEntity be = Blockentity;
            if (be is BEFuelHeater heater) return heater.IsBurning;
            if (be is BlockEntityFirepit firepit) return firepit.IsBurning;
            if (be is BlockEntityOven oven) return oven.IsBurning;
            return false;
        }

        float FuelSmoke()
        {
            if (Blockentity is BEFuelHeater heater) return heater.FuelSmoke;
            if (Blockentity is BlockEntityFirepit firepit)
            {
                CollectibleObject fuel = firepit.fuelSlot?.Itemstack?.Collectible;
                if (fuel != null) lastFuelSmoke = SmokeSystem.FuelSmoke(fuel);
                return lastFuelSmoke;
            }
            return 1f;
        }

        /// <summary>Same rule as the vanilla chimney, looking up instead of down. Returns the chimney or null.</summary>
        BlockPos FindChimney(IBlockAccessor ba)
        {
            BlockPos tmp = Pos.Copy();
            for (int i = 1; i <= 8; i++)
            {
                tmp.Set(Pos.X, Pos.Y + i, Pos.Z);
                Block block = ba.GetBlock(tmp);
                if (block.Id == 0) continue;
                // The upper part of a tall station (the Smoker) is its own block: look through it
                if (block is BlockMultiblock mb && tmp.AddCopy(mb.OffsetInv.X, mb.OffsetInv.Y, mb.OffsetInv.Z).Equals(Pos)) continue;
                if (StovePipes.IsChimney(block)) return tmp.Copy();
                if (block.SideIsSolid(tmp, BlockFacing.DOWN.Index) || block.SideIsSolid(tmp, BlockFacing.UP.Index)) return null;
            }
            return null;
        }

        public void ResetVent()
        {
            Burning = false; Production = 0; VentKind = Vent.None; ChimneyPos = null; FireRoom = null; RoomKey = null;
        }

        /// <summary>Server, once a second: is this fire burning, how much smoke, where does it go.</summary>
        public void ComputeVent(IBlockAccessor ba, RoomRegistry rooms)
        {
            Burning = IsBurning();
            float stationMul = (Blockentity as BEFuelHeater)?.CurrentSmokeMul ?? 1f;
            Production = Burning ? stationMul * FuelSmoke() : 0;
            DraftRatio = 1f; WeatherMul = 1f; Backed = 0; Spare = 0; ChimneyShare = 1;
            WeatherKind = SmokeSystem.Weather.Normal;
            VentKind = Vent.None;
            ChimneyPos = null;
            FireRoom = null;

            // A cold fire only matters while its room still has smoke to clear
            if (!Burning && RoomVolume <= 0) return;

            Room room = rooms?.GetRoomForPosition(Pos);
            if (room != null && (room.ExitCount != 0 || room.AnyChunkUnloaded != 0)) room = null;
            FireRoom = room;

            ChimneyPos = FindChimney(ba);
            if (ChimneyPos != null) VentKind = Vent.Chimney;
            else if (StovePipes.IsSource(Blockentity.Block))
            {
                BlockPos chimney;
                StovePipes.VentResult vent = StovePipes.FollowFromSource(ba, Pos, room, out chimney);
                if (vent == StovePipes.VentResult.Chimney && chimney != null) { VentKind = Vent.Chimney; ChimneyPos = chimney; }
                else if (vent == StovePipes.VentResult.Outside) VentKind = Vent.Outside;
            }
        }

        public void ApplyRoom(SmokeSystem.RoomSmoke rs, bool lead)
        {
            RoomVolume = rs.Volume;
            RoomFrac = rs.Total > 0 ? rs.Volume / rs.Total : 0;
            SmokeBottom = rs.Bottom;
            SmokeTop = rs.MinY + rs.Layer.Length;
            Leaky = rs.LeakTotal > 0;
            Lead = lead;
            Smoky = rs.Volume >= 0.5f;
            // Fire sits on the floor: smoke at its top smothers it; about 1.6 blocks up is head height
            Smouldering = Smoky && rs.Bottom <= Pos.Y + 1;
            HeadHigh = Smoky && rs.Bottom <= Pos.Y + 1.6f;
            minX = Math.Max(rs.Loc.X1, Pos.X - 7); maxX = Math.Min(rs.Loc.X2, Pos.X + 7);
            minZ = Math.Max(rs.Loc.Z1, Pos.Z - 7); maxZ = Math.Min(rs.Loc.Z2, Pos.Z + 7);
        }

        /// <summary>Not in a closed room (or Real Smoke is doing rooms): whatever smoke there was clears fast.</summary>
        public void ApplyNoRoom(float dt)
        {
            RoomVolume = Math.Max(0, RoomVolume * 0.7f - 2 * dt);
            if (RoomVolume < 0.5f) RoomVolume = 0;
            Smoky = Smouldering = HeadHigh = Leaky = Lead = false;
            RoomFrac = 0;
        }

        public void FinishTick()
        {
            HasChimney = Burning && VentKind == Vent.Chimney;
            float draft = HasChimney ? 1f + SmokeSystem.DraftBonus * WeatherMul * DraftRatio : 1f;
            Mul = !Burning ? 1f : draft * (Smouldering ? SmokeSystem.SmokyPenalty : 1f);

            if (Blockentity is BEFuelHeater heater) heater.VentMul = Mul;

            int state = (HasChimney ? 1 : 0) | (Smoky ? 2 : 0) | (Smouldering ? 4 : 0) | (HeadHigh ? 8 : 0) | (Leaky ? 16 : 0)
                | (Lead ? 32 : 0) | ((int)WeatherKind << 6) | (Math.Min(ChimneyShare, 15) << 8) | ((int)(Mul * 20) << 12);
            if (state != sentState || Math.Abs(sentBottom - SmokeBottom) >= 0.25f || Math.Abs(sentVolume - RoomVolume) >= 5 || (RoomVolume == 0) != (sentVolume == 0))
            {
                sentState = state;
                sentBottom = SmokeBottom;
                sentVolume = RoomVolume;
                Blockentity.MarkDirty();
            }
        }

        /// <summary>Vanilla firepit: speed up or slow down its cooking timer by the same amounts as our stations.</summary>
        void OnFastServerTick(float dt)
        {
            BlockEntityFirepit firepit = Blockentity as BlockEntityFirepit;
            if (firepit == null) return;

            float now = firepit.inputStackCookingTime;
            float delta = now - lastCookTime;
            if (delta > 0 && firepit.IsBurning && Mul != 1f)
            {
                firepit.inputStackCookingTime = Math.Max(0, now + delta * (Mul - 1f));
            }
            lastCookTime = firepit.inputStackCookingTime;
        }

        /// <summary>A thin wisp from the stove's own rear stub, only while no pipe is fitted on top of it.</summary>
        void StubWisp()
        {
            BEFuelHeater heater = Blockentity as BEFuelHeater;
            if (heater == null || !heater.IsBurning) return;
            if (StovePipes.PipeOnTop(Api.World.BlockAccessor, Pos)) return;
            if (Api.World.Rand.NextDouble() > 0.5 * GameMath.Clamp(heater.FuelSmoke, 0.3f, 1.5f)) return;

            double dx = 0, dz = -0.06;   // stub sits just behind the middle on a north-facing stove
            if (heater is BESmoker) dz = 0;   // the Smoker's vent is in the middle of its ridge
            else if (heater is BECrucibleFurnace)
            {
                int[] o = StovePipes.FurnaceFlue(Blockentity.Block);
                dx = o[0] / 16.0; dz = o[1] / 16.0;
            }
            else switch (Blockentity.Block?.Variant?["side"])
            {
                case "east": dx = 0.06; dz = 0; break;
                case "south": dz = 0.06; break;
                case "west": dx = -0.06; dz = 0; break;
            }
            SimpleParticleProperties wisp = new SimpleParticleProperties(1, 1,
                ColorUtil.ColorFromRgba(110, 110, 110, 150),
                new Vec3d(Pos.X + 0.5 + dx - 0.02, Pos.Y + heater.StubTop, Pos.Z + 0.5 + dz - 0.02), new Vec3d(Pos.X + 0.5 + dx + 0.02, Pos.Y + heater.StubTop + 0.02, Pos.Z + 0.5 + dz + 0.02),
                new Vec3f(-0.02f, 0.12f, -0.02f), new Vec3f(0.02f, 0.2f, 0.02f),
                5f, 0f, 0.15f, 0.3f, EnumParticleModel.Quad);
            wisp.SelfPropelled = true;
            wisp.WindAffected = true;
            wisp.WindAffectednes = 0.7f;
            wisp.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -150);
            wisp.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.6f);
            Api.World.SpawnParticles(wisp);
        }

        /// <summary>Room smoke: a layer of haze from the ceiling down to where the smoke has reached.</summary>
        void OnClientTick(float dt)
        {
            if (realSmoke) return;
            StubWisp();
            if (!Smoky || !Lead || SmokeTop <= SmokeBottom) return;

            float thick = SmokeTop - SmokeBottom;
            int area = (maxX - minX + 1) * (maxZ - minZ + 1);
            float amount = GameMath.Clamp(thick * area / 30f, 0.5f, 6f);
            int alpha = (int)GameMath.Clamp(70 + 120 * RoomFrac, 70, 190);
            SimpleParticleProperties props = new SimpleParticleProperties(
                amount, amount * 2,
                ColorUtil.ColorFromRgba(110, 110, 110, alpha),
                new Vec3d(minX, SmokeBottom, minZ), new Vec3d(maxX + 1, SmokeTop - 0.2, maxZ + 1),
                new Vec3f(-0.06f, -0.015f, -0.06f), new Vec3f(0.06f, 0.015f, 0.06f),
                6f, 0f, 1.5f, 3f, EnumParticleModel.Quad);
            props.SelfPropelled = true;
            props.WindAffected = false;
            Api.World.SpawnParticles(props);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("ffChimney", HasChimney);
            tree.SetBool("ffSmoky", Smoky);
            tree.SetBool("ffSmoulder", Smouldering);
            tree.SetBool("ffHead", HeadHigh);
            tree.SetBool("ffLeaky", Leaky);
            tree.SetBool("ffLead", Lead);
            tree.SetFloat("ffRoomSmoke", RoomVolume);
            tree.SetFloat("ffFrac", RoomFrac);
            tree.SetFloat("ffMul", Mul);
            tree.SetFloat("ffBottom", SmokeBottom);
            tree.SetFloat("ffTop", SmokeTop);
            tree.SetInt("ffWeather", (int)WeatherKind);
            tree.SetInt("ffShare", ChimneyShare);
            tree.SetFloat("ffDraft", DraftRatio);
            tree.SetFloat("ffWeatherMul", WeatherMul);
            tree.SetInt("ffMinX", minX); tree.SetInt("ffMaxX", maxX);
            tree.SetInt("ffMinZ", minZ); tree.SetInt("ffMaxZ", maxZ);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            HasChimney = tree.GetBool("ffChimney");
            Smoky = tree.GetBool("ffSmoky");
            Smouldering = tree.GetBool("ffSmoulder");
            HeadHigh = tree.GetBool("ffHead");
            Leaky = tree.GetBool("ffLeaky");
            Lead = tree.GetBool("ffLead");
            RoomVolume = tree.GetFloat("ffRoomSmoke");
            RoomFrac = tree.GetFloat("ffFrac");
            Mul = tree.GetFloat("ffMul", 1f);
            SmokeBottom = tree.GetFloat("ffBottom");
            SmokeTop = tree.GetFloat("ffTop");
            WeatherKind = (SmokeSystem.Weather)tree.GetInt("ffWeather");
            ChimneyShare = tree.GetInt("ffShare", 1);
            DraftRatio = tree.GetFloat("ffDraft", 1f);
            WeatherMul = tree.GetFloat("ffWeatherMul", 1f);
            minX = tree.GetInt("ffMinX"); maxX = tree.GetInt("ffMaxX");
            minZ = tree.GetInt("ffMinZ"); maxZ = tree.GetInt("ffMaxZ");
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            if (Blockentity is BEFuelHeater heater && heater.IsBurning)
            {
                if (heater.FuelSmoke <= 0.5f) dsc.AppendLine(Lang.Get("forgeandfeast:fuelsmoke-low"));
                else if (heater.FuelSmoke >= 1.3f) dsc.AppendLine(Lang.Get("forgeandfeast:fuelsmoke-high"));
            }
            if (!IsBurning() && !Smoky) return;

            if (HasChimney)
            {
                int pct = (int)Math.Round(100 * SmokeSystem.DraftBonus * WeatherMul * DraftRatio);
                dsc.AppendLine(Lang.Get("forgeandfeast:vent-chimney", Math.Max(0, pct)));
                if (DraftRatio < 0.99f) dsc.AppendLine(Lang.Get("forgeandfeast:vent-overloaded", ChimneyShare));
                switch (WeatherKind)
                {
                    case SmokeSystem.Weather.Windy: dsc.AppendLine(Lang.Get("forgeandfeast:vent-windy")); break;
                    case SmokeSystem.Weather.Rain: dsc.AppendLine(Lang.Get("forgeandfeast:vent-rain")); break;
                    case SmokeSystem.Weather.Still: dsc.AppendLine(Lang.Get("forgeandfeast:vent-still")); break;
                }
            }
            if (Smoky)
            {
                if (Smouldering) dsc.AppendLine(Lang.Get("forgeandfeast:vent-smoky"));
                if (HeadHigh) dsc.AppendLine(Lang.Get("forgeandfeast:vent-choking"));
                else dsc.AppendLine(Lang.Get("forgeandfeast:vent-gathering", (int)Math.Round(RoomFrac * 100)));
                if (Leaky) dsc.AppendLine(Lang.Get("forgeandfeast:vent-leaky"));
            }
        }
    }
}
