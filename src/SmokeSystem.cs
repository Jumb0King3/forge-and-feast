using System;
using System.Collections.Generic;
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
    /// Runs smoke for every fire with the ForgeSmokeVent behaviour, once a second on the server.
    ///
    /// - Smoke amount: each fire makes smoke by its fuel (wood 1, charcoal and coke very little, peat and
    ///   hay a lot) and by what it's doing (a working Retort or a Smoker full of meat makes more).
    /// - Chimney capacity: one chimney carries so much smoke - more the taller its stack. Fires that share
    ///   an overloaded chimney get less draft, and the smoke it can't carry backs up into the room.
    /// - Weather: wind makes a chimney draw better; rain and still air make it draw worse.
    /// - Rooms: smoke the fires can't get rid of collects under the ceiling of a closed room and builds
    ///   downward. Thatch, hay, leaves, chiselled gaps and doors let some out wherever the smoke reaches
    ///   them, and spare chimney capacity draws some out too. You only choke once the smoke is down to
    ///   your head - crouching buys time - and a fire smoulders once the smoke reaches it.
    /// </summary>
    public class SmokeSystem : ModSystem
    {
        /// <summary>Blocks of room filled per second by one ordinary fire's unvented smoke.</summary>
        public const float SmokePerSecond = 1.5f;
        public const float DamagePerSecond = 0.5f;
        /// <summary>Extra speed a chimney gives at full draft in normal weather.</summary>
        public const float DraftBonus = 0.25f;
        public const float SmokyPenalty = 0.6f;

        public enum Weather { Normal = 0, Windy = 1, Rain = 2, Still = 3 }

        ICoreServerAPI sapi;
        RoomRegistry rooms;
        readonly HashSet<BEBehaviorSmokeVent> fires = new HashSet<BEBehaviorSmokeVent>();
        readonly Dictionary<string, RoomSmoke> roomSmoke = new Dictionary<string, RoomSmoke>();
        readonly Dictionary<long, ChimneyInfo> chimneys = new Dictionary<long, ChimneyInfo>();
        readonly Dictionary<string, long> lastWarn = new Dictionary<string, long>();
        readonly Dictionary<string, long> nextOutdoor = new Dictionary<string, long>();
        bool realSmoke;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            realSmoke = StovePipes.RealSmoke(api);
            api.Event.RegisterGameTickListener(OnTick, 1000, 200);
        }

        public void Add(BEBehaviorSmokeVent fire) { fires.Add(fire); }
        public void Remove(BEBehaviorSmokeVent fire) { fires.Remove(fire); }

        /// <summary>How smoky a fuel burns. Other mods can set "ffFuelSmoke" in an item's attributes.</summary>
        public static float FuelSmoke(CollectibleObject c)
        {
            if (c?.Code == null) return 1f;
            JsonObject own = c.Attributes?["ffFuelSmoke"];
            if (own != null && own.Exists) return GameMath.Clamp(own.AsFloat(1f), 0f, 4f);
            string p = c.Code.Path;
            if (p.Contains("charcoal") || p.Contains("coke")) return 0.15f;
            if (p.Contains("anthracite")) return 0.35f;
            if (p.Contains("lignite") || p.Contains("browncoal")) return 0.8f;
            if (p.Contains("coal")) return 0.5f;
            if (p.Contains("peat") || p.Contains("dung")) return 1.6f;
            if (p.Contains("drygrass") || p.Contains("hay") || p.Contains("straw") || p.Contains("cattail") || p.Contains("papyrus") || p.Contains("reed")) return 1.5f;
            if (p.Contains("stick") || p.Contains("plank")) return 0.9f;
            return 1f;
        }

        static long Key(BlockPos p) => ((long)(p.X & 0xFFFFFF) << 40) | ((long)(p.Y & 0xFFFF) << 24) | (long)(p.Z & 0xFFFFFF);

        static string RoomKey(Room r)
        {
            Cuboidi c = r.Location;
            return c.X1 + "," + c.Y1 + "," + c.Z1 + "," + c.X2 + "," + c.Y2 + "," + c.Z2;
        }

        void OnTick(float dt)
        {
            if (rooms == null) rooms = sapi.ModLoader.GetModSystem<RoomRegistry>();
            IBlockAccessor ba = sapi.World.BlockAccessor;
            long now = sapi.World.ElapsedMilliseconds;

            List<BEBehaviorSmokeVent> list = new List<BEBehaviorSmokeVent>(fires);
            Dictionary<long, List<BEBehaviorSmokeVent>> byChimney = new Dictionary<long, List<BEBehaviorSmokeVent>>();
            Dictionary<string, List<BEBehaviorSmokeVent>> byRoom = new Dictionary<string, List<BEBehaviorSmokeVent>>();

            // 1. Each fire: is it burning, how much smoke, where does it go
            foreach (BEBehaviorSmokeVent f in list)
            {
                if (f.Blockentity == null || f.Api == null || f.Blockentity.Block == null) { fires.Remove(f); continue; }
                try { f.ComputeVent(ba, realSmoke ? null : rooms); }
                catch (Exception e) { f.ResetVent(); sapi.Logger.Debug("[forgeandfeast] smoke check failed at {0}: {1}", f.Pos, e.Message); continue; }

                if (f.VentKind == BEBehaviorSmokeVent.Vent.Chimney && f.ChimneyPos != null)
                {
                    ChimneyInfo ci = Chimney(ba, f.ChimneyPos, now);
                    f.ChimneyKey = ci.Key;
                    List<BEBehaviorSmokeVent> g;
                    if (!byChimney.TryGetValue(ci.Key, out g)) byChimney[ci.Key] = g = new List<BEBehaviorSmokeVent>();
                    g.Add(f);
                }
                if (f.FireRoom != null)
                {
                    string k = RoomKey(f.FireRoom);
                    f.RoomKey = k;
                    List<BEBehaviorSmokeVent> g;
                    if (!byRoom.TryGetValue(k, out g)) byRoom[k] = g = new List<BEBehaviorSmokeVent>();
                    g.Add(f);
                }
            }

            // 2. Chimneys: share each one's capacity between the fires using it
            foreach (KeyValuePair<long, List<BEBehaviorSmokeVent>> kv in byChimney)
            {
                ChimneyInfo ci = chimneys[kv.Key];
                float cap = ci.Capacity * ci.WeatherMul;
                float load = 0, biggest = 0;
                int burning = 0;
                foreach (BEBehaviorSmokeVent f in kv.Value) { load += f.Production; biggest = Math.Max(biggest, f.Production); if (f.Burning) burning++; }
                // Any chimney carries one fire's smoke in full, however short; it's only sharing one between
                // several fires that can overload it
                cap = Math.Max(cap, biggest);
                float ratio = load <= cap || load <= 0 ? 1f : cap / load;
                float spare = Math.Max(0, cap - load) / kv.Value.Count;
                foreach (BEBehaviorSmokeVent f in kv.Value)
                {
                    f.DraftRatio = ratio;
                    f.WeatherMul = ci.WeatherMul;
                    f.WeatherKind = ci.Weather;
                    f.ChimneyShare = Math.Max(1, burning);
                    f.Backed = f.Production * (1 - ratio);
                    f.Spare = spare;
                }
            }

            // 3. Players: the room each one is in counts too (torches, lamps, a forge, a house fire...),
            //    plus whatever lit thing they're holding. Outdoors, a big open fire's smoke can still choke.
            Dictionary<string, Room> roomOf = new Dictionary<string, Room>();
            Dictionary<string, float> held = new Dictionary<string, float>();
            foreach (KeyValuePair<string, List<BEBehaviorSmokeVent>> kv in byRoom) roomOf[kv.Key] = kv.Value[0].FireRoom;
            if (!realSmoke && rooms != null)
            {
                foreach (IPlayer player in sapi.World.AllOnlinePlayers)
                {
                    EntityPlayer e = player.Entity;
                    if (e == null || !e.Alive) continue;
                    Room r = null;
                    try
                    {
                        r = rooms.GetRoomForPosition(new BlockPos((int)Math.Floor(e.Pos.X), (int)Math.Floor(e.Pos.Y + 0.5), (int)Math.Floor(e.Pos.Z)));
                    }
                    catch (Exception) { }
                    if (r != null && r.ExitCount == 0 && r.AnyChunkUnloaded == 0)
                    {
                        string k = RoomKey(r);
                        if (!roomOf.ContainsKey(k)) roomOf[k] = r;
                        float h;
                        held.TryGetValue(k, out h);
                        held[k] = h + HeldSmoke(e);
                    }
                    else
                    {
                        long next;
                        if (!nextOutdoor.TryGetValue(player.PlayerUID, out next) || now >= next)
                        {
                            nextOutdoor[player.PlayerUID] = now + 2000;
                            try { OutdoorSmoke(ba, player, now); } catch (Exception) { }
                        }
                    }
                }
            }

            // 4. Rooms: smoke builds from the ceiling down
            foreach (KeyValuePair<string, Room> rk in roomOf)
            {
                List<BEBehaviorSmokeVent> members;
                if (!byRoom.TryGetValue(rk.Key, out members)) members = new List<BEBehaviorSmokeVent>();
                Room room = rk.Value;
                RoomSmoke rs;
                if (!roomSmoke.TryGetValue(rk.Key, out rs))
                {
                    rs = new RoomSmoke();
                    foreach (BEBehaviorSmokeVent f in members) rs.Volume = Math.Max(rs.Volume, f.RoomVolume);
                    roomSmoke[rk.Key] = rs;
                }
                if (rs.ForRoom != room || now >= rs.NextBuild) rs.Build(ba, room, now);
                rs.Touched = now;

                float inflow = 0, drawn = 0;
                bool anyBurning = false;
                foreach (BEBehaviorSmokeVent f in members)
                {
                    inflow += f.Unvented;
                    if (f.VentKind == BEBehaviorSmokeVent.Vent.Chimney) drawn += f.Spare;
                    anyBurning |= f.Burning;
                }
                float heldSmoke;
                held.TryGetValue(rk.Key, out heldSmoke);
                inflow += rs.SourceSmoke + heldSmoke;
                anyBurning |= rs.SourceSmoke + heldSmoke > 0;
                inflow *= SmokePerSecond;
                drawn *= SmokePerSecond;
                float passive = 0.2f + 0.004f * rs.Volume;
                float leak = rs.CoveredLeak();
                rs.Volume = GameMath.Clamp(rs.Volume + (inflow - drawn - leak - passive) * dt, 0, rs.Total);
                if (!anyBurning && rs.Volume < 0.5f) rs.Volume = 0;
                rs.UpdateBottom();

                if (rs.Volume >= 1f) Choke(rs, room, members.Count > 0 ? members[0] : null, now);

                if (members.Count > 0)
                {
                    BEBehaviorSmokeVent lead = members[0];
                    foreach (BEBehaviorSmokeVent f in members) if (f.Burning && !lead.Burning) lead = f;
                    foreach (BEBehaviorSmokeVent f in members) f.ApplyRoom(rs, f == lead);
                }
                else if (rs.Volume >= 0.5f) ServerHaze(rs);
            }

            // 5. Finish: speed multipliers, and tell clients what changed
            foreach (BEBehaviorSmokeVent f in list)
            {
                if (f.Blockentity == null || f.Api == null) continue;
                if (f.FireRoom == null) f.ApplyNoRoom(dt);
                f.FinishTick();
            }

            // Forget rooms and chimneys nothing has used for a while
            List<string> oldRooms = null;
            foreach (KeyValuePair<string, RoomSmoke> kv in roomSmoke) if (now - kv.Value.Touched > 30000) (oldRooms ?? (oldRooms = new List<string>())).Add(kv.Key);
            if (oldRooms != null) foreach (string k in oldRooms) roomSmoke.Remove(k);
            List<long> oldChimneys = null;
            foreach (KeyValuePair<long, ChimneyInfo> kv in chimneys) if (now - kv.Value.Used > 60000) (oldChimneys ?? (oldChimneys = new List<long>())).Add(kv.Key);
            if (oldChimneys != null) foreach (long k in oldChimneys) chimneys.Remove(k);
        }

        /// <summary>A chimney stack: found from its bottom block, capacity by height, draft by weather.</summary>
        ChimneyInfo Chimney(IBlockAccessor ba, BlockPos at, long now)
        {
            BlockPos bottom = at.Copy();
            for (int i = 0; i < 16 && StovePipes.IsChimney(ba.GetBlock(bottom.DownCopy(1))); i++) bottom.Down(1);
            long key = Key(bottom);
            ChimneyInfo ci;
            if (chimneys.TryGetValue(key, out ci) && now < ci.NextCheck) { ci.Used = now; return ci; }
            if (ci == null) chimneys[key] = ci = new ChimneyInfo();
            ci.Key = key;
            ci.Used = now;
            ci.NextCheck = now + 10000;

            int height = 1;
            BlockPos top = bottom.Copy();
            while (height < 8 && StovePipes.IsChimney(ba.GetBlock(top.UpCopy()))) { top.Up(); height++; }
            ci.Height = height;
            ci.Capacity = Math.Min(5f, 1.5f + 0.5f * (height - 1));

            // Weather at the top of the stack
            float mul = 1f;
            Weather kind = Weather.Normal;
            try
            {
                BlockPos air = top.UpCopy();
                double wind = ba.GetWindSpeedAt(air).Length();
                ClimateCondition climate = ba.GetClimateAt(air, EnumGetClimateMode.NowValues, sapi.World.Calendar.TotalDays);
                float rain = climate?.Rainfall ?? 0;
                if (rain > 0.25f) { mul = 1f - 0.25f * Math.Min(1f, rain); kind = Weather.Rain; }
                else if (wind > 0.4) { mul = 1f + 0.4f * (float)Math.Min(1.0, wind); kind = Weather.Windy; }
                else if (wind < 0.1) { mul = 0.9f; kind = Weather.Still; }
            }
            catch (Exception) { }
            ci.WeatherMul = mul;
            ci.Weather = kind;
            return ci;
        }

        void Choke(RoomSmoke rs, Room room, BEBehaviorSmokeVent source, long now)
        {
            BlockPos p = new BlockPos(0);
            double cx = (rs.Loc.X1 + rs.Loc.X2) / 2.0 + 0.5, cy = (rs.Loc.Y1 + rs.Loc.Y2) / 2.0, cz = (rs.Loc.Z1 + rs.Loc.Z2) / 2.0 + 0.5;
            foreach (IPlayer player in sapi.World.AllOnlinePlayers)
            {
                EntityPlayer entity = player.Entity;
                if (entity == null || !entity.Alive) continue;
                EnumGameMode mode = player.WorldData?.CurrentGameMode ?? EnumGameMode.Survival;
                if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) continue;
                if (Math.Abs(entity.Pos.X - cx) > 20 || Math.Abs(entity.Pos.Y - cy) > 20 || Math.Abs(entity.Pos.Z - cz) > 20) continue;

                p.Set((int)Math.Floor(entity.Pos.X), (int)Math.Floor(entity.Pos.Y), (int)Math.Floor(entity.Pos.Z));
                p.dimension = entity.Pos.Dimension;
                bool inside = room.Contains(p);
                if (!inside) { p.Up(); inside = room.Contains(p); }
                if (!inside) continue;

                double eye = entity.LocalEyePos?.Y ?? 1.6;
                bool low = entity.Controls.Sneak || entity.Controls.FloorSitting || entity.ServerControls.Sneak;
                if (low) eye = Math.Min(eye, 1.0);
                if (entity.Pos.Y + eye < rs.Bottom) continue;

                entity.ReceiveDamage(new DamageSource()
                {
                    Source = source != null ? EnumDamageSource.Block : EnumDamageSource.Internal,
                    SourceBlock = source?.Blockentity?.Block,
                    SourcePos = source != null ? source.Pos.ToVec3d() : entity.Pos.XYZ,
                    Type = EnumDamageType.Suffocation
                }, DamagePerSecond);

                IServerPlayer sp = player as IServerPlayer;
                long last;
                if (sp != null && (!lastWarn.TryGetValue(sp.PlayerUID, out last) || now - last > 4000))
                {
                    lastWarn[sp.PlayerUID] = now;
                    sp.SendIngameError("forgeandfeast-smoke", Lang.Get("forgeandfeast:smoke-choking"));
                }
            }
        }

        /// <summary>Lit things a player is holding: a torch smokes, a lamp or candle barely.</summary>
        static float HeldSmoke(EntityPlayer e)
        {
            float sum = 0;
            ItemSlot[] slots = { e.RightHandItemSlot, e.LeftHandItemSlot };
            foreach (ItemSlot slot in slots)
            {
                string path = slot?.Itemstack?.Collectible?.Code?.Path;
                if (path == null) continue;
                if (path.Contains("torch") && path.Contains("-lit")) sum += 0.05f;
                else if (path.Contains("oillamp") || path.Contains("lantern")) sum += 0.015f;
                else if (path.Contains("candle")) sum += 0.01f;
            }
            return sum;
        }

        /// <summary>
        /// Smoke from an open fire (lightning, a burning house or forest): heavy close up, mostly rising,
        /// blown downwind. Enough of it nearby chokes you even outdoors; crouching or getting upwind helps.
        /// </summary>
        void OutdoorSmoke(IBlockAccessor ba, IPlayer player, long now)
        {
            EntityPlayer e = player.Entity;
            EnumGameMode mode = player.WorldData?.CurrentGameMode ?? EnumGameMode.Survival;
            if (mode == EnumGameMode.Creative || mode == EnumGameMode.Spectator) return;

            int fx = (int)Math.Floor(e.Pos.X), fy = (int)Math.Floor(e.Pos.Y), fz = (int)Math.Floor(e.Pos.Z);
            bool low = e.Controls.Sneak || e.Controls.FloorSitting || e.ServerControls.Sneak;
            double eyeY = e.Pos.Y + (low ? Math.Min(e.LocalEyePos?.Y ?? 1.6, 1.0) : (e.LocalEyePos?.Y ?? 1.6));
            BlockPos n = new BlockPos(fx, fy, fz);
            Vec3d wind = ba.GetWindSpeedAt(n);
            double windLen = wind == null ? 0 : wind.HorLength();

            float density = 0;
            for (int dx = -5; dx <= 5; dx++)
            for (int dz = -5; dz <= 5; dz++)
            for (int dy = -4; dy <= 2; dy++)
            {
                n.Set(fx + dx, fy + dy, fz + dz);
                Block b = ba.GetBlock(n);
                if (b == null || b.Id == 0 || b.Code == null) continue;
                string path = b.Code.Path;
                float w = path == "fire" ? 1f : (path.StartsWith("ember") ? 0.3f : 0f);
                if (w == 0) continue;
                double wgt = w / (1 + (dx * dx + dz * dz) / 3.0);
                // Smoke goes up: a fire above your head mostly misses you
                if (n.Y > eyeY) wgt *= 0.3;
                // Downwind of the fire you get it all; upwind it blows away from you
                if (windLen > 0.2 && (dx != 0 || dz != 0))
                {
                    double cos = (-dx * wind.X - dz * wind.Z) / (Math.Sqrt(dx * dx + dz * dz) * windLen);
                    wgt *= 1 + 0.6 * cos;
                }
                density += (float)wgt;
            }
            if (low) density *= 0.6f;
            if (density < 3f) return;

            e.ReceiveDamage(new DamageSource()
            {
                Source = EnumDamageSource.Internal,
                SourcePos = e.Pos.XYZ,
                Type = EnumDamageType.Suffocation
            }, DamagePerSecond * 2 * Math.Min(2f, density / 3f));

            IServerPlayer sp = player as IServerPlayer;
            long last;
            if (sp != null && (!lastWarn.TryGetValue(sp.PlayerUID, out last) || now - last > 4000))
            {
                lastWarn[sp.PlayerUID] = now;
                sp.SendIngameError("forgeandfeast-smoke", Lang.Get("forgeandfeast:smoke-fire"));
            }
        }

        /// <summary>Haze for a room with no stove of ours in it (torches, a forge, a house fire), sent from the server.</summary>
        void ServerHaze(RoomSmoke rs)
        {
            float top = rs.MinY + rs.Layer.Length;
            if (top <= rs.Bottom) return;
            int area = (rs.Loc.X2 - rs.Loc.X1 + 1) * (rs.Loc.Z2 - rs.Loc.Z1 + 1);
            float amount = GameMath.Clamp((top - rs.Bottom) * area / 6f, 2f, 30f);
            float frac = rs.Total > 0 ? rs.Volume / rs.Total : 0;
            int alpha = (int)GameMath.Clamp(70 + 120 * frac, 70, 190);
            SimpleParticleProperties props = new SimpleParticleProperties(
                amount, amount * 2,
                ColorUtil.ColorFromRgba(110, 110, 110, alpha),
                new Vec3d(rs.Loc.X1, rs.Bottom, rs.Loc.Z1), new Vec3d(rs.Loc.X2 + 1, top - 0.2, rs.Loc.Z2 + 1),
                new Vec3f(-0.06f, -0.015f, -0.06f), new Vec3f(0.06f, 0.015f, 0.06f),
                6f, 0f, 1.5f, 3f, EnumParticleModel.Quad);
            props.SelfPropelled = true;
            props.WindAffected = false;
            sapi.World.SpawnParticles(props, null);
        }

        public class ChimneyInfo
        {
            public long Key;
            public int Height;
            public float Capacity;
            public float WeatherMul = 1f;
            public Weather Weather;
            public long NextCheck, Used;
        }

        /// <summary>One closed room's smoke: how much there is and the room's shape layer by layer.</summary>
        public class RoomSmoke
        {
            public Room ForRoom;
            public Cuboidi Loc;
            public float Volume;
            public int Total;
            public int MinY;
            public int[] Layer;
            public float[] Leak;
            public float LeakTotal;
            /// <summary>Smoke from lit things in the room that aren't our stoves (torches, a forge, fire...), in fires' worth.</summary>
            public float SourceSmoke;
            public int FireBlocks;
            public float Bottom;
            public long NextBuild, Touched;

            public void Build(IBlockAccessor ba, Room room, long now)
            {
                ForRoom = room;
                Loc = room.Location;
                SourceSmoke = 0;
                FireBlocks = 0;
                MinY = Loc.Y1;
                int h = Loc.Y2 - Loc.Y1 + 1;
                Layer = new int[h];
                Leak = new float[h];
                Total = 0;
                LeakTotal = 0;
                BlockPos p = new BlockPos(0), n = new BlockPos(0);
                for (int x = Loc.X1; x <= Loc.X2; x++)
                for (int y = Loc.Y1; y <= Loc.Y2; y++)
                for (int z = Loc.Z1; z <= Loc.Z2; z++)
                {
                    p.Set(x, y, z);
                    if (!room.Contains(p)) continue;
                    Layer[y - MinY]++;
                    Total++;
                    Block here = ba.GetBlock(p);
                    if (here != null && here.Id != 0)
                    {
                        float smoke = SourceSmokeOf(ba, here, p);
                        if (smoke > 0)
                        {
                            SourceSmoke += smoke;
                            if (here.Code?.Path == "fire") FireBlocks++;
                        }
                    }
                    for (int i = 0; i < 6; i++)
                    {
                        BlockFacing f = BlockFacing.ALLFACES[i];
                        n.Set(x + f.Normali.X, y + f.Normali.Y, z + f.Normali.Z);
                        if (n.X >= Loc.X1 && n.X <= Loc.X2 && n.Y >= Loc.Y1 && n.Y <= Loc.Y2 && n.Z >= Loc.Z1 && n.Z <= Loc.Z2 && room.Contains(n)) continue;
                        float leak = Porosity(ba, ba.GetBlock(n), n, f.Opposite);
                        Leak[y - MinY] += leak;
                        LeakTotal += leak;
                    }
                }
                Volume = Math.Min(Volume, Total);
                // A fire spreads fast, so look again sooner
                NextBuild = now + (FireBlocks > 0 ? 4000 : 15000);
                UpdateBottom();
            }

            /// <summary>
            /// How much smoke a lit thing makes, in fires' worth. Our stoves are counted separately.
            /// </summary>
            static float SourceSmokeOf(IBlockAccessor ba, Block b, BlockPos pos)
            {
                if (b.EntityClass != null)
                {
                    BlockEntity be = ba.GetBlockEntity(pos);
                    if (be != null)
                    {
                        if (be.GetBehavior<BEBehaviorSmokeVent>() != null) return 0;
                        float s = BlockEntitySmoke(be);
                        if (s >= 0) return s > 0 && ChimneyAbove(ba, pos) ? 0 : s;
                    }
                }

                byte[] hsv = b.GetLightHsv(ba, pos, null);
                if (hsv == null || hsv.Length < 3 || hsv[2] == 0) return 0;
                string path = b.Code?.Path;
                if (path == null) return 0;
                if (path == "fire") return 1.2f;
                if (path.StartsWith("ember")) return 0.3f;
                if (path.Contains("jonas") || path.Contains("lava") || path.Contains("glow")) return 0;
                // Lit torches and filled holders give light; burnt-out and empty ones don't
                if (path.Contains("torch")) return 0.05f;
                if (path.Contains("candle") || path.Contains("chandelier")) return 0.01f;
                if (path.Contains("oillamp") || path.Contains("lantern") || path.Contains("lamp")) return 0.015f;
                if (path.Contains("brazier") || path.Contains("campfire") || path.Contains("fireplace") || path.Contains("bonfire")) return ChimneyAbove(ba, pos) ? 0 : 0.8f;
                // Other mods' lit fires
                foreach (string part in path.Split('-')) if (part == "lit" || part == "burning") return ChimneyAbove(ba, pos) ? 0 : 0.3f;
                return 0;
            }

            /// <summary>Base game fires with a block entity. -1 when it isn't one of them.</summary>
            static float BlockEntitySmoke(BlockEntity be)
            {
                if (be is BlockEntityPitKiln kiln) return kiln.Lit ? 2f : 0f;
                if (be is BlockEntityCoalPile coal) return coal.IsBurning ? 0.8f : 0f;
                if (be is BlockEntityGroundStorage storage) return storage.IsBurning ? 1f : 0f;
                if (be is BlockEntityForge forge) return forge.IsBurning ? 0.4f : 0f;
                if (be is BlockEntityBloomery bloomery) return bloomery.IsBurning ? 1.5f : 0f;
                if (be is BlockEntityCharcoalPit pit) return pit.Lit ? 1.5f : 0f;
                return -1;
            }

            /// <summary>A chimney straight above within 8 blocks, with nothing solid in between.</summary>
            static bool ChimneyAbove(IBlockAccessor ba, BlockPos pos)
            {
                BlockPos t = pos.Copy();
                for (int i = 1; i <= 8; i++)
                {
                    t.Set(pos.X, pos.Y + i, pos.Z);
                    Block b = ba.GetBlock(t);
                    if (b == null || b.Id == 0) continue;
                    if (StovePipes.IsChimney(b)) return true;
                    if (b.SideIsSolid(t, BlockFacing.DOWN.Index) || b.SideIsSolid(t, BlockFacing.UP.Index)) return false;
                }
                return false;
            }

            /// <summary>Blocks the room check counts as walls that still let a little smoke through.</summary>
            static float Porosity(IBlockAccessor ba, Block b, BlockPos pos, BlockFacing towardRoom)
            {
                if (b == null || b.Id == 0 || b.Code == null) return 0;
                string path = b.Code.Path;
                if (path.Contains("thatch") || path.Contains("hay") || path.Contains("straw") || path.Contains("reed") || path.Contains("leaves") || path.Contains("wattle")) return 0.08f;
                if (b is BlockMicroBlock && !b.SideIsSolid(ba, pos, towardRoom.Index)) return 0.1f;
                if (path.Contains("door") || path.Contains("hatch")) return 0.03f;
                return 0;
            }

            /// <summary>Leaks only let smoke out where the smoke has reached them.</summary>
            public float CoveredLeak()
            {
                if (Leak == null || Volume <= 0) return 0;
                float sum = 0;
                for (int i = 0; i < Leak.Length; i++)
                {
                    float covered = GameMath.Clamp(MinY + i + 1 - Bottom, 0, 1);
                    sum += Leak[i] * covered;
                }
                return sum;
            }

            /// <summary>Fills the room from the top layer down; Bottom is the Y the smoke has reached.</summary>
            public void UpdateBottom()
            {
                if (Layer == null) { Bottom = float.MaxValue; return; }
                float rem = Volume;
                float bottom = MinY + Layer.Length;
                for (int i = Layer.Length - 1; i >= 0 && rem > 0; i--)
                {
                    int c = Layer[i];
                    if (c == 0) continue;
                    if (rem >= c) { rem -= c; bottom = MinY + i; }
                    else { bottom = MinY + i + 1 - rem / c; rem = 0; }
                }
                Bottom = bottom;
            }
        }
    }
}
