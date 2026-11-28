using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
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
            // Chiselled blocks (base game chisel, QP's Chisel Tools): a carved roof or ceiling
            if (b is BlockMicroBlock) return true;
            // VS Roofing's gridless roofs (slopes, corners, ridges and their framing)
            if (b.Code?.Domain == "vsroofing") return true;
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
                if (b is BlockCrucibleFurnace) return FurnaceFlue(b);
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

        /// <summary>
        /// The Crucible Furnace's flue rises from its right-hand pivot (model x 15, z 8 facing north), so it
        /// stays joined to the drum while it tips. Returns {x, z} in sixteenths for the block's turn.
        /// </summary>
        public static int[] FurnaceFlue(Block b)
        {
            double r = (b?.Shape?.rotateY ?? 0) * GameMath.DEG2RAD;
            int x = (int)Math.Round(7 * Math.Cos(r)), z = (int)Math.Round(-7 * Math.Sin(r));
            return new[] { x, z };
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

        /// <summary>How many blocks of roof a pipe can pass up through (steep VS Roofing roofs are thick).</summary>
        public const int MaxRoofLayers = 8;

        /// <summary>
        /// A pipe that ends against a roof with a chimney on the other side counts as passing through it, so the
        /// roof never has to be opened. Returns the chimney position or null.
        /// </summary>
        public static BlockPos RoofChimney(IBlockAccessor ba, BlockPos pipe)
        {
            BlockPos outlet; int layers;
            return RoofExit(ba, pipe, out outlet, out layers);
        }

        /// <summary>
        /// Looks up through the roof above a pipe. Returns a chimney on top of the roof (straight above, or one
        /// block over where the roof slopes away), or null. With no chimney, outlet is the open-air block just
        /// above the roof when it is under open sky, and layers is how many roof blocks the pipe passes through.
        /// </summary>
        public static BlockPos RoofExit(IBlockAccessor ba, BlockPos pipe, out BlockPos outlet, out int layers)
        {
            outlet = null; layers = 0;
            BlockPos u = pipe.UpCopy();
            if (!IsRoofLike(ba.GetBlock(u))) return null;
            BlockPos t = new BlockPos();
            for (int k = 0; k < MaxRoofLayers; k++)
            {
                layers = k + 1;
                u.Up();
                for (int d = 0; d < 5; d++)
                {
                    int dx = d == 1 ? 1 : (d == 2 ? -1 : 0), dz = d == 3 ? 1 : (d == 4 ? -1 : 0);
                    t.Set(u.X + dx, u.Y, u.Z + dz);
                    if (IsChimney(ba.GetBlock(t))) { outlet = null; return t.Copy(); }
                }
                Block b = ba.GetBlock(u);
                if (IsRoofLike(b)) continue;
                if (b.Id == 0 && ba.GetRainMapHeightAt(u.X, u.Z) < u.Y) outlet = u.Copy();
                return null;
            }
            return null;
        }

        /// <summary>
        /// The pipe goes on up through the roof into the open air and vents there under a rain cap, with no
        /// chimney needed. Outlets through a roof are kept at least this many blocks apart (side to side).</summary>
        public const int OutletSpacing = 3;

        /// <summary>
        /// As OutletCandidate, but only one outlet per run of pipe, and none within OutletSpacing blocks of
        /// another: the pipe with the lowest position key wins, so every pipe works out the same answer.
        /// </summary>
        public static BlockPos RoofOutlet(IBlockAccessor ba, BlockPos pipe, out int layers)
        {
            int myTier;
            BlockPos outlet = OutletCandidate(ba, pipe, out layers, out myTier);
            if (outlet == null) return null;
            long mine = Key(pipe);
            int other, otherTier;

            // The rest of this pipe's run
            BlockPos[] queue = new BlockPos[MaxPipes];
            Dictionary<long, bool> seen = new Dictionary<long, bool>();
            int head = 0, tail = 0;
            queue[tail++] = pipe.Copy();
            seen[mine] = true;
            BlockPos n = pipe.Copy();
            while (head < tail)
            {
                BlockPos p = queue[head++];
                if (head > 1 && OutletCandidate(ba, p, out other, out otherTier) != null && Beats(otherTier, Key(p), myTier, mine)) { layers = 0; return null; }
                for (int i = 0; i < 6; i++)
                {
                    BlockFacing f = BlockFacing.ALLFACES[i];
                    n.Set(p.X + f.Normali.X, p.Y + f.Normali.Y, p.Z + f.Normali.Z);
                    if (!IsPipe(ba.GetBlock(n))) continue;
                    long k = Key(n);
                    if (seen.ContainsKey(k) || tail >= MaxPipes) continue;
                    seen[k] = true;
                    queue[tail++] = n.Copy();
                }
                Diagonal(ba, p, queue, ref tail, seen);
            }

            // Other runs' outlets close by
            int r = OutletSpacing - 1;
            BlockPos q = new BlockPos();
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dy = -4; dy <= 4; dy++)
                    {
                        if (dx == 0 && dz == 0 && dy == 0) continue;
                        q.Set(pipe.X + dx, pipe.Y + dy, pipe.Z + dz);
                        long k = Key(q);
                        if (seen.ContainsKey(k) || !IsPipe(ba.GetBlock(q))) continue;
                        if (OutletCandidate(ba, q, out other, out otherTier) != null && Beats(otherTier, k, myTier, mine)) { layers = 0; return null; }
                    }
                }
            }
            return outlet;
        }

        /// <summary>The better outlet of two: the end of a run before a bend, a bend before a straight piece, then the lower key.</summary>
        static bool Beats(int tierA, long keyA, int tierB, long keyB)
        {
            return tierA < tierB || (tierA == tierB && keyA < keyB);
        }

        /// <summary>
        /// The end of a run that could go up through the roof above it: nothing joined above, at most one pipe or
        /// stove joined, and the roof is open to the sky on top. Returns the open-air block above the roof, or null.
        /// </summary>
        public static BlockPos OutletCandidate(IBlockAccessor ba, BlockPos pipe, out int layers)
        {
            int tier;
            return OutletCandidate(ba, pipe, out layers, out tier);
        }

        /// <summary>
        /// As above, with how good a spot it is: 0 the end of the run, 1 a bend or tee tucked under the roof
        /// (a run that turns along the ceiling to join another stove), 2 a straight piece along the ceiling.
        /// </summary>
        public static BlockPos OutletCandidate(IBlockAccessor ba, BlockPos pipe, out int layers, out int tier)
        {
            layers = 0; tier = 0;
            int mask = ConnectionMask(ba, pipe);
            if ((mask & (1 << BlockFacing.UP.Index)) != 0) return null;
            int joined = 0;
            for (int i = 0; i < 6; i++) if ((mask & (1 << i)) != 0) joined++;
            int diag = Bits(DiagonalMask(ba, pipe));
            joined += diag;
            if (joined > 1)
            {
                int ns = (1 << BlockFacing.NORTH.Index) | (1 << BlockFacing.SOUTH.Index);
                int ew = (1 << BlockFacing.EAST.Index) | (1 << BlockFacing.WEST.Index);
                tier = joined == 2 && diag == 0 && (mask == ns || mask == ew) ? 2 : 1;
            }
            BlockPos outlet;
            if (RoofExit(ba, pipe, out outlet, out layers) != null) return null;
            if (outlet == null) layers = 0;
            return outlet;
        }

        static bool SolidFace(IBlockAccessor ba, BlockPos p, BlockFacing face)
        {
            Block b = ba.GetBlock(p);
            return b != null && b.Id != 0 && !IsPipe(b) && !IsChimney(b) && !(b is BlockFuelHeater) && !(b is BlockCrucibleFurnace) && !(b is BlockPotbellyStove) && b.SideIsSolid(ba, p, face.Index);
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

        /// <summary>
        /// A chimney block: anything with the game's chimney behaviour, or a block named as a chimney. Real Smoke
        /// takes the game's chimney behaviour off the Clay Brick Chimney (it moves smoke with its own pump), and
        /// some mods' chimneys never had it, so the name counts too.
        /// </summary>
        public static bool IsChimney(Block b)
        {
            if (b == null || b.Id == 0 || b is BlockStovePipe) return false;
            if (b.HasBehavior<BlockBehaviorChimney>()) return true;
            string path = b.Code?.Path;
            if (path == null) return false;
            if (path.Contains("chimney")) return true;
            // Hearth & Draught's flues and chimney pots (and its dampers, unless shut)
            if (b.Code.Domain == "hearthanddraught" && path.Contains("flue")) return b.Variant?["state"] != "closed";
            return false;
        }

        /// <summary>True when Real Smoke is installed: it simulates the smoke, so ours stays out of its way.</summary>
        public static bool RealSmoke(ICoreAPI api)
        {
            return api?.ModLoader?.IsModEnabled("realsmoke") == true;
        }

        static bool IsWall(IBlockAccessor ba, BlockPos pos, BlockFacing f)
        {
            Block b = ba.GetBlock(pos.AddCopy(f));
            BlockPos np = pos.AddCopy(f);
            return b != null && b.Id != 0 && !(b is BlockStovePipe) && !IsSourceAt(ba, np, b) && b.SideIsSolid(ba, np, f.Opposite.Index);
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
            if (b is BlockFuelHeater || b is BlockCrucibleFurnace) return true;
            if (b is BlockPotbellyStove) return true;
            return b.Code != null && b.Code.Domain == "game" && b.Code.Path.StartsWith("clayoven");
        }


        /// <summary>Bit per face (BlockFacing.Index: N, E, S, W, U, D) that has something to join.</summary>
        /// <summary>
        /// The 12 diagonal directions a pipe can step to round a corner: up or down and sideways at once (over an
        /// eave or a ledge), or sideways in two directions (round the corner of a wall). Same order as the dg
        /// pieces in the pipe model.
        /// </summary>
        public static readonly int[][] Diagonals =
        {
            new[] { 1, 1, 0 }, new[] { -1, 1, 0 }, new[] { 0, 1, 1 }, new[] { 0, 1, -1 },
            new[] { 1, -1, 0 }, new[] { -1, -1, 0 }, new[] { 0, -1, 1 }, new[] { 0, -1, -1 },
            new[] { 1, 0, 1 }, new[] { 1, 0, -1 }, new[] { -1, 0, 1 }, new[] { -1, 0, -1 }
        };

        /// <summary>
        /// True when the pipe at pos joins a pipe diagonally across in direction d. Only when neither block in
        /// the corner between them is a pipe, so a pipe that already goes round the corner the square way, or
        /// two runs side by side, never get extra diagonal joins. Works the same from either end.
        /// </summary>
        public static bool DiagonalLink(IBlockAccessor ba, BlockPos pos, int[] d)
        {
            if (!IsPipe(ba.GetBlock(pos.X + d[0], pos.Y + d[1], pos.Z + d[2], BlockLayersAccess.Default))) return false;
            int ax = d[0], ay = d[1], az = d[2];
            // The two corner blocks: one step along each of the two directions on their own
            int c1x = ax != 0 ? ax : 0, c1y = ax != 0 ? 0 : ay, c1z = ax != 0 ? 0 : az;
            int c2x = d[0] - c1x, c2y = d[1] - c1y, c2z = d[2] - c1z;
            if (IsPipe(ba.GetBlock(pos.X + c1x, pos.Y + c1y, pos.Z + c1z, BlockLayersAccess.Default))) return false;
            if (IsPipe(ba.GetBlock(pos.X + c2x, pos.Y + c2y, pos.Z + c2z, BlockLayersAccess.Default))) return false;
            return true;
        }

        /// <summary>Which of the 12 diagonal directions this pipe is joined in, one bit each.</summary>
        public static int DiagonalMask(IBlockAccessor ba, BlockPos pos)
        {
            if (!IsPipe(ba.GetBlock(pos))) return 0;
            int mask = 0;
            for (int i = 0; i < Diagonals.Length; i++) if (DiagonalLink(ba, pos, Diagonals[i])) mask |= 1 << i;
            return mask;
        }

        /// <summary>A block a pipe can be drawn through on its way round a corner: air, or plants and the like.</summary>
        static bool OpenCell(IBlockAccessor ba, BlockPos p)
        {
            Block b = ba.GetBlock(p);
            if (b == null || IsPipe(b)) return false;
            return b.Id == 0 || b.Replaceable >= 6000;
        }

        static int FaceIndexOf(int x, int y, int z)
        {
            for (int i = 0; i < 6; i++)
            {
                Vec3i n = BlockFacing.ALLFACES[i].Normali;
                if (n.X == x && n.Y == y && n.Z == z) return i;
            }
            return -1;
        }

        /// <summary>
        /// For a diagonal join in direction d: which face of this pipe the join goes out of, so the pipe goes round
        /// the corner through the open block in the corner (round the outside of a wall corner, or out and then up
        /// past an eave), instead of cutting across it. -1 when both corner blocks are solid: then it's drawn as a
        /// stepped diagonal. Both pipes pick the same corner block.
        /// </summary>
        public static int CornerFace(IBlockAccessor ba, BlockPos pos, int[] d)
        {
            int ax = d[0] != 0 ? d[0] : 0, ay = d[0] != 0 ? 0 : d[1], az = d[0] != 0 ? 0 : d[2];
            int bx = d[0] - ax, by = d[1] - ay, bz = d[2] - az;
            BlockPos c1 = pos.AddCopy(ax, ay, az), c2 = pos.AddCopy(bx, by, bz);
            bool o1 = OpenCell(ba, c1), o2 = OpenCell(ba, c2);
            bool pickA;
            if (o1 && !o2) pickA = true;
            else if (o2 && !o1) pickA = false;
            else if (!o1 && !o2) return -1;
            else if (c1.Y != c2.Y) pickA = c1.Y < c2.Y;
            else pickA = Key(c1) < Key(c2);
            return pickA ? FaceIndexOf(ax, ay, az) : FaceIndexOf(bx, by, bz);
        }

        /// <summary>The faces this pipe's diagonal joins leave by (round corners), one bit per face.</summary>
        public static int CornerMask(IBlockAccessor ba, BlockPos pos, int diag)
        {
            int mask = 0;
            for (int i = 0; i < Diagonals.Length; i++)
            {
                if ((diag & (1 << i)) == 0) continue;
                int f = CornerFace(ba, pos, Diagonals[i]);
                if (f >= 0) mask |= 1 << f;
            }
            return mask;
        }

        /// <summary>The diagonal joins that cut across a solid corner and are drawn as a stepped diagonal.</summary>
        public static int SteppedMask(IBlockAccessor ba, BlockPos pos, int diag)
        {
            int mask = 0;
            for (int i = 0; i < Diagonals.Length; i++)
            {
                if ((diag & (1 << i)) != 0 && CornerFace(ba, pos, Diagonals[i]) < 0) mask |= 1 << i;
            }
            return mask;
        }

        /// <summary>Where a pipe's middle sits in its block, in sixteenths: x, y, z.</summary>
        public static int[] Middle(IBlockAccessor ba, BlockPos pos)
        {
            int[] o = Offset(ba, pos);
            return new[] { 8 + o[0], 8 + RunLevel(ba, pos), 8 + o[1] };
        }

        public static int Bits(int mask)
        {
            int n = 0;
            for (; mask != 0; mask &= mask - 1) n++;
            return n;
        }

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

        public static long Key(BlockPos p) => ((long)(p.X & 0xFFFFFF) << 40) | ((long)(p.Y & 0xFFFF) << 24) | (long)(p.Z & 0xFFFFFF);

        /// <summary>
        /// Follows the pipes joined to a fire. Reaching a chimney gives a draft; a pipe end poking out of
        /// the fire's closed room into open air vents the smoke outside.
        /// </summary>
        public static VentResult FollowFromSource(IBlockAccessor ba, BlockPos source, Room enclosedRoom)
        {
            BlockPos chimney;
            return FollowFromSource(ba, source, enclosedRoom, out chimney);
        }

        /// <summary>As above, and also gives the chimney block the pipes lead to (null if none).</summary>
        public static VentResult FollowFromSource(IBlockAccessor ba, BlockPos source, Room enclosedRoom, out BlockPos chimney)
        {
            chimney = null;
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
                BlockPos roof = RoofChimney(ba, p);
                if (roof != null) { chimney = roof; return VentResult.Chimney; }
                // Up through the roof into a brick chimney top of its own: counts as a chimney, with its draft
                int roofLayers;
                BlockPos outletTop = RoofOutlet(ba, p, out roofLayers);
                if (outletTop != null) { chimney = outletTop; return VentResult.Chimney; }
                for (int i = 0; i < 6; i++)
                {
                    BlockFacing f = BlockFacing.ALLFACES[i];
                    n.Set(p.X + f.Normali.X, p.Y + f.Normali.Y, p.Z + f.Normali.Z);
                    Block nb = ba.GetBlock(n);
                    if (IsChimney(nb)) { chimney = n.Copy(); return VentResult.Chimney; }
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
                Diagonal(ba, p, queue, ref tail, seen);
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
                Diagonal(ba, p, queue, ref tail, seen);
            }
            return false;
        }

        /// <summary>Adds the pipes joined diagonally to p to a run being followed.</summary>
        static void Diagonal(IBlockAccessor ba, BlockPos p, BlockPos[] queue, ref int tail, Dictionary<long, bool> seen)
        {
            for (int i = 0; i < Diagonals.Length; i++)
            {
                int[] d = Diagonals[i];
                if (!DiagonalLink(ba, p, d)) continue;
                BlockPos n = p.AddCopy(d[0], d[1], d[2]);
                long k = Key(n);
                if (seen.ContainsKey(k) || tail >= MaxPipes) continue;
                seen[k] = true;
                queue[tail++] = n;
            }
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
            return WithCover(blockAccessor, pos, LiftedBoxes(blockAccessor, pos));
        }

        /// <summary>A covered pipe is also as solid as its cover.</summary>
        static Cuboidf[] WithCover(IBlockAccessor ba, BlockPos pos, Cuboidf[] pipe)
        {
            Cuboidf[] cover = (ba.GetBlockEntity(pos) as BEStovePipe)?.CoverBoxes();
            if (cover == null || cover.Length == 0) return pipe;
            Cuboidf[] all = new Cuboidf[pipe.Length + cover.Length];
            pipe.CopyTo(all, 0);
            cover.CopyTo(all, pipe.Length);
            return all;
        }

        /// <summary>
        /// Wrench in the offhand and a block in the main hand covers the pipe with that block (a chiselled block
        /// keeps its carved shape), so a pipe can pass through a wall without knocking a hole in it. Wrench in the
        /// offhand with an empty main hand takes the cover back off.
        /// </summary>
        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
        {
            BEStovePipe be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEStovePipe;
            ItemSlot off = byPlayer?.Entity?.LeftHandItemSlot;
            if (be == null || !PipeCover.IsWrench(off?.Itemstack)) return base.OnBlockInteractStart(world, byPlayer, blockSel);

            ItemSlot hand = byPlayer.InventoryManager.ActiveHotbarSlot;
            if (hand == null || hand.Empty)
            {
                if (be.Cover == null) return base.OnBlockInteractStart(world, byPlayer, blockSel);
                if (world.Side == EnumAppSide.Server) be.TakeCover(byPlayer);
                return true;
            }
            if (be.Cover != null) return true;
            if (!PipeCover.CanCover(hand.Itemstack)) return base.OnBlockInteractStart(world, byPlayer, blockSel);
            if (world.Side == EnumAppSide.Server) be.SetCover(byPlayer, hand);
            return true;
        }

        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
        {
            ItemStack[] drops = base.GetDrops(world, pos, byPlayer, dropQuantityMultiplier);
            ItemStack cover = (world.BlockAccessor.GetBlockEntity(pos) as BEStovePipe)?.Cover;
            if (cover == null) return drops;
            List<ItemStack> all = new List<ItemStack>();
            if (drops != null) all.AddRange(drops);
            all.Add(cover.Clone());
            return all.ToArray();
        }

        /// <summary>
        /// A stovepipe is a sealed tube: open only where it joins something or ends in open air. Real Smoke moves
        /// smoke through a block's sides the same way the game moves liquids, so this keeps smoke inside the pipe
        /// until it reaches the chimney or the open end.
        /// </summary>
        public override float GetLiquidBarrierHeightOnSide(BlockFacing face, BlockPos pos)
        {
            try
            {
                // Open sides for Real Smoke: where it joins something, and the open end of a run (straight on from
                // its one join), so smoke still comes out of a pipe end poking out through a wall
                int real = StovePipes.ConnectionMask(api.World.BlockAccessor, pos);
                int open = VisualMask(real);
                for (int i = 0; i < 6; i++) if (real == (1 << i)) open |= 1 << BlockFacing.ALLFACES[i].Opposite.Index;
                return (open & (1 << face.Index)) != 0 ? 0f : 1f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        // Both versions go to SolidAt. The game's own version with a block accessor just calls the one without,
        // so neither may call the other (or base) or they loop forever and crash the game.
        public override bool SideIsSolid(BlockPos pos, int faceIndex)
        {
            return SolidAt(api?.World?.BlockAccessor, pos, faceIndex);
        }

        public override bool SideIsSolid(IBlockAccessor blockAccessor, BlockPos pos, int faceIndex)
        {
            return SolidAt(blockAccessor ?? api?.World?.BlockAccessor, pos, faceIndex);
        }

        bool SolidAt(IBlockAccessor blockAccessor, BlockPos pos, int faceIndex)
        {
            BEStovePipe be = pos == null ? null : blockAccessor?.GetBlockEntity(pos) as BEStovePipe;
            if (be?.Cover != null) return be.CoverSideSolid(faceIndex);
            return SideSolid[faceIndex];
        }

        /// <summary>A covered pipe keeps a room sealed and warm like its cover would.</summary>
        public override int GetRetention(BlockPos pos, BlockFacing facing, EnumRetentionType type)
        {
            BEStovePipe be = api.World.BlockAccessor.GetBlockEntity(pos) as BEStovePipe;
            Block cover = be?.Cover?.Block;
            if (cover == null) return base.GetRetention(pos, facing, type);
            if (cover is BlockMicroBlock)
            {
                if (!be.CoverSideSolid(facing.Index)) return 0;
                Block material = be.CoverMaterial();
                return material != null ? material.GetRetention(pos, facing, type) : 1;
            }
            return cover.GetRetention(pos, facing, type);
        }

        static Cuboidf[] LiftedBoxes(IBlockAccessor ba, BlockPos pos)
        {
            // A pipe that lost its block entity is drawn from the plain model, a straight upright pipe in the middle
            // of the block, so its boxes have to be there too or it can't be aimed at and broken
            if (!(ba.GetBlockEntity(pos) is BEStovePipe)) return Plain;
            int real = StovePipes.ConnectionMask(ba, pos);
            int all3 = StovePipes.DiagonalMask(ba, pos);
            int corner = StovePipes.CornerMask(ba, pos, all3);
            int diag = StovePipes.SteppedMask(ba, pos, all3);
            int[] o = StovePipes.Offset(ba, pos);
            Cuboidf[] boxes = Boxes(VisualMask(real | corner, diag), o, StovePipes.RunLevel(ba, pos), true);
            if (diag == 0) return boxes;
            List<Cuboidf> all = new List<Cuboidf>(boxes);
            for (int i = 0; i < StovePipes.Diagonals.Length; i++)
            {
                if ((diag & (1 << i)) == 0) continue;
                int[] d = StovePipes.Diagonals[i];
                for (int k = 3; k <= 7; k += 2)
                {
                    float cx = (8 + k * d[0] + o[0]) / 16f, cy = (8 + k * d[1]) / 16f, cz = (8 + k * d[2] + o[1]) / 16f;
                    Cuboidf c = new Cuboidf(cx - 0.125f, cy - 0.125f, cz - 0.125f, cx + 0.125f, cy + 0.125f, cz + 0.125f);
                    c.X1 = GameMath.Clamp(c.X1, 0, 1); c.Y1 = GameMath.Clamp(c.Y1, 0, 1); c.Z1 = GameMath.Clamp(c.Z1, 0, 1);
                    c.X2 = GameMath.Clamp(c.X2, 0, 1); c.Y2 = GameMath.Clamp(c.Y2, 0, 1); c.Z2 = GameMath.Clamp(c.Z2, 0, 1);
                    all.Add(c);
                }
            }
            return all.ToArray();
        }

        static readonly Cuboidf[] Plain = { new Cuboidf(R1, 0, R1, R2, 1, R2) };

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return WithCover(blockAccessor, pos, LiftedBoxes(blockAccessor, pos));
        }

        /// <summary>As below, but a pipe joined diagonally doesn't also run straight through.</summary>
        public static int VisualMask(int mask, int diagMask)
        {
            if (diagMask == 0) return VisualMask(mask);
            return mask;
        }

        public static int VisualMask(int mask)
        {
            // A lone pipe stands upright. A pipe joined only from below or above runs straight through, so the
            // top of a riser is open. A sideways run just ends at its last pipe instead of sticking out past it
            // (round a wall corner it looked like it carried on to something that isn't there).
            if (mask == 0) return (1 << BlockFacing.UP.Index) | (1 << BlockFacing.DOWN.Index);
            if (mask == (1 << BlockFacing.UP.Index) || mask == (1 << BlockFacing.DOWN.Index))
                return (1 << BlockFacing.UP.Index) | (1 << BlockFacing.DOWN.Index);
            return mask;
        }

        static Cuboidf[] Boxes(int mask, int[] o, int level, bool maskIsVisual = false)
        {
            if (!maskIsVisual) mask = VisualMask(mask);
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
            if (StovePipes.RealSmoke(api)) { isWindAffected = false; return false; }
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

            pipeCount += StovePipes.Bits(StovePipes.DiagonalMask(ba, pos));

            if (!feedsChimney)
            {
                BlockPos roof = StovePipes.RoofChimney(ba, pos);
                if (roof != null) { feedsChimney = true; chimney = roof; }
            }

            int roofLayers = 0;
            BlockPos roofOutlet = feedsChimney ? null : StovePipes.RoofOutlet(ba, pos, out roofLayers);

            bool openEnd = !feedsChimney && roofOutlet == null && pipeCount <= 1 && (mask & (1 << BlockFacing.UP.Index)) == 0
                && ba.GetBlock(pos.UpCopy()).Id == 0;
            if (!feedsChimney && !openEnd && roofOutlet == null) return;
            if (!StovePipes.RunIsBurning(ba, pos)) return;

            if (feedsChimney)
            {
                BlockPos top = chimney.Copy();
                while (StovePipes.IsChimney(ba.GetBlock(top.UpCopy())) && top.Y - chimney.Y < 16) top.Up();
                smoke.MinPos.Set(top.X + 0.3, top.Y + 1.0, top.Z + 0.3);
            }
            else if (roofOutlet != null)
            {
                // Out of the top of the brick chimney top
                int[] so = StovePipes.Offset(ba, pos);
                double sx = GameMath.Clamp(so[0], -4, 4) / 16.0, sz = GameMath.Clamp(so[1], -4, 4) / 16.0;
                smoke.MinPos.Set(roofOutlet.X + 0.35 + sx, roofOutlet.Y + 0.8, roofOutlet.Z + 0.35 + sz);
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

        ItemStack cover;
        Cuboidf[] coverBoxes;
        bool[] coverSides;

        /// <summary>The block this pipe is covered with (one item), or null.</summary>
        public ItemStack Cover => cover;

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            RedrawDiagonals();
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            RedrawDiagonals();
        }

        /// <summary>
        /// Pipes only hear about changes on their six faces, so when a pipe comes or goes, the pipes diagonally
        /// round a corner from it are redrawn too, to grow or drop their half of the diagonal join.
        /// </summary>
        void RedrawDiagonals()
        {
            if (Api?.Side != EnumAppSide.Client || Pos == null) return;
            IBlockAccessor ba = Api.World.BlockAccessor;
            foreach (int[] d in StovePipes.Diagonals)
            {
                BlockPos n = Pos.AddCopy(d[0], d[1], d[2]);
                if (StovePipes.IsPipe(ba.GetBlock(n))) ba.MarkBlockDirty(n);
            }
        }

        public void SetCover(IPlayer byPlayer, ItemSlot hand)
        {
            cover = hand.Itemstack.Clone();
            cover.StackSize = 1;
            if (byPlayer?.WorldData?.CurrentGameMode != EnumGameMode.Creative)
            {
                hand.TakeOut(1);
                hand.MarkDirty();
            }
            CoverChanged();
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        }

        public void TakeCover(IPlayer byPlayer)
        {
            if (cover == null) return;
            ItemStack stack = cover;
            cover = null;
            CoverChanged();
            if (byPlayer?.InventoryManager == null || !byPlayer.InventoryManager.TryGiveItemstack(stack, true))
            {
                Api.World.SpawnItemEntity(stack, Pos.ToVec3d().Add(0.5, 0.5, 0.5), null);
            }
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/player/build"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        }

        void CoverChanged()
        {
            coverBoxes = null;
            coverSides = null;
            MarkDirty(true);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);
        }

        /// <summary>The cover's solid parts, as boxes inside the block.</summary>
        public Cuboidf[] CoverBoxes()
        {
            if (cover?.Block == null) return null;
            if (coverBoxes != null) return coverBoxes;
            if (cover.Block is BlockMicroBlock)
            {
                List<Cuboidf> boxes = new List<Cuboidf>();
                foreach (int[] c in PipeCover.Voxels(cover))
                {
                    boxes.Add(new Cuboidf(c[0] / 16f, c[1] / 16f, c[2] / 16f, c[3] / 16f, c[4] / 16f, c[5] / 16f));
                }
                coverBoxes = boxes.ToArray();
            }
            else
            {
                coverBoxes = cover.Block.CollisionBoxes ?? new Cuboidf[] { Cuboidf.Default() };
            }
            return coverBoxes;
        }

        /// <summary>True when the cover fills the whole of that side of the block.</summary>
        public bool CoverSideSolid(int faceIndex)
        {
            if (cover?.Block == null) return false;
            if (coverSides == null)
            {
                coverSides = new bool[6];
                if (cover.Block is BlockMicroBlock)
                {
                    List<int[]> voxels = PipeCover.Voxels(cover);
                    for (int f = 0; f < 6; f++) coverSides[f] = PipeCover.FaceArea(voxels, f) >= 256;
                }
                else
                {
                    for (int f = 0; f < 6; f++) coverSides[f] = cover.Block.SideSolid[f];
                }
            }
            return coverSides[faceIndex];
        }

        /// <summary>The main material a chiselled cover is made of.</summary>
        public Block CoverMaterial()
        {
            if (cover?.Block == null) return null;
            if (!(cover.Block is BlockMicroBlock)) return cover.Block;
            int[] ids = BlockEntityMicroBlock.MaterialIdsFromAttributes(cover.Attributes, Api.World);
            return ids != null && ids.Length > 0 ? Api.World.GetBlock(ids[0]) : null;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            if (cover != null) tree.SetItemstack("pipeCover", cover);
            else tree.RemoveAttribute("pipeCover");
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            ItemStack was = cover;
            cover = tree.GetItemstack("pipeCover", null);
            cover?.ResolveBlockOrItem(worldAccessForResolve);
            if ((was == null) != (cover == null) || (was != null && cover != null && !was.Equals(worldAccessForResolve, cover, new string[0])))
            {
                coverBoxes = null;
                coverSides = null;
                if (Api?.Side == EnumAppSide.Client) Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }
        }

        public override void GetBlockInfo(IPlayer forPlayer, System.Text.StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            if (cover != null) dsc.AppendLine(Lang.Get("forgeandfeast:pipe-covered", cover.GetName()));
            if (Api == null) return;
            IBlockAccessor ba = Api.World.BlockAccessor;
            int diag = StovePipes.DiagonalMask(ba, Pos);
            for (int i = 0; i < StovePipes.Diagonals.Length; i++)
            {
                if ((diag & (1 << i)) == 0) continue;
                int[] d = StovePipes.Diagonals[i];
                string dir = (d[1] > 0 ? Lang.Get("forgeandfeast:dir-up") + " " : d[1] < 0 ? Lang.Get("forgeandfeast:dir-down") + " " : "")
                    + (d[2] < 0 ? Lang.Get("forgeandfeast:dir-north") : d[2] > 0 ? Lang.Get("forgeandfeast:dir-south") : "")
                    + (d[0] > 0 ? Lang.Get("forgeandfeast:dir-east") : d[0] < 0 ? Lang.Get("forgeandfeast:dir-west") : "");
                bool bend = StovePipes.CornerFace(ba, Pos, d) >= 0;
                dsc.AppendLine(Lang.Get(bend ? "forgeandfeast:pipe-corner-bend" : "forgeandfeast:pipe-corner-step", dir.Trim()));
            }
        }

        /// <summary>The cover's model: a chiselled block's carved shape, or the block's usual model.</summary>
        MeshData CoverMesh()
        {
            ICoreClientAPI capi = Api as ICoreClientAPI;
            Block b = cover?.Block;
            if (capi == null || b == null) return null;
            if (b is IContainedMeshSource source)
            {
                DummySlot slot = new DummySlot(cover);
                string key = "pipecover-" + source.GetMeshCacheKey(slot);
                Dictionary<string, MeshData> cache = ObjectCacheUtil.GetOrCreate(Api, "forgeandfeast-pipecovers", () => new Dictionary<string, MeshData>());
                lock (cache)
                {
                    MeshData m;
                    if (!cache.TryGetValue(key, out m))
                    {
                        m = source.GenMesh(slot, capi.BlockTextureAtlas, Pos);
                        cache[key] = m;
                    }
                    return m;
                }
            }
            return capi.TesselatorManager.GetDefaultBlockMesh(b);
        }
        static string Code(int v) { return v < 0 ? "m" : (v > 0 ? "p" : "0"); }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            IBlockAccessor ba = Api.World.BlockAccessor;
            int real = StovePipes.ConnectionMask(ba, Pos);
            int diag = StovePipes.DiagonalMask(ba, Pos);
            int corner = StovePipes.CornerMask(ba, Pos, diag);
            int step = StovePipes.SteppedMask(ba, Pos, diag);
            int mask = BlockStovePipe.VisualMask(real | corner, step);
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
            int roofLayers;
            if (StovePipes.RoofOutlet(ba, Pos, out roofLayers) == null) roofLayers = 0;
            // A real chimney straight above the roof: the pipe goes up through the roof into a brick seat in the top
            // roof block, so the chimney sits down into a sloped roof instead of floating above it
            int seatLayers = 0;
            if (roofLayers == 0)
            {
                BlockPos noOutlet; int chLayers;
                BlockPos ch = StovePipes.RoofExit(ba, Pos, out noOutlet, out chLayers);
                if (ch != null && ch.X == Pos.X && ch.Z == Pos.Z && (mask & (1 << BlockFacing.UP.Index)) == 0) seatLayers = chLayers;
            }
            int topLayer = roofLayers > 0 ? roofLayers : seatLayers;
            int stackBase = topLayer > 0 ? RoofSurface(ba, Pos.UpCopy(topLayer), roofLayers > 0 ? o : new[] { 0, 0 }, roofLayers > 0 ? 4 : 7) : 0;
            key.Append('|').Append(cmask).Append('|').Append(smask).Append(smokerBelow ? "s" : "").Append('|').Append(roofLayers).Append('|').Append(step).Append('|').Append(seatLayers).Append('|').Append(stackBase);

            MeshData mesh = GetMesh(key.ToString(), tesselator, mask, cmask, smask, smokerBelow, o, level, f, roofLayers, step, seatLayers, stackBase);
            if (mesh == null) return false;
            MeshData elbows = ElbowMesh(ba, tesselator, diag);
            if (elbows != null)
            {
                mesh = mesh.Clone();
                mesh.AddMeshData(elbows);
            }
            if (cover != null)
            {
                MeshData coverMesh = CoverMesh();
                if (coverMesh != null) mesher.AddMeshData(coverMesh);
                // A hair bigger, so the pipe's open ends show on the cover's faces instead of flickering
                mesh = mesh.Clone();
                mesh.Scale(new Vec3f(0.5f, 0.5f, 0.5f), 1.004f, 1.004f, 1.004f);
            }
            mesher.AddMeshData(mesh);
            return true;
        }

        /// <summary>
        /// The bend in the open corner block where a diagonal join goes round a corner: in from this pipe's side,
        /// then out to the other pipe's side, each leg lined up with the pipe it meets. Drawn by one of the two
        /// pipes only (the one with the lower position key).
        /// </summary>
        MeshData ElbowMesh(IBlockAccessor ba, ITesselatorAPI tesselator, int diag)
        {
            if (diag == 0) return null;
            MeshData result = null;
            long mine = StovePipes.Key(Pos);
            for (int i = 0; i < StovePipes.Diagonals.Length; i++)
            {
                if ((diag & (1 << i)) == 0) continue;
                int[] d = StovePipes.Diagonals[i];
                int face = StovePipes.CornerFace(ba, Pos, d);
                if (face < 0) continue;
                BlockPos other = Pos.AddCopy(d[0], d[1], d[2]);
                if (StovePipes.Key(other) < mine) continue;

                Vec3i n1 = BlockFacing.ALLFACES[face].Normali;
                int[] e1 = { n1.X, n1.Y, n1.Z };
                int[] e2 = { d[0] - e1[0], d[1] - e1[1], d[2] - e1[2] };
                int a1 = e1[0] != 0 ? 0 : (e1[1] != 0 ? 1 : 2);
                int a2 = e2[0] != 0 ? 0 : (e2[1] != 0 ? 1 : 2);
                int[] a = StovePipes.Middle(ba, Pos), b = StovePipes.Middle(ba, other);

                // In the corner block's own sixteenths
                float[] p = { a[0], a[1], a[2] };
                p[a1] = b[a1];
                float[] lo = new float[3], hi = new float[3];

                // Leg 1: from the face shared with this pipe to the bend, lined up with this pipe
                for (int k = 0; k < 3; k++) { lo[k] = a[k] - 1; hi[k] = a[k] + 1; }
                float entry = e1[a1] > 0 ? 0 : 16;
                lo[a1] = Math.Min(entry, p[a1] - 1); hi[a1] = Math.Max(entry, p[a1] + 1);
                result = AddBar(result, tesselator, lo, hi, e1);

                // Leg 2: from the bend to the face shared with the other pipe, lined up with that pipe
                for (int k = 0; k < 3; k++) { lo[k] = b[k] - 1; hi[k] = b[k] + 1; }
                float exit = e2[a2] > 0 ? 16 : 0;
                lo[a2] = Math.Min(exit, p[a2] - 1); hi[a2] = Math.Max(exit, p[a2] + 1);
                result = AddBar(result, tesselator, lo, hi, e1);

                // The two pipes can sit at different heights (a run tucked up under the eaves meeting one at the
                // middle of the wall) or out from the wall by different amounts: a short upright or sideways piece
                // at the bend joins the two legs
                int a3 = 3 - a1 - a2;
                if (Math.Abs(a[a3] - b[a3]) > 0.01f)
                {
                    for (int k = 0; k < 3; k++) { lo[k] = p[k] - 1; hi[k] = p[k] + 1; }
                    lo[a3] = Math.Min(a[a3], b[a3]) - 1; hi[a3] = Math.Max(a[a3], b[a3]) + 1;
                    result = AddBar(result, tesselator, lo, hi, e1);
                }
            }
            return result;
        }

        /// <summary>A pipe-coloured box from lo to hi (sixteenths of the corner block one step along shift).</summary>
        MeshData AddBar(MeshData into, ITesselatorAPI tesselator, float[] lo, float[] hi, int[] shift)
        {
            MeshData core = CoreMesh(tesselator);
            if (core == null) return into;
            MeshData bar = core.Clone();
            bar.Scale(new Vec3f(0.5f, 0.5f, 0.5f), (hi[0] - lo[0]) / 2f, (hi[1] - lo[1]) / 2f, (hi[2] - lo[2]) / 2f);
            bar.Translate(((lo[0] + hi[0]) / 2f - 8f) / 16f + shift[0], ((lo[1] + hi[1]) / 2f - 8f) / 16f + shift[1], ((lo[2] + hi[2]) / 2f - 8f) / 16f + shift[2]);
            if (into == null) return bar;
            into.AddMeshData(bar);
            return into;
        }

        MeshData CoreMesh(ITesselatorAPI tesselator)
        {
            Dictionary<string, MeshData> cache = ObjectCacheUtil.GetOrCreate(Api, "forgeandfeast-pipecore", () => new Dictionary<string, MeshData>());
            lock (cache)
            {
                MeshData m;
                if (cache.TryGetValue("core", out m)) return m;
                Shape shape = Shape.TryGet(Api, new AssetLocation("forgeandfeast", "shapes/block/stovepipe.json"));
                if (shape == null) return null;
                m = Tess(tesselator, shape, new List<string> { "core" }, 0, 0);
                cache["core"] = m;
                return m;
            }
        }

        /// <summary>
        /// How high the roof's surface is in a roof block, in sixteenths, at its lowest under a square footprint
        /// (half-width hw, centred on the pipe's offset), read from the block's collision boxes. A stack or seat
        /// starts there, so it is buried in the roof on the uphill side and never shows under it.
        /// </summary>
        static int RoofSurface(IBlockAccessor ba, BlockPos at, int[] o, int hw)
        {
            Block b = ba.GetBlock(at);
            Cuboidf[] boxes = null;
            try { boxes = b?.GetCollisionBoxes(ba, at); } catch (Exception) { boxes = null; }
            if (boxes == null || boxes.Length == 0) return 15;
            float cx = 8 + GameMath.Clamp(o[0], -4, 4), cz = 8 + GameMath.Clamp(o[1], -4, 4);
            float lowest = 16;
            for (int ix = -1; ix <= 1; ix++)
            {
                for (int iz = -1; iz <= 1; iz++)
                {
                    float x = (cx + ix * (hw - 0.5f)) / 16f, z = (cz + iz * (hw - 0.5f)) / 16f;
                    float top = 0;
                    foreach (Cuboidf c in boxes)
                    {
                        if (c == null) continue;
                        if (x >= c.X1 && x <= c.X2 && z >= c.Z1 && z <= c.Z2) top = Math.Max(top, c.Y2);
                    }
                    lowest = Math.Min(lowest, top * 16f);
                }
            }
            return GameMath.Clamp((int)Math.Floor(lowest) - 1, 0, 15);
        }

        /// <summary>
        /// The pipe is a metal block, so the game draws it shiny. Brick (the chimney top and seat) shouldn't be:
        /// this takes the shine off.
        /// </summary>
        static MeshData Matte(MeshData m)
        {
            if (m?.Flags == null) return m;
            int n = Math.Min(m.FlagsCount, m.Flags.Length);
            for (int i = 0; i < n; i++) m.Flags[i] &= ~VertexFlags.ReflectiveBitMask;
            return m;
        }

        MeshData Tess(ITesselatorAPI t, Shape shape, List<string> names, float dx, float dz)
        {
            MeshData m;
            t.TesselateShape(Block, shape, out m, null, null, names.ToArray());
            if (dx != 0 || dz != 0) m.Translate(dx / 16f, 0, dz / 16f);
            return m;
        }

        static string Va(int from, int to) { return "va_" + from + "_" + to; }

        MeshData GetMesh(string key, ITesselatorAPI tesselator, int mask, int cmask, int smask, bool smokerBelow, int[] o, int level, int[][] f, int roofLayers, int diag, int seatLayers, int stackBase)
        {
            Dictionary<string, MeshData> cache = ObjectCacheUtil.GetOrCreate(Api, "forgeandfeast-pipemeshes9", () => new Dictionary<string, MeshData>());
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
                // Diagonal joins: a stepped pipe from the middle to the edge it shares with the next pipe
                if (diag != 0)
                {
                    List<string> dg = new List<string>();
                    for (int i = 0; i < 12; i++)
                    {
                        if ((diag & (1 << i)) == 0) continue;
                        for (int k = 1; k <= 8; k++) dg.Add("dg" + i + "_" + k);
                    }
                    mesh.AddMeshData(Tess(tesselator, shape, dg, o[0], o[1]));
                }

                // Through the roof: up out of this block, through each roof block, and out into the open air
                // above with a collar where it leaves the roof and a rain cap on top
                // Through the roof into a brick chimney top: pipe up through the lower roof blocks, a brick stack from
                // the top roof block (so it rises out of a sloped roof) to a rim and flue above it. With a real chimney
                // straight above instead, the top roof block gets a brick seat the chimney sits on.
                int layersUp = roofLayers > 0 ? roofLayers : seatLayers;
                if (layersUp > 0)
                {
                    if (coreTop < 16) vert.Add(Va(coreTop, 16));
                    MeshData through = Tess(tesselator, shape, new List<string> { "va_0_16" }, o[0], o[1]);
                    // The pipe goes on up into the top roof block too, under the stack or seat
                    for (int k = 1; k <= layersUp; k++)
                    {
                        MeshData piece = through.Clone();
                        piece.Translate(0, k, 0);
                        mesh.AddMeshData(piece);
                    }
                    // Kept inside the block, but over to the wall side the pipe hugs
                    float sx = GameMath.Clamp(o[0], -4, 4), sz = GameMath.Clamp(o[1], -4, 4);
                    if (roofLayers > 0)
                    {
                        // Only from the roof's surface up, so it doesn't hang down under a thin or sloped roof
                        MeshData stack = Matte(Tess(tesselator, shape, new List<string> { "stack" }, sx, sz));
                        stack.Scale(new Vec3f(0.5f, 1f, 0.5f), 1f, (16 - stackBase) / 16f, 1f);
                        stack.Translate(0, roofLayers, 0);
                        mesh.AddMeshData(stack);
                        MeshData top = Matte(Tess(tesselator, shape, new List<string> { "stacktop", "stackrim", "stackflue" }, sx, sz));
                        top.Translate(0, roofLayers + 1, 0);
                        mesh.AddMeshData(top);
                    }
                    else
                    {
                        MeshData seat = Matte(Tess(tesselator, shape, new List<string> { "stackseat" }, 0, 0));
                        seat.Scale(new Vec3f(0.5f, 1f, 0.5f), 1f, (16 - stackBase) / 16f, 1f);
                        seat.Translate(0, seatLayers, 0);
                        mesh.AddMeshData(seat);
                    }
                }
                if (vert.Count > 0) mesh.AddMeshData(Tess(tesselator, shape, vert, o[0], o[1]));
                if (vertFixed.Count > 0) mesh.AddMeshData(Tess(tesselator, shape, vertFixed, 0, 0));

                cache[key] = mesh;
                return mesh;
            }
        }
    }

    /// <summary>Which blocks can cover a stovepipe, and reading a chiselled cover's carved shape.</summary>
    public static class PipeCover
    {
        public static bool IsWrench(ItemStack stack)
        {
            string path = stack?.Collectible?.Code?.Path;
            return path != null && path.StartsWith("wrench");
        }

        /// <summary>Chiselled blocks, or any plain block that is solid on every side and has no workings of its own.</summary>
        public static bool CanCover(ItemStack stack)
        {
            Block b = stack?.Block;
            if (b == null || b.Id == 0 || b is BlockStovePipe) return false;
            if (b is BlockMicroBlock) return true;
            if (b.EntityClass != null) return false;
            for (int f = 0; f < 6; f++)
            {
                if (!b.SideSolid[f]) return false;
            }
            return true;
        }

        /// <summary>A chiselled item's carved parts as {x1, y1, z1, x2, y2, z2} in sixteenths.</summary>
        public static List<int[]> Voxels(ItemStack stack)
        {
            List<int[]> list = new List<int[]>();
            uint[] cuboids = BlockEntityMicroBlock.GetVoxelCuboids(stack.Attributes);
            if (cuboids == null) return list;
            foreach (uint u in cuboids)
            {
                int x0, y0, z0, x1, y1, z1, material;
                BlockEntityMicroBlock.FromUint(u, out x0, out y0, out z0, out x1, out y1, out z1, out material);
                if (x1 <= x0) x1 = x0 + 1;
                if (y1 <= y0) y1 = y0 + 1;
                if (z1 <= z0) z1 = z0 + 1;
                list.Add(new[] { Math.Max(0, x0), Math.Max(0, y0), Math.Max(0, z0), Math.Min(16, x1), Math.Min(16, y1), Math.Min(16, z1) });
            }
            return list;
        }

        /// <summary>How many of the 256 voxel squares on one side of the block the carved parts cover.</summary>
        public static int FaceArea(List<int[]> voxels, int faceIndex)
        {
            bool[,] hit = new bool[16, 16];
            BlockFacing face = BlockFacing.ALLFACES[faceIndex];
            foreach (int[] c in voxels)
            {
                // Does this part reach that side?
                bool touches;
                switch (face.Index)
                {
                    case 0: touches = c[2] == 0; break;     // north
                    case 1: touches = c[3] == 16; break;    // east
                    case 2: touches = c[5] == 16; break;    // south
                    case 3: touches = c[0] == 0; break;     // west
                    case 4: touches = c[4] == 16; break;    // up
                    default: touches = c[1] == 0; break;    // down
                }
                if (!touches) continue;
                int a0, a1, b0, b1;
                if (face.Axis == EnumAxis.Z) { a0 = c[0]; a1 = c[3]; b0 = c[1]; b1 = c[4]; }
                else if (face.Axis == EnumAxis.X) { a0 = c[2]; a1 = c[5]; b0 = c[1]; b1 = c[4]; }
                else { a0 = c[0]; a1 = c[3]; b0 = c[2]; b1 = c[5]; }
                for (int a = a0; a < a1; a++) for (int b = b0; b < b1; b++) hit[a, b] = true;
            }
            int n = 0;
            for (int a = 0; a < 16; a++) for (int b = 0; b < 16; b++) if (hit[a, b]) n++;
            return n;
        }
    }
}
