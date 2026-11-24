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

        /// <summary>
        /// A pipe riser standing on a Kitchen Range or Kiln Oven lines up with the stove's rear stub, so it is drawn
        /// pushed toward the back. Returns that direction, or null when the pipe doesn't stand on one.
        /// </summary>
        public static BlockFacing BackShift(IBlockAccessor ba, BlockPos pos)
        {
            BlockPos p = pos.Copy();
            for (int i = 0; i < MaxPipes; i++)
            {
                p.Y -= 1;
                Block b = ba.GetBlock(p);
                if (b is BlockFuelHeater)
                {
                    string side = b.Variant?["side"];
                    BlockFacing f = side == null ? null : BlockFacing.FromCode(side);
                    return f?.Opposite;
                }
                if (!IsPipe(b)) return null;
            }
            return null;
        }

        /// <summary>
        /// A pipe that ends against a roof of one or two solid blocks with a chimney on the other side counts as
        /// passing through it, so the roof never has to be opened. Returns the chimney position or null.
        /// </summary>
        public static BlockPos RoofChimney(IBlockAccessor ba, BlockPos pipe)
        {
            BlockPos u = pipe.UpCopy();
            Block first = ba.GetBlock(u);
            if (first == null || first.Id == 0 || IsPipe(first) || IsChimney(first) || !first.SideSolid[BlockFacing.DOWN.Index]) return null;
            for (int k = 0; k < 2; k++)
            {
                u.Up();
                Block b = ba.GetBlock(u);
                if (IsChimney(b)) return u.Copy();
                if (b == null || b.Id == 0 || !b.SideSolid[BlockFacing.DOWN.Index]) return null;
            }
            return null;
        }

        /// <summary>A pipe running sideways under a solid roof sits tight against it instead of floating mid-block.</summary>
        public static bool CeilingLift(IBlockAccessor ba, BlockPos pos, int realMask)
        {
            int horizontal = realMask & 15;
            if (horizontal == 0 || (realMask & ((1 << BlockFacing.UP.Index) | (1 << BlockFacing.DOWN.Index))) != 0) return false;
            Block above = ba.GetBlock(pos.UpCopy());
            return above != null && above.Id != 0 && !IsPipe(above) && !IsChimney(above) && above.SideSolid[BlockFacing.DOWN.Index];
        }

        public static bool IsChimney(Block b) => b != null && b.Id != 0 && b.HasBehavior<BlockBehaviorChimney>();

        static bool IsWall(IBlockAccessor ba, BlockPos pos, BlockFacing f)
        {
            Block b = ba.GetBlock(pos.AddCopy(f));
            return b != null && b.Id != 0 && !(b is BlockStovePipe) && b.SideSolid[f.Opposite.Index];
        }

        /// <summary>
        /// Where this pipe sits inside its block, as {x, z} offsets in sixteenths (-6, 0 or 6). A riser on a stove
        /// lines up with the stove's rear stub. Otherwise the pipe hugs a wall beside it, or stays central.
        /// </summary>
        public static int[] Offset(IBlockAccessor ba, BlockPos pos)
        {
            BlockFacing back = BackShift(ba, pos);
            if (back != null) return new[] { back.Normali.X * 7, back.Normali.Z * 7 };
            int ox = 0, oz = 0;
            bool w = IsWall(ba, pos, BlockFacing.WEST), e = IsWall(ba, pos, BlockFacing.EAST);
            bool n = IsWall(ba, pos, BlockFacing.NORTH), s = IsWall(ba, pos, BlockFacing.SOUTH);
            if (w && !e) ox = -7; else if (e && !w) ox = 7;
            if (n && !s) oz = -7; else if (s && !n) oz = 7;
            return new[] { ox, oz };
        }

        /// <summary>The offset the joint with a neighbour is drawn at: the same answer from both sides.</summary>
        public static int Meet(int a, int b)
        {
            if (a == b) return a;
            if (a == 0) return b;
            if (b == 0) return a;
            return Math.Min(a, b);
        }

        /// <summary>Offset of whatever is joined on face f, for working out the joint. Chimneys stay central.</summary>
        public static int[] NeighbourOffset(IBlockAccessor ba, BlockPos pos, BlockFacing f, int[] mine, bool connected)
        {
            if (!connected) return mine;
            BlockPos n = pos.AddCopy(f);
            Block b = ba.GetBlock(n);
            if (IsPipe(b)) return Offset(ba, n);
            if (IsChimney(b)) return new[] { 0, 0 };
            if (b is BlockFuelHeater) return mine;
            return new[] { 0, 0 };
        }

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
                if (RoofChimney(ba, p) != null) return VentResult.Chimney;
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
        const float R1 = 6f / 16f, R2 = 10f / 16f;
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
            return LiftedBoxes(blockAccessor, pos);
        }

        static Cuboidf[] LiftedBoxes(IBlockAccessor ba, BlockPos pos)
        {
            int real = StovePipes.ConnectionMask(ba, pos);
            Cuboidf[] boxes = Boxes(real, StovePipes.Offset(ba, pos));
            if (StovePipes.CeilingLift(ba, pos, real))
            {
                for (int i = 0; i < boxes.Length; i++) boxes[i] = boxes[i].Translate(0, 6f / 16f, 0);
            }
            return boxes;
        }

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return LiftedBoxes(blockAccessor, pos);
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

        static Cuboidf[] Boxes(int mask, int[] o)
        {
            mask = VisualMask(mask);
            List<Cuboidf> boxes = new List<Cuboidf>();
            float ox = o[0] / 16f, oz = o[1] / 16f;
            boxes.Add(new Cuboidf(R1, R1, R1, R2, R2, R2).Translate(ox, 0, oz));
            if ((mask & (1 << BlockFacing.DOWN.Index)) != 0) boxes.Add(new Cuboidf(R1, 0, R1, R2, R1, R2).Translate(ox, 0, oz));
            if ((mask & (1 << BlockFacing.UP.Index)) != 0) boxes.Add(new Cuboidf(R1, R2, R1, R2, 1, R2).Translate(ox, 0, oz));
            if ((mask & (1 << BlockFacing.NORTH.Index)) != 0) boxes.Add(new Cuboidf(R1 + ox, R1, 0, R2 + ox, R2, R2 + oz));
            if ((mask & (1 << BlockFacing.SOUTH.Index)) != 0) boxes.Add(new Cuboidf(R1 + ox, R1, R1 + oz, R2 + ox, R2, 1));
            if ((mask & (1 << BlockFacing.WEST.Index)) != 0) boxes.Add(new Cuboidf(0, R1, R1 + oz, R2 + ox, R2, R2 + oz));
            if ((mask & (1 << BlockFacing.EAST.Index)) != 0) boxes.Add(new Cuboidf(R1 + ox, R1, R1 + oz, 1, R2, R2 + oz));
            return boxes.ToArray();
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

            if (!feedsChimney)
            {
                BlockPos roof = StovePipes.RoofChimney(ba, pos);
                if (roof != null) { feedsChimney = true; chimney = roof; }
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
        const string Dirs = "NESW";
        static string Code(int v) { return v < 0 ? "m" : (v > 0 ? "p" : "0"); }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            IBlockAccessor ba = Api.World.BlockAccessor;
            int real = StovePipes.ConnectionMask(ba, Pos);
            int mask = BlockStovePipe.VisualMask(real);
            int[] o = StovePipes.Offset(ba, Pos);

            int cmask = 0;
            int[][] f = new int[6][];
            System.Text.StringBuilder key = new System.Text.StringBuilder();
            key.Append(Code(o[0])).Append(Code(o[1])).Append('|').Append(mask).Append('|');
            for (int i = 0; i < 6; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                BlockFacing face = BlockFacing.ALLFACES[i];
                bool connected = (real & (1 << i)) != 0;
                if (connected && StovePipes.IsChimney(ba.GetBlock(Pos.AddCopy(face)))) cmask |= 1 << i;
                int[] n = StovePipes.NeighbourOffset(ba, Pos, face, o, connected);
                f[i] = new[] { StovePipes.Meet(o[0], n[0]), StovePipes.Meet(o[1], n[1]) };
                if ((cmask & (1 << i)) != 0) f[i] = new[] { 0, 0 };
                key.Append(Code(f[i][0])).Append(Code(f[i][1])).Append(',');
            }
            key.Append('|').Append(cmask);

            MeshData mesh = GetMesh(key.ToString(), tesselator, mask, cmask, o, f);
            if (mesh == null) return false;
            if (StovePipes.CeilingLift(ba, Pos, real))
            {
                mesh = mesh.Clone();
                mesh.Translate(0, 6f / 16f, 0);
            }
            mesher.AddMeshData(mesh);
            return true;
        }

        MeshData Tess(ITesselatorAPI t, Shape shape, List<string> names, float dx, float dz)
        {
            MeshData m;
            t.TesselateShape(Block, shape, out m, null, null, names.ToArray());
            if (dx != 0 || dz != 0) m.Translate(dx / 16f, 0, dz / 16f);
            return m;
        }

        MeshData GetMesh(string key, ITesselatorAPI tesselator, int mask, int cmask, int[] o, int[][] f)
        {
            Dictionary<string, MeshData> cache = ObjectCacheUtil.GetOrCreate(Api, "forgeandfeast-pipemeshes2", () => new Dictionary<string, MeshData>());
            lock (cache)
            {
                MeshData mesh;
                if (cache.TryGetValue(key, out mesh)) return mesh;

                Shape shape = Shape.TryGet(Api, new AssetLocation("forgeandfeast", "shapes/block/stovepipe.json"));
                if (shape == null) return null;

                string[] faceNames = { "north", "east", "south", "west", "up", "down" };
                List<string> body = new List<string> { "core" };
                List<string> fixedPart = new List<string>();

                // up and down: part of the body, with a plate where the joint sits somewhere else
                for (int i = 4; i < 6; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    string nm = faceNames[i];
                    bool jog = f[i][0] != o[0] || f[i][1] != o[1];
                    body.Add(jog ? "arm-" + nm + "-s" : "arm-" + nm);
                    if (jog) fixedPart.Add((i == 4 ? "vu_" : "vd_") + Code(o[0]) + Code(o[1]) + "_" + Code(f[i][0]) + Code(f[i][1]));
                    if ((cmask & (1 << i)) != 0) fixedPart.Add("cflange-" + nm);
                }
                mesh = Tess(tesselator, shape, body, o[0], o[1]);

                for (int i = 0; i < 4; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    bool ns = i == 0 || i == 2;
                    int along = ns ? o[1] : o[0];
                    int lat = ns ? o[0] : o[1];
                    int flat = ns ? f[i][0] : f[i][1];
                    List<string> arm = new List<string> { "h" + Dirs[i] + "_" + Code(along) };
                    MeshData am = Tess(tesselator, shape, arm, ns ? lat : 0, ns ? 0 : lat);
                    mesh.AddMeshData(am);
                    if (lat != flat) fixedPart.Add("j" + Dirs[i] + "_" + Code(lat) + "_" + Code(flat));
                    if ((cmask & (1 << i)) != 0) fixedPart.Add("cflange-" + faceNames[i]);
                }

                if (fixedPart.Count > 0) mesh.AddMeshData(Tess(tesselator, shape, fixedPart, 0, 0));
                cache[key] = mesh;
                return mesh;
            }
        }
    }
}
