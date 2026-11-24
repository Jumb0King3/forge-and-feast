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
    /// Smoke and ventilation for fires: the Kitchen Range, Kiln Oven, vanilla firepit and vanilla clay oven.
    ///
    /// - Chimney: a Clay Brick Chimney within 8 blocks above (nothing solid in between), or one reached
    ///   through a run of stovepipes, gives a draft: the fire works 25% faster.
    /// - Stovepipes that lead out of the room into open air vent the smoke (no draft bonus).
    /// - Smoky room: burning in a fully enclosed room (the game's own room check - doors shut, no gaps,
    ///   up to about 14 blocks across) with no chimney fills the room with smoke. The fire smoulders
    ///   and works 40% slower.
    /// - Once the smoke has built up (15 seconds), anyone in that room chokes: 0.5 damage every second
    ///   until they leave, open the room up or put the fire out. Smoke clears 3x faster than it builds.
    /// </summary>
    public class BEBehaviorSmokeVent : BlockEntityBehavior
    {
        public const float ChimneyBonus = 1.25f;
        public const float SmokyPenalty = 0.6f;
        const float SmokeBuildSeconds = 15f;
        const float MaxSmoke = 60f;
        const float DamagePerSecond = 0.5f;

        public bool HasChimney;
        public bool Smoky;
        public float SmokeSeconds;

        float lastCookTime;
        long lastWarnMs;
        RoomRegistry rooms;

        public BEBehaviorSmokeVent(BlockEntity blockentity) : base(blockentity) { }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);
            rooms = api.ModLoader.GetModSystem<RoomRegistry>();
            if (api.Side == EnumAppSide.Server)
            {
                Blockentity.RegisterGameTickListener(OnServerTick, 1000, api.World.Rand.Next(500));
                Blockentity.RegisterGameTickListener(OnFastServerTick, 100);
            }
            else
            {
                Blockentity.RegisterGameTickListener(OnClientTick, 200);
            }
        }

        public bool IsBurning()
        {
            BlockEntity be = Blockentity;
            if (be is BEFuelHeater heater) return heater.IsBurning;
            if (be is BlockEntityFirepit firepit) return firepit.IsBurning;
            if (be is BlockEntityOven oven) return oven.IsBurning;
            return false;
        }

        /// <summary>Same rule as the vanilla chimney, looking up instead of down.</summary>
        bool FindChimney()
        {
            IBlockAccessor ba = Api.World.BlockAccessor;
            BlockPos tmp = Pos.Copy();
            for (int i = 1; i <= 8; i++)
            {
                tmp.Set(Pos.X, Pos.Y + i, Pos.Z);
                Block block = ba.GetBlock(tmp);
                if (block.Id == 0) continue;
                if (block.HasBehavior<BlockBehaviorChimney>()) return true;
                if (block.SideIsSolid(tmp, BlockFacing.DOWN.Index) || block.SideIsSolid(tmp, BlockFacing.UP.Index)) return false;
            }
            return false;
        }

        Room EnclosedRoom()
        {
            Room room = rooms?.GetRoomForPosition(Pos);
            if (room == null || room.ExitCount != 0) return null;
            return room;
        }

        void OnServerTick(float dt)
        {
            bool wasChimney = HasChimney, wasSmoky = Smoky;
            float wasSmoke = SmokeSeconds;

            Room room = null;
            if (IsBurning())
            {
                room = EnclosedRoom();
                HasChimney = FindChimney();
                bool ventedOutside = false;
                if (!HasChimney && StovePipes.IsSource(Blockentity.Block))
                {
                    StovePipes.VentResult vent = StovePipes.FollowFromSource(Api.World.BlockAccessor, Pos, room);
                    HasChimney = vent == StovePipes.VentResult.Chimney;
                    ventedOutside = vent == StovePipes.VentResult.Outside;
                }
                Smoky = room != null && !HasChimney && !ventedOutside;
                if (!Smoky) room = null;
            }
            else
            {
                Smoky = false;
            }

            float smokeRate = (Blockentity as BEFuelHeater)?.CurrentSmokeMul ?? 1f;
            if (Smoky) SmokeSeconds = Math.Min(MaxSmoke, SmokeSeconds + dt * smokeRate);
            else SmokeSeconds = Math.Max(0, SmokeSeconds - 3 * dt);

            BEFuelHeater heater = Blockentity as BEFuelHeater;
            if (heater != null)
            {
                heater.VentMul = !IsBurning() ? 1f : (HasChimney ? ChimneyBonus : (Smoky ? SmokyPenalty : 1f));
            }

            if (SmokeSeconds >= SmokeBuildSeconds)
            {
                if (room == null) room = EnclosedRoom();
                if (room != null) HurtPlayersIn(room, dt);
            }

            if (wasChimney != HasChimney || wasSmoky != Smoky || (wasSmoke >= SmokeBuildSeconds) != (SmokeSeconds >= SmokeBuildSeconds) || Math.Abs(wasSmoke - SmokeSeconds) >= 5)
            {
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
            if (delta > 0 && firepit.IsBurning)
            {
                float mul = HasChimney ? ChimneyBonus : (Smoky ? SmokyPenalty : 1f);
                if (mul != 1f)
                {
                    firepit.inputStackCookingTime = Math.Max(0, now + delta * (mul - 1f));
                }
            }
            lastCookTime = firepit.inputStackCookingTime;
        }

        void HurtPlayersIn(Room room, float dt)
        {
            ICoreServerAPI sapi = Api as ICoreServerAPI;
            if (sapi == null) return;

            BlockPos ppos = Pos.Copy();
            foreach (IPlayer player in Api.World.AllOnlinePlayers)
            {
                EntityPlayer entity = player.Entity;
                if (entity == null || !entity.Alive) continue;
                if (player.WorldData?.CurrentGameMode == EnumGameMode.Creative || player.WorldData?.CurrentGameMode == EnumGameMode.Spectator) continue;
                if (entity.Pos.DistanceTo(Pos.ToVec3d().Add(0.5, 0.5, 0.5)) > 20) continue;

                ppos.Set((int)entity.Pos.X, (int)(entity.Pos.Y + entity.LocalEyePos.Y), (int)entity.Pos.Z);
                bool inside = room.Contains(ppos);
                if (!inside)
                {
                    ppos.Set((int)entity.Pos.X, (int)entity.Pos.Y, (int)entity.Pos.Z);
                    inside = room.Contains(ppos);
                }
                if (!inside) continue;

                entity.ReceiveDamage(new DamageSource()
                {
                    Source = EnumDamageSource.Block,
                    SourceBlock = Blockentity.Block,
                    SourcePos = Pos.ToVec3d(),
                    Type = EnumDamageType.Suffocation
                }, DamagePerSecond * dt);

                IServerPlayer sp = player as IServerPlayer;
                if (sp != null && sapi.World.ElapsedMilliseconds - lastWarnMs > 4000)
                {
                    sp.SendIngameError("forgeandfeast-smoke", Lang.Get("forgeandfeast:smoke-choking"));
                }
            }
            if (sapi.World.ElapsedMilliseconds - lastWarnMs > 4000) lastWarnMs = sapi.World.ElapsedMilliseconds;
        }

        /// <summary>A thin wisp from the stove's own rear stub, only while no pipe is fitted on top of it.</summary>
        void StubWisp()
        {
            BEFuelHeater heater = Blockentity as BEFuelHeater;
            if (heater == null || !heater.IsBurning) return;
            if ((StovePipes.ConnectionMask(Api.World.BlockAccessor, Pos) & (1 << BlockFacing.UP.Index)) != 0) return;
            if (Api.World.Rand.NextDouble() > 0.5) return;

            double dx = 0, dz = -0.06;   // stub sits just behind the middle on a north-facing stove
            switch (Blockentity.Block?.Variant?["side"])
            {
                case "east": dx = 0.06; dz = 0; break;
                case "south": dz = 0.06; break;
                case "west": dx = -0.06; dz = 0; break;
            }
            SimpleParticleProperties wisp = new SimpleParticleProperties(1, 1,
                ColorUtil.ColorFromRgba(110, 110, 110, 150),
                new Vec3d(Pos.X + 0.5 + dx - 0.02, Pos.Y + 0.95, Pos.Z + 0.5 + dz - 0.02), new Vec3d(Pos.X + 0.5 + dx + 0.02, Pos.Y + 0.97, Pos.Z + 0.5 + dz + 0.02),
                new Vec3f(-0.02f, 0.12f, -0.02f), new Vec3f(0.02f, 0.2f, 0.02f),
                5f, 0f, 0.15f, 0.3f, EnumParticleModel.Quad);
            wisp.SelfPropelled = true;
            wisp.WindAffected = true;
            wisp.WindAffectednes = 0.7f;
            wisp.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -150);
            wisp.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.6f);
            Api.World.SpawnParticles(wisp);
        }

        void OnClientTick(float dt)
        {
            StubWisp();
            if (!Smoky || SmokeSeconds <= 0) return;

            float level = GameMath.Clamp(SmokeSeconds / SmokeBuildSeconds, 0.15f, 1.5f);
            float smokeMul = (Blockentity as BEFuelHeater)?.SmokeMul ?? 1f;
            int alpha = (int)GameMath.Clamp(60 + 80 * level, 60, 180);
            Vec3d min = new Vec3d(Pos.X - 2.5, Pos.Y + 1.2, Pos.Z - 2.5);
            Vec3d max = new Vec3d(Pos.X + 3.5, Pos.Y + 2.6, Pos.Z + 3.5);
            SimpleParticleProperties props = new SimpleParticleProperties(
                1 * level * smokeMul, 3 * level * smokeMul,
                ColorUtil.ColorFromRgba(110, 110, 110, alpha),
                min, max,
                new Vec3f(-0.08f, -0.02f, -0.08f), new Vec3f(0.08f, 0.05f, 0.08f),
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
            tree.SetFloat("ffSmoke", SmokeSeconds);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            HasChimney = tree.GetBool("ffChimney");
            Smoky = tree.GetBool("ffSmoky");
            SmokeSeconds = tree.GetFloat("ffSmoke");
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            if (!IsBurning() && SmokeSeconds <= 0) return;
            if (HasChimney) dsc.AppendLine(Lang.Get("forgeandfeast:vent-chimney"));
            else if (Smoky) dsc.AppendLine(Lang.Get(SmokeSeconds >= SmokeBuildSeconds ? "forgeandfeast:vent-choking" : "forgeandfeast:vent-smoky"));
        }
    }
}
