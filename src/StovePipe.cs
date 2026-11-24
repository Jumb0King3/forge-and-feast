using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>
    /// Shared rules for stovepipe runs. Pipes join to other pipes, to vanilla Clay Brick Chimneys and to
    /// fires that can take a pipe (Kitchen Range, Kiln Oven, vanilla clay oven).
    /// </summary>
    public static class StovePipes
    {
        public const int MaxPipes = 64;

        public enum VentResult { None, Outside, Chimney }

        public static bool IsPipe(Block b) => b is BlockStovePipe;

        public static bool IsChimney(Block b) => b != null && b.Id != 0 && b.HasBehavior<BlockBehaviorChimney>();

        /// <summary>Fires a stovepipe can connect to. The firepit is left out on purpose.</summary>
        public static bool IsSource(Block b)
        {
            if (b == null || b.Id == 0) return false;
            if (b is BlockFuelHeater) return true;
            return b.Code != null && b.Code.Domain == "game" && b.Code.Path.StartsWith("clayoven");
        }

        public static bool Connects(Block b) => IsPipe(b) || IsChimney(b) || IsSource(b);

        /// <summary>Bit per face (BlockFacing.Index: N, E, S, W, U, D) that has something to join.</summary>
        public static int ConnectionMask(IBlockAccessor ba, BlockPos pos)
        {
            int mask = 0;
            BlockPos tmp = pos.Copy();
            for (int i = 0; i < 6; i++)
            {
                BlockFacing f = BlockFacing.ALLFACES[i];
                tmp.Set(pos.X + f.Normali.X, pos.Y + f.Normali.Y, pos.Z + f.Normali.Z);
                if (Connects(ba.GetBlock(tmp))) mask |= 1 << f.Index;
            }
            return mask;
        }

        static long Key(BlockPos p) => ((long)(p.X & 0xFFFFFF) << 40) | ((long)(p.Y & 0xFFFF) << 24) | (long)(p.Z & 0xFFFFFF);

        /// <summary>
        /// Follows the pipes joined to a fire. Reaching a chimney gives a draft; a pipe end poking out of
        /// the fire's closed room into open air vents the smoke outside.
        /// </summary>
        public static VentResult FollowFromSource(IBlockAccessor ba, BlockPos source, Room enclosedRoom)
        {
            BlockPos[] queue = new BlockPos[MaxPipes];
            Dictionary<long, bool> seen = new Dictionary<long, bool>();
            int head = 0, tail = 0;
            seen[Key(source)] = true;

            VentResult result = VentResult.None;
            BlockPos n = source.Copy();
            for (int i = 0; i < 6; i++)
            {
                BlockFacing f = BlockFacing.ALLFACES[i];
                n.Set(source.X + f.Normali.X, source.Y + f.Normali.Y, source.Z + f.Normali.Z);
                if (IsPipe(ba.GetBlock(n)) && !seen.ContainsKey(Key(n)) && tail < MaxPipes)
                {
                    seen[Key(n)] = true;
                    queue[tail++] = n.Copy();
                }
            }

            while (head < tail)
            {
                BlockPos p = queue[head++];
                bool pipeOutside = enclosedRoom != null && !enclosedRoom.Contains(p);
                for (int i = 0; i < 6; i++)
                {
                    BlockFacing f = BlockFacing.ALLFACES[i];
                    n.Set(p.X + f.Normali.X, p.Y + f.Normali.Y, p.Z + f.Normali.Z);
                    Block nb = ba.GetBlock(n);
                    if (IsChimney(nb)) return VentResult.Chimney;
                    if (IsPipe(nb))
                    {
                        long k = Key(n);
                        if (!seen.ContainsKey(k) && tail < MaxPipes)
                        {
                            seen[k] = true;
                            queue[tail++] = n.Copy();
                        }
                        continue;
                    }
                    if (pipeOutside && nb.Id == 0 && !enclosedRoom.Contains(n)) result = VentResult.Outside;
                }
            }
            return result;
        }

        /// <summary>True if this pipe's run is joined to a fire that's burning right now.</summary>
        public static bool RunIsBurning(IBlockAccessor ba, BlockPos start)
        {
            BlockPos[] queue = new BlockPos[MaxPipes];
            Dictionary<long, bool> seen = new Dictionary<long, bool>();
            int head = 0, tail = 0;
            queue[tail++] = start.Copy();
            seen[Key(start)] = true;
            BlockPos n = start.Copy();

            while (head < tail)
            {
                BlockPos p = queue[head++];
                for (int i = 0; i < 6; i++)
                {
                    BlockFacing f = BlockFacing.ALLFACES[i];
                    n.Set(p.X + f.Normali.X, p.Y + f.Normali.Y, p.Z + f.Normali.Z);
                    Block nb = ba.GetBlock(n);
                    if (IsSource(nb))
                    {
                        if (SourceIsBurning(ba.GetBlockEntity(n))) return true;
                        continue;
                    }
                    if (IsPipe(nb))
                    {
                        long k = Key(n);
                        if (!seen.ContainsKey(k) && tail < MaxPipes)
                        {
                            seen[k] = true;
                            queue[tail++] = n.Copy();
                        }
                    }
                }
            }
            return false;
        }

        public static bool SourceIsBurning(BlockEntity be)
        {
            if (be is BEFuelHeater heater) return heater.IsBurning;
            if (be is BlockEntityOven oven) return oven.IsBurning;
            return false;
        }
    }

    /// <summary>
    /// A stovepipe. Joins up automatically with neighbouring pipes, chimneys and fires that take a pipe.
    /// Puffs smoke out of the top of a chimney it feeds into, or out of an open end, while its fire burns.
    /// </summary>
    public class BlockStovePipe : Block, ISmokeEmitter
    {
        const float R1 = 5.5f / 16f, R2 = 10.5f / 16f;
        SimpleParticleProperties smoke;

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);
            if (api.Side == EnumAppSide.Client)
            {
                smoke = new SimpleParticleProperties(0.3f, 0.6f, ColorUtil.ColorFromRgba(120, 120, 120, 160),
                    new Vec3d(), new Vec3d(), new Vec3f(-0.03f, 0.25f, -0.03f), new Vec3f(0.03f, 0.45f, 0.03f),
                    10f, 0f, 0.6f, 1.2f, EnumParticleModel.Quad);
                smoke.SelfPropelled = true;
                smoke.WindAffected = true;
            }
        }

        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return Boxes(StovePipes.ConnectionMask(blockAccessor, pos));
        }

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return Boxes(StovePipes.ConnectionMask(blockAccessor, pos));
        }

        public static int VisualMask(int mask)
        {
            // A lone pipe or a pipe joined on one side only runs straight through
            if (mask == 0) return (1 << BlockFacing.UP.Index) | (1 << BlockFacing.DOWN.Index);
            for (int i = 0; i < 6; i++)
            {
                if (mask == (1 << i)) return mask | (1 << BlockFacing.ALLFACES[i].Opposite.Index);
            }
            return mask;
        }

        static Cuboidf[] Boxes(int mask)
        {
            mask = VisualMask(mask);
            Cuboidf[] boxes = new Cuboidf[1 + 6];
            int n = 0;
            boxes[n++] = new Cuboidf(R1, R1, R1, R2, R2, R2);
            if ((mask & (1 << BlockFacing.NORTH.Index)) != 0) boxes[n++] = new Cuboidf(R1, R1, 0, R2, R2, R1);
            if ((mask & (1 << BlockFacing.SOUTH.Index)) != 0) boxes[n++] = new Cuboidf(R1, R1, R2, R2, R2, 1);
            if ((mask & (1 << BlockFacing.WEST.Index)) != 0) boxes[n++] = new Cuboidf(0, R1, R1, R1, R2, R2);
            if ((mask & (1 << BlockFacing.EAST.Index)) != 0) boxes[n++] = new Cuboidf(R2, R1, R1, 1, R2, R2);
            if ((mask & (1 << BlockFacing.DOWN.Index)) != 0) boxes[n++] = new Cuboidf(R1, 0, R1, R2, R1, R2);
            if ((mask & (1 << BlockFacing.UP.Index)) != 0) boxes[n++] = new Cuboidf(R1, R2, R1, R2, 1, R2);
            Cuboidf[] result = new Cuboidf[n];
            Array.Copy(boxes, result, n);
            return result;
        }

        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);
            if (world.Side == EnumAppSide.Client) world.BlockAccessor.MarkBlockDirty(pos);
            else world.BlockAccessor.GetBlockEntity(pos)?.MarkDirty(true);
        }

        /// <summary>A vanilla chimney straight above this pipe puffs while the pipe's fire burns.</summary>
        public bool EmitsSmoke(BlockPos pos)
        {
            return StovePipes.RunIsBurning(api.World.BlockAccessor, pos);
        }

        public override bool ShouldReceiveClientParticleTicks(IWorldAccessor world, IPlayer player, BlockPos pos, out bool isWindAffected)
        {
            isWindAffected = true;
            return true;
        }

        /// <summary>
        /// Smoke for chimneys the pipe joins from the side (the vanilla chimney only looks straight down),
        /// and for open pipe ends.
        /// </summary>
        public override void OnAsyncClientParticleTick(IAsyncParticleManager manager, BlockPos pos, float windAffectednessAtPos, float secondsTicking)
        {
            if (smoke == null) return;
            IBlockAccessor ba = manager.BlockAccess;
            int mask = StovePipes.ConnectionMask(ba, pos);
            int pipeCount = 0;
            bool feedsChimney = false;
            BlockPos chimney = null;
            BlockPos n = pos.Copy();

            for (int i = 0; i < 6; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                BlockFacing f = BlockFacing.ALLFACES[i];
                n.Set(pos.X + f.Normali.X, pos.Y + f.Normali.Y, pos.Z + f.Normali.Z);
                Block nb = ba.GetBlock(n);
                if (StovePipes.IsPipe(nb) || StovePipes.IsSource(nb)) pipeCount++;
                // Chimney joined from the side or from above: vanilla won't smoke it, so we do
                if (StovePipes.IsChimney(nb) && f != BlockFacing.UP) { feedsChimney = true; chimney = n.Copy(); }
            }

            bool openEnd = !feedsChimney && pipeCount <= 1 && (mask & (1 << BlockFacing.UP.Index)) == 0
                && ba.GetBlock(pos.UpCopy()).Id == 0;
            if (!feedsChimney && !openEnd) return;
            if (!StovePipes.RunIsBurning(ba, pos)) return;

            if (feedsChimney)
            {
                BlockPos top = chimney.Copy();
                while (StovePipes.IsChimney(ba.GetBlock(top.UpCopy())) && top.Y - chimney.Y < 16) top.Up();
                smoke.MinPos.Set(top.X + 0.3, top.Y + 1.0, top.Z + 0.3);
            }
            else
            {
                smoke.MinPos.Set(pos.X + 0.4, pos.Y + 1.0, pos.Z + 0.4);
            }
            smoke.AddPos.Set(0.4, 0.1, 0.4);
            smoke.WindAffectednes = windAffectednessAtPos * 0.7f;
            manager.Spawn(smoke);
        }
    }

    /// <summary>Draws the pipe with arms toward whatever it's joined to.</summary>
    public class BEStovePipe : BlockEntity
    {
        static readonly string[] ArmNames = { "arm-north", "arm-east", "arm-south", "arm-west", "arm-up", "arm-down" };

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            int mask = BlockStovePipe.VisualMask(StovePipes.ConnectionMask(Api.World.BlockAccessor, Pos));
            int cmask = 0;
            BlockPos np = Pos.Copy();
            for (int i = 0; i < 6; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                np.Set(Pos).Add(BlockFacing.ALLFACES[i]);
                if (StovePipes.IsChimney(Api.World.BlockAccessor.GetBlock(np))) cmask |= 1 << i;
            }
            MeshData mesh = GetMesh(mask | (cmask << 6), tesselator);
            if (mesh == null) return false;
            mesher.AddMeshData(mesh);
            return true;
        }

        MeshData GetMesh(int mask, ITesselatorAPI tesselator)
        {
            Dictionary<int, MeshData> cache = ObjectCacheUtil.GetOrCreate(Api, "forgeandfeast-pipemeshes", () => new Dictionary<int, MeshData>());
            lock (cache)
            {
                MeshData mesh;
                if (cache.TryGetValue(mask, out mesh)) return mesh;

                Shape shape = Shape.TryGet(Api, new AssetLocation("forgeandfeast", "shapes/block/stovepipe.json"));
                if (shape == null) return null;

                int cm = mask >> 6;
                string[] elems = new string[1 + 6 * 2];
                int k = 0;
                elems[k++] = "core";
                for (int i = 0; i < 6; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    if ((cm & (1 << i)) != 0)
                    {
                        elems[k++] = "c" + ArmNames[i];
                        elems[k++] = "cflange-" + ArmNames[i].Substring(4);
                    }
                    else elems[k++] = ArmNames[i];
                }
                System.Array.Resize(ref elems, k);

                tesselator.TesselateShape(Block, shape, out mesh, null, null, elems);
                cache[mask] = mesh;
                return mesh;
            }
        }
    }
}
