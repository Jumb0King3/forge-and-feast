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
        /// The main block of a tall station: the Smoker's upper half is a separate multiblock part, which
        /// points back at the real station below it. Other blocks are returned as they are.
        /// </summary>
        public static Block MainBlock(IBlockAccessor ba, BlockPos pos, Block b, out BlockPos mainPos)
        {
            mainPos = pos;
            if (b is BlockMultiblock mb)
            {
                mainPos = pos.AddCopy(mb.OffsetInv.X, mb.OffsetInv.Y, mb.OffsetInv.Z);
                return ba.GetBlock(mainPos);
            }
            return b;
        }

        /// <summary>A fire a pipe can join at this position, counting the upper half of a tall station.</summary>
        public static bool IsSourceAt(IBlockAccessor ba, BlockPos pos, Block b)
        {
            if (b == null || b.Id == 0) return false;
            if (b is BlockMultiblock)
            {
                BlockPos mp;
                Block main = MainBlock(ba, pos, b, out mp);
                return main != null && !(main is BlockMultiblock) && IsSource(main);
            }
            return IsSource(b);
        }

        public static bool ConnectsAt(IBlockAccessor ba, BlockPos pos, Block b)
        {
            return IsPipe(b) || IsChimney(b) || IsSourceAt(ba, pos, b);
        }

        /// <summary>
        /// A block a pipe can pass through to a chimney above: a solid roof, or one of the game's partial
        /// roofing pieces (slopes, ridges, thatch, shingles) that are not solid on every side.
        /// </summary>
        public static bool IsRoofLike(Block b)
        {
            if (b == null || b.Id == 0 || IsPipe(b) || IsChimney(b) || b is BlockMultiblock) return false;
            if (b.SideSolid[BlockFacing.DOWN.Index]) return true;
            string path = b.Code?.Path;
            if (path != null)
            {
                if (path.Contains("roof") || path.Contains("thatch") || path.Contains("shingle") || path.Contains("slate")) return true;
            }
            return false;
        }

        /// <summary>
        /// Where a pipe standing on a fire (directly, or at the bottom of a straight riser) sits so it lines up
        /// with that fire's flue: over the rear stub of a Range, Kiln, Retort or Potbelly, over the middle of
        /// the Smoker's roof vent or a clay oven. Returns {x, z} in sixteenths, or null when not on a fire.
        /// </summary>
        public static int[] StationOffset(IBlockAccessor ba, BlockPos pos)
        {
            BlockPos p = pos.Copy();
            for (int i = 0; i < MaxPipes; i++)
            {
                p.Y -= 1;
                Block b = ba.GetBlock(p);
                if (b is BlockFuelHeater || b is BlockPotbellyStove)
                {
                    if (ba.GetBlockEntity(p) is BESmoker) return new[] { 0, 0 };
                    string side = b.Variant?["side"];
                    BlockFacing f = side == null ? null : BlockFacing.FromCode(side);
                    if (f == null) return new[] { 0, 0 };
                    return new[] { f.Opposite.Normali.X * 7, f.Opposite.Normali.Z * 7 };
                }
                if (IsSourceAt(ba, p, b)) return new[] { 0, 0 };
                if (!IsPipe(b)) return null;
            }
            return null;
        }

        /// <summary>The highest block a station fills: itself, or the top of a tall one (the Smoker).</summary>
        public static BlockPos StationTop(IBlockAccessor ba, BlockPos station)
        {
            BlockPos top = station.Copy();
            for (int dy = 1; dy <= 3; dy++)
            {
                BlockPos up = station.UpCopy(dy);
                Block ub = ba.GetBlock(up);
                if (ub is BlockMultiblock mb && up.AddCopy(mb.OffsetInv.X, mb.OffsetInv.Y, mb.OffsetInv.Z).Equals(station)) top = up;
                else break;
            }
            return top;
        }

        /// <summary>True when a stovepipe (or a chimney) sits on top of this station, so it should hide its own flue stub.</summary>
        public static bool PipeOnTop(IBlockAccessor ba, BlockPos station)
        {
            Block above = ba.GetBlock(StationTop(ba, station).Up());
            return IsPipe(above) || IsChimney(above);
        }

        /// <summary>
        /// A pipe that ends against a roof of one or two solid blocks with a chimney on the other side counts as
        /// passing through it, so the roof never has to be opened. Returns the chimney position or null.
        /// </summary>
        public static BlockPos RoofChimney(IBlockAccessor ba, BlockPos pipe)
        {
            BlockPos u = pipe.UpCopy();
            if (!IsRoofLike(ba.GetBlock(u))) return null;
            BlockPos t = new BlockPos();
            for (int k = 0; k < 3; k++)
            {
                u.Up();
                // A chimney straight above, or one block over where the roof slopes away
                for (int d = 0; d < 5; d++)
                {
                    int dx = d == 1 ? 1 : (d == 2 ? -1 : 0), dz = d == 3 ? 1 : (d == 4 ? -1 : 0);
                    t.Set(u.X + dx, u.Y, u.Z + dz);
                    if (IsChimney(ba.GetBlock(t))) return t.Copy();
                }
                Block b = ba.GetBlock(u);
                if (!IsRoofLike(b)) return null;
            }
            return null;
        }

        static bool SolidFace(IBlockAccessor ba, BlockPos p, BlockFacing face)
        {
            Block b = ba.GetBlock(p);
            return b != null && b.Id != 0 && !IsPipe(b) && !IsChimney(b) && !(b is BlockFuelHeater) && !(b is BlockPotbellyStove) && b.SideSolid[face.Index];
        }

        /// <summary>
        /// How high a pipe's sideways run sits in its blocks, in sixteenths: +6 tucked up under a roof or ceiling,
        /// -6 lying on a floor, 0 in the middle. Worked out for the whole straight-across run (every pipe joined
        /// sideways on the same level), so all of its joints line up, including T-junctions and the bends where
        /// it turns up into a chimney or down to a stove.
        /// </summary>
        public static int RunLevel(IBlockAccessor ba, BlockPos pos)
        {
            int real = ConnectionMask(ba, pos);
            if ((BlockStovePipe.VisualMask(real) & 15) == 0) return 0;

            List<BlockPos> run = new List<BlockPos> { pos.Copy() };
            Dictionary<long, bool> seen = new Dictionary<long, bool>();
            seen[Key(pos)] = true;
            bool ceiling = false, floor = false, sideFire = false;
            BlockPos n = pos.Copy();
            for (int k = 0; k < run.Count && k < MaxPipes; k++)
            {
                BlockPos p = run[k];
                int m = k == 0 ? real : ConnectionMask(ba, p);
                if ((m & (1 << BlockFacing.UP.Index)) == 0)
                {
                    BlockPos u = p.UpCopy();
                    if (SolidFace(ba, u, BlockFacing.DOWN) || IsRoofLike(ba.GetBlock(u))) ceiling = true;
                }
                if ((m & (1 << BlockFacing.DOWN.Index)) == 0 && SolidFace(ba, p.DownCopy(), BlockFacing.UP)) floor = true;
                for (int i = 0; i < 4; i++)
                {
                    if ((m & (1 << i)) == 0) continue;
                    BlockFacing f = BlockFacing.ALLFACES[i];
                    n.Set(p.X + f.Normali.X, p.Y, p.Z + f.Normali.Z);
                    Block nb = ba.GetBlock(n);
                    if (IsPipe(nb))
                    {
                        long key = Key(n);
                        if (!seen.ContainsKey(key)) { seen[key] = true; run.Add(n.Copy()); }
                    }
                    else if (IsSourceAt(ba, n, nb)) sideFire = true;
                }
            }
            if (ceiling) return 6;
            // A run joined to the side of a stove stays at the stove's middle rather than dropping to the floor
            if (floor && !sideFire) return -6;
            return 0;
        }

        public static bool IsChimney(Block b) => b != null && b.Id != 0 && b.HasBehavior<BlockBehaviorChimney>();

        static bool IsWall(IBlockAccessor ba, BlockPos pos, BlockFacing f)
        {
            Block b = ba.GetBlock(pos.AddCopy(f));
            return b != null && b.Id != 0 && !(b is BlockStovePipe) && !IsSourceAt(ba, pos.AddCopy(f), b) && b.SideSolid[f.Opposite.Index];
        }

        /// <summary>
        /// Where this pipe sits inside its block, as {x, z} offsets in sixteenths (-6, 0 or 6). A riser on a stove
        /// lines up with the stove's rear stub. Otherwise the pipe hugs a wall beside it, or stays central.
        /// </summary>
        public static int[] Offset(IBlockAccessor ba, BlockPos pos)
        {
            int[] st = StationOffset(ba, pos);
            if (st != null) return st;
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
            if (IsSourceAt(ba, n, b)) return mine;
            return new[] { 0, 0 };
        }

        /// <summary>Fires a stovepipe can connect to. The firepit is left out on purpose.</summary>
        public static bool IsSource(Block b)
        {
            if (b == null || b.Id == 0) return false;
            if (b is BlockFuelHeater) return true;
            if (b is BlockPotbellyStove) return true;
            return b.Code != null && b.Code.Domain == "game" && b.Code.Path.StartsWith("clayoven");
        }


        /// <summary>Bit per face (BlockFacing.Index: N, E, S, W, U, D) that has something to join.</summary>
        public static int ConnectionMask(IBlockAccessor ba, BlockPos pos)
        {
            int mask = 0;
            BlockPos tmp = pos.Copy();
            for (int i = 0; i < 6; i++)
            {
                BlockFacing f = BlockFacing.ALLFACES[i];
                tmp.Set(pos.X + f.Normali.X, pos.Y + f.Normali.Y, pos.Z + f.Normali.Z);
                if (ConnectsAt(ba, tmp, ba.GetBlock(tmp))) mask |= 1 << f.Index;
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
            // The station's cells: itself, plus the upper half of a tall one (the Smoker)
            List<BlockPos> cells = new List<BlockPos> { source.Copy() };
            for (int dy = 1; dy <= 3; dy++)
            {
                BlockPos up = source.UpCopy(dy);
                Block ub = ba.GetBlock(up);
                if (ub is BlockMultiblock mb && up.AddCopy(mb.OffsetInv.X, mb.OffsetInv.Y, mb.OffsetInv.Z).Equals(source)) cells.Add(up);
            }
            foreach (BlockPos cell in cells)
            {
                for (int i = 0; i < 6; i++)
                {
                    BlockFacing f = BlockFacing.ALLFACES[i];
                    n.Set(cell.X + f.Normali.X, cell.Y + f.Normali.Y, cell.Z + f.Normali.Z);
                    if (IsPipe(ba.GetBlock(n)) && !seen.ContainsKey(Key(n)) && tail < MaxPipes)
                    {
                        seen[Key(n)] = true;
                        queue[tail++] = n.Copy();
                    }
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
                    if (IsSourceAt(ba, n, nb))
                    {
                        BlockPos mp;
                        MainBlock(ba, n, nb, out mp);
                        if (SourceIsBurning(ba.GetBlockEntity(mp))) return true;
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
            if (be is BEPotbellyStove potbelly) return potbelly.IsBurning;
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
            return Boxes(real, StovePipes.Offset(ba, pos), StovePipes.RunLevel(ba, pos));
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

        static Cuboidf[] Boxes(int mask, int[] o, int level)
        {
            mask = VisualMask(mask);
            List<Cuboidf> boxes = new List<Cuboidf>();
            float ox = o[0] / 16f, oz = o[1] / 16f, y = level / 16f;
            float y1 = R1 + y, y2 = R2 + y;
            boxes.Add(new Cuboidf(R1 + ox, y1, R1 + oz, R2 + ox, y2, R2 + oz));
            if ((mask & (1 << BlockFacing.DOWN.Index)) != 0 && y1 > 0.01f) boxes.Add(new Cuboidf(R1 + ox, 0, R1 + oz, R2 + ox, y1, R2 + oz));
            if ((mask & (1 << BlockFacing.UP.Index)) != 0 && y2 < 0.99f) boxes.Add(new Cuboidf(R1 + ox, y2, R1 + oz, R2 + ox, 1, R2 + oz));
            if ((mask & (1 << BlockFacing.NORTH.Index)) != 0) boxes.Add(new Cuboidf(R1 + ox, y1, 0, R2 + ox, y2, R2 + oz));
            if ((mask & (1 << BlockFacing.SOUTH.Index)) != 0) boxes.Add(new Cuboidf(R1 + ox, y1, R1 + oz, R2 + ox, y2, 1));
            if ((mask & (1 << BlockFacing.WEST.Index)) != 0) boxes.Add(new Cuboidf(0, y1, R1 + oz, R2 + ox, y2, R2 + oz));
            if ((mask & (1 << BlockFacing.EAST.Index)) != 0) boxes.Add(new Cuboidf(R1 + ox, y1, R1 + oz, 1, y2, R2 + oz));
            for (int i = 0; i < boxes.Count; i++)
            {
                Cuboidf c = boxes[i];
                if (c.Y1 < 0) c.Y1 = 0;
                if (c.Y2 > 1) c.Y2 = 1;
            }
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
                if (StovePipes.IsPipe(nb) || StovePipes.IsSourceAt(ba, n, nb)) pipeCount++;
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

    /// <summary>
    /// Draws the pipe with arms toward whatever it's joined to. The sideways part (middle and side arms) sits at
    /// its run's level; the up and down arms are built to reach from there to the top and bottom of the block,
    /// so every joint lands on the block face where the next pipe, chimney or stove picks it up.
    /// </summary>
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
            int level = StovePipes.RunLevel(ba, Pos);

            int cmask = 0, smask = 0;
            bool smokerBelow = false;
            int[][] f = new int[6][];
            System.Text.StringBuilder key = new System.Text.StringBuilder();
            key.Append(Code(o[0])).Append(Code(o[1])).Append('|').Append(level).Append('|').Append(mask).Append('|');
            for (int i = 0; i < 6; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                BlockFacing face = BlockFacing.ALLFACES[i];
                bool connected = (real & (1 << i)) != 0;
                BlockPos np = Pos.AddCopy(face);
                Block nb = ba.GetBlock(np);
                if (connected && StovePipes.IsChimney(nb)) cmask |= 1 << i;
                if (connected && StovePipes.IsSourceAt(ba, np, nb))
                {
                    smask |= 1 << i;
                    if (face == BlockFacing.DOWN)
                    {
                        BlockPos mp;
                        StovePipes.MainBlock(ba, np, nb, out mp);
                        smokerBelow = ba.GetBlockEntity(mp) is BESmoker;
                    }
                }
                int[] n = StovePipes.NeighbourOffset(ba, Pos, face, o, connected);
                f[i] = new[] { StovePipes.Meet(o[0], n[0]), StovePipes.Meet(o[1], n[1]) };
                if ((cmask & (1 << i)) != 0) f[i] = new[] { 0, 0 };
                key.Append(Code(f[i][0])).Append(Code(f[i][1])).Append(',');
            }
            key.Append('|').Append(cmask).Append('|').Append(smask).Append(smokerBelow ? "s" : "");

            MeshData mesh = GetMesh(key.ToString(), tesselator, mask, cmask, smask, smokerBelow, o, level, f);
            if (mesh == null) return false;
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

        static string Va(int from, int to) { return "va_" + from + "_" + to; }

        MeshData GetMesh(string key, ITesselatorAPI tesselator, int mask, int cmask, int smask, bool smokerBelow, int[] o, int level, int[][] f)
        {
            Dictionary<string, MeshData> cache = ObjectCacheUtil.GetOrCreate(Api, "forgeandfeast-pipemeshes3", () => new Dictionary<string, MeshData>());
            lock (cache)
            {
                MeshData mesh;
                if (cache.TryGetValue(key, out mesh)) return mesh;

                Shape shape = Shape.TryGet(Api, new AssetLocation("forgeandfeast", "shapes/block/stovepipe.json"));
                if (shape == null) return null;

                string[] faceNames = { "north", "east", "south", "west", "up", "down" };

                // Sideways part: middle, side arms, joint plates, flanges and stubs into a stove; lifted to the run's level
                MeshData side = Tess(tesselator, shape, new List<string> { "core" }, o[0], o[1]);
                List<string> sideFixed = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    if ((mask & (1 << i)) == 0) continue;
                    bool ns = i == 0 || i == 2;
                    int along = ns ? o[1] : o[0];
                    int lat = ns ? o[0] : o[1];
                    int flat = ns ? f[i][0] : f[i][1];
                    side.AddMeshData(Tess(tesselator, shape, new List<string> { "h" + Dirs[i] + "_" + Code(along) }, ns ? lat : 0, ns ? 0 : lat));
                    if (lat != flat) sideFixed.Add("j" + Dirs[i] + "_" + Code(lat) + "_" + Code(flat));
                    if ((cmask & (1 << i)) != 0) sideFixed.Add("cflange-" + faceNames[i]);
                    if ((smask & (1 << i)) != 0) side.AddMeshData(Tess(tesselator, shape, new List<string> { "x" + Dirs[i] }, ns ? lat : 0, ns ? 0 : lat));
                }
                if (sideFixed.Count > 0) side.AddMeshData(Tess(tesselator, shape, sideFixed, 0, 0));
                if (level != 0) side.Translate(0, level / 16f, 0);
                mesh = side;

                // Up and down: from the middle at its level to the top and bottom faces
                int coreBottom = 7 + level, coreTop = 9 + level;
                List<string> vert = new List<string>();
                List<string> vertFixed = new List<string>();
                if ((mask & (1 << BlockFacing.UP.Index)) != 0)
                {
                    bool jog = f[4][0] != o[0] || f[4][1] != o[1];
                    if (jog)
                    {
                        if (coreTop < 12) vert.Add(Va(coreTop, 12));
                        vertFixed.Add("vu_" + Code(o[0]) + Code(o[1]) + "_" + Code(f[4][0]) + Code(f[4][1]));
                    }
                    else if (coreTop < 16) vert.Add(Va(coreTop, 16));
                    if ((cmask & (1 << BlockFacing.UP.Index)) != 0) vertFixed.Add("cflange-up");
                }
                if ((mask & (1 << BlockFacing.DOWN.Index)) != 0)
                {
                    bool jog = f[5][0] != o[0] || f[5][1] != o[1];
                    if (jog)
                    {
                        if (coreBottom > 4) vert.Add(Va(4, coreBottom));
                        vertFixed.Add("vd_" + Code(o[0]) + Code(o[1]) + "_" + Code(f[5][0]) + Code(f[5][1]));
                    }
                    else if (coreBottom > 0) vert.Add(Va(0, coreBottom));
                    if ((cmask & (1 << BlockFacing.DOWN.Index)) != 0) vertFixed.Add("cflange-down");
                    if ((smask & (1 << BlockFacing.DOWN.Index)) != 0)
                    {
                        // Reaches down into the stove's flue; on the Smoker it goes into the roof ridge with a collar
                        vert.Add("xD");
                        if (smokerBelow) vert.Add("cflange-down");
                    }
                }
                if (vert.Count > 0) mesh.AddMeshData(Tess(tesselator, shape, vert, o[0], o[1]));
                if (vertFixed.Count > 0) mesh.AddMeshData(Tess(tesselator, shape, vertFixed, 0, 0));

                cache[key] = mesh;
                return mesh;
            }
        }
    }
}
