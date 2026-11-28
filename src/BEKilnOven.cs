using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>
    /// Fuel slot plus up to 8 firing slots. Iron and Steel kilns can also smelt: a fired crucible in a
    /// firing slot gets four ingredient slots of its own, hidden unless a crucible is there.
    /// </summary>
    public class InventoryKiln : InventoryBase
    {
        public const int MaxFiringSlots = 8;
        public const int PerCrucible = 4;
        ItemSlot[] slots;
        BurnerSlots[] providers;
        public int ActiveSlots = MaxFiringSlots;
        /// <summary>True on tiers that can smelt metal in crucibles.</summary>
        public bool Smelting;

        public InventoryKiln(string inventoryID, ICoreAPI api) : base(inventoryID, api)
        {
            slots = GenEmptySlots(1 + MaxFiringSlots + MaxFiringSlots * PerCrucible);
            BuildProviders();
            baseWeight = 4f;
        }

        void BuildProviders()
        {
            providers = new BurnerSlots[MaxFiringSlots];
            for (int i = 0; i < MaxFiringSlots; i++)
            {
                int c = CookingSlotId(i, 0);
                providers[i] = new BurnerSlots(this, new ItemSlot[] { slots[c], slots[c + 1], slots[c + 2], slots[c + 3] });
            }
        }

        public static int CookingSlotId(int firing, int j) => 1 + MaxFiringSlots + firing * PerCrucible + j;
        /// <summary>Which firing slot an ingredient slot belongs to, or -1 if it isn't one.</summary>
        public static int FiringOfCook(int slotId) => slotId > MaxFiringSlots ? (slotId - 1 - MaxFiringSlots) / PerCrucible : -1;

        public ItemSlot FuelSlot => slots[0];
        public ItemSlot FiringSlot(int i) => slots[1 + i];
        public BurnerSlots Cook(int i) => providers[i];
        public bool HasCrucible(int i) => FiringSlot(i).Itemstack?.Collectible is BlockSmeltingContainer;

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
            if (id > MaxFiringSlots)
            {
                int f = FiringOfCook(id);
                if (f >= ActiveSlots || !Smelting || !HasCrucible(f)) return false;
                return base.CanContain(sinkSlot, sourceSlot);
            }
            if (id > ActiveSlots) return false;
            if (id >= 1 && sourceSlot?.Itemstack?.Collectible is BlockSmeltingContainer && !Smelting) return false;
            return base.CanContain(sinkSlot, sourceSlot);
        }

        public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
        {
            int id = GetSlotId(targetSlot);
            ItemStack stack = sourceSlot.Itemstack;
            CombustibleProperties props = stack?.Collectible?.CombustibleProps;
            if (id == 0)
            {
                if (props == null || props.BurnTemperature <= 0) return 0;
            }
            else if (id > MaxFiringSlots)
            {
                int f = FiringOfCook(id);
                if (f >= ActiveSlots || !Smelting || !HasCrucible(f)) return 0;
                if (props == null || props.SmeltedStack == null) return 0;
                return 2.2f;
            }
            else
            {
                if (id > ActiveSlots) return 0;
                if (stack?.Collectible is BlockSmeltingContainer) return Smelting ? 2.4f : 0;
                if (props != null && props.SmeltingType == EnumSmeltType.Fire) return 2.2f;
            }
            return base.GetSuitability(sourceSlot, targetSlot, isMerge);
        }

        public override void LateInitialize(string inventoryID, ICoreAPI api)
        {
            base.LateInitialize(inventoryID, api);
            for (int i = 0; i < MaxFiringSlots; i++) UpdateCookingSlots(i);
        }

        public override void DidModifyItemSlot(ItemSlot slot, ItemStack extractedStack = null)
        {
            base.DidModifyItemSlot(slot, extractedStack);
            int id = GetSlotId(slot);
            if (id >= 1 && id <= MaxFiringSlots)
            {
                int i = id - 1;
                if (!HasCrucible(i)) DiscardCookingSlots(i);
                UpdateCookingSlots(i);
            }
        }

        /// <summary>Ingredient slot size follows the crucible, like the vanilla firepit.</summary>
        public void UpdateCookingSlots(int i)
        {
            ItemStack cr = FiringSlot(i).Itemstack;
            int maxStack = cr?.Collectible is BlockSmeltingContainer ? (cr.ItemAttributes?["maxContainerSlotStackSize"].AsInt(32) ?? 32) : 0;
            for (int j = 0; j < PerCrucible; j++) slots[CookingSlotId(i, j)].MaxSlotStackSize = maxStack;
        }

        void DiscardCookingSlots(int i)
        {
            if (Api == null || Api.Side != EnumAppSide.Server || Pos == null) return;
            Vec3d droppos = Pos.ToVec3d().Add(0.5, 1.1, 0.5);
            for (int j = 0; j < PerCrucible; j++)
            {
                ItemSlot s = slots[CookingSlotId(i, j)];
                if (s.Empty) continue;
                Api.World.SpawnItemEntity(s.Itemstack, droppos);
                s.Itemstack = null;
                s.MarkDirty();
            }
        }

        public override void FromTreeAttributes(ITreeAttribute tree)
        {
            SlotTreeHelper.ReadSlots(this, tree, slots);
            BuildProviders();
            if (Api != null) for (int i = 0; i < MaxFiringSlots; i++) UpdateCookingSlots(i);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            SlotsToTreeAttributes(slots, tree);
        }
    }

    /// <summary>
    /// The Kiln Oven fires raw clay - tool molds, ingot molds, pots, bricks and anything else the
    /// game marks as needing a kiln - in its firing slots once it's hot enough.
    /// </summary>
    public class BEKilnOven : BEFuelHeater
    {
        readonly InventoryKiln inventory;
        public int FiringSlots = 4;
        /// <summary>True on the Iron and Steel kilns, which can smelt metal in crucibles.</summary>
        public bool CanSmelt;
        /// <summary>Firing takes this many times the item's own firing duration (before the tier speed bonus).</summary>
        public float DurationMul = 4f;
        /// <summary>In-game hours to fire a pot or mould at minimum heat (Copper 12, Bronze 10, Iron 8, Steel 6).</summary>
        public float FiringHours = 12f;

        readonly float[] progress = new float[InventoryKiln.MaxFiringSlots];
        readonly string[] lastCode = new string[InventoryKiln.MaxFiringSlots];

        public BEKilnOven()
        {
            inventory = new InventoryKiln(null, null);
            inventory.SlotModified += OnSlotModified;
        }

        public override InventoryBase Inventory => inventory;
        public override string InventoryClassName => "kilnoven";

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);
            // Kilns fire pottery only now; metal is melted in the Crucible Furnace.
            // Hand back any crucible (and what's in it) left in a kiln from before.
            if (api.Side == EnumAppSide.Server && !CanSmelt) RegisterDelayedCallback(dt => HandBackCrucibles(), 1000);
        }

        void HandBackCrucibles()
        {
            Vec3d drop = Pos.ToVec3d().Add(0.5, 1.1, 0.5);
            bool changed = false;
            for (int i = 0; i < InventoryKiln.MaxFiringSlots; i++)
            {
                for (int j = 0; j < InventoryKiln.PerCrucible; j++)
                {
                    ItemSlot c = inventory[InventoryKiln.CookingSlotId(i, j)];
                    if (c.Empty) continue;
                    Api.World.SpawnItemEntity(c.Itemstack, drop);
                    c.Itemstack = null;
                    c.MarkDirty();
                    changed = true;
                }
                ItemSlot s = inventory.FiringSlot(i);
                if (s.Itemstack?.Collectible is BlockSmeltingContainer || s.Itemstack?.Collectible is BlockSmeltedContainer)
                {
                    Api.World.SpawnItemEntity(s.Itemstack, drop);
                    s.Itemstack = null;
                    s.MarkDirty();
                    changed = true;
                }
            }
            if (changed) MarkDirty(true);
        }
        public override ItemSlot FuelSlot => inventory.FuelSlot;

        protected override void ReadTierStats()
        {
            base.ReadTierStats();
            FiringSlots = GameMath.Clamp(Block?.Attributes?["firingSlots"].AsInt(4) ?? 4, 1, InventoryKiln.MaxFiringSlots);
            DurationMul = Block?.Attributes?["firingDurationMul"].AsFloat(4f) ?? 4f;
            FiringHours = Block?.Attributes?["firingHours"].AsFloat(12f) ?? 12f;
            inventory.ActiveSlots = FiringSlots;
            CanSmelt = Block?.Attributes?["smelting"].AsBool(false) ?? false;
            inventory.Smelting = CanSmelt;
            inventory.Pos = Pos;
        }

        void OnSlotModified(int slotId)
        {
            if (Api == null) return;
            MarkDirty(Api.Side == EnumAppSide.Server);
        }

        static bool IsFireable(ItemStack stack)
        {
            CombustibleProperties props = stack?.Collectible?.CombustibleProps;
            return props != null
                && props.SmeltingType == EnumSmeltType.Fire
                && props.SmeltedStack?.ResolvedItemstack != null
                && stack.StackSize >= Math.Max(1, props.SmeltedRatio);
        }

        protected override bool HasActiveWork()
        {
            for (int i = 0; i < FiringSlots; i++)
            {
                if (IsFireable(inventory.FiringSlot(i).Itemstack)) return true;
                if (inventory.HasCrucible(i) && CrucibleHasIngredients(i)) return true;
                ItemStack st = inventory.FiringSlot(i).Itemstack;
                if (st?.Collectible is BlockSmeltedContainer && (lastGlow[i] != 0 || GlowLevel(i) != 0)) return true;
            }
            return false;
        }

        protected override void ProcessContents(float dt)
        {
            for (int i = 0; i < FiringSlots; i++)
            {
                ItemSlot slot = inventory.FiringSlot(i);
                ItemStack stack = slot.Itemstack;
                if (stack?.Collectible is BlockSmeltingContainer)
                {
                    ProcessCrucible(i, dt);
                    UpdateGlow(i);
                    continue;
                }
                if (stack?.Collectible is BlockSmeltedContainer)
                {
                    // Molten metal sitting in the kiln stays hot as long as the kiln is
                    float t = stack.Collectible.GetTemperature(Api.World, stack);
                    float nt = ChangeTemperature(t, temperature, dt * 2f);
                    if (Math.Abs(nt - t) > 0.05f) stack.Collectible.SetTemperature(Api.World, stack, nt);
                    UpdateGlow(i);
                    continue;
                }
                if (lastGlow[i] != 0) UpdateGlow(i);
                string code = stack?.Collectible?.Code?.ToString();
                if (code != lastCode[i])
                {
                    progress[i] = 0;
                    lastCode[i] = code;
                }

                if (!IsFireable(stack))
                {
                    progress[i] = 0;
                    continue;
                }

                CombustibleProperties props = stack.Collectible.CombustibleProps;
                if (temperature < props.MeltingPoint)
                {
                    if (progress[i] > 0) progress[i] = Math.Max(0, progress[i] - GameHours(dt));
                    continue;
                }

                // Hotter fuel fires faster, like the pit kiln (charcoal twice as fast as firewood)
                float bonus = GameMath.Clamp(temperature / Math.Max(1, props.MeltingPoint), 1f, 2f);
                progress[i] += GameHours(dt) * VentMul * bonus;

                if (progress[i] >= RequiredTime(props))
                {
                    int ratio = Math.Max(1, props.SmeltedRatio);
                    ItemStack result = props.SmeltedStack.ResolvedItemstack.Clone();
                    result.StackSize *= stack.StackSize / ratio;
                    slot.Itemstack = result;
                    slot.MarkDirty();
                    progress[i] = 0;
                    lastCode[i] = result.Collectible.Code?.ToString();
                    MarkDirty(true);
                }
            }
        }


        void UpdateGlow(int i)
        {
            int g = GlowLevel(i);
            if (g == lastGlow[i]) return;
            lastGlow[i] = g;
            MarkDirty(true);
        }

        readonly int[] lastGlow = new int[InventoryKiln.MaxFiringSlots];

        /// <summary>0-255 glow for the crucible in firing slot i: bright once its metal is molten, dim while it is melting.</summary>
        int GlowLevel(int i)
        {
            ItemStack st = inventory.FiringSlot(i).Itemstack;
            if (st == null) return 0;
            if (st.Collectible is BlockSmeltedContainer)
                return st.Collectible.GetTemperature(Api.World, st) > 600 ? 255 : 0;
            if (st.Collectible is BlockSmeltingContainer && progress[i] > 0) return 140;
            return 0;
        }

        protected override string DisplayKey()
        {
            System.Text.StringBuilder k = new System.Text.StringBuilder();
            for (int i = 0; i < InventoryKiln.MaxFiringSlots; i++)
                k.Append(inventory.FiringSlot(i).Itemstack?.Collectible?.Code).Append(':').Append(GlowLevel(i)).Append(',');
            return k.ToString();
        }

        bool CrucibleHasIngredients(int i)
        {
            ItemSlot[] cs = inventory.Cook(i).Slots;
            for (int j = 0; j < cs.Length; j++) if (!cs[j].Empty) return true;
            return false;
        }

        /// <summary>Heats the crucible's ingredients toward the kiln temperature and smelts them once they are all hot enough.</summary>
        void ProcessCrucible(int i, float dt)
        {
            ItemSlot slot = inventory.FiringSlot(i);
            BurnerSlots prov = inventory.Cook(i);
            ItemStack cr = slot.Itemstack;

            float minTemp = float.MaxValue;
            bool any = false;
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
                progress[i] = 0;
                return;
            }

            float meltingPoint = cr.Collectible.GetMeltingPoint(Api.World, prov, slot);
            float duration = Math.Max(5f, cr.Collectible.GetMeltingDuration(Api.World, prov, slot));
            crucibleDuration[i] = duration;
            crucibleMelt[i] = meltingPoint;

            if (minTemp >= meltingPoint) progress[i] += dt * SpeedMul * VentMul;
            else if (progress[i] > 0) progress[i] = Math.Max(0, progress[i] - dt);

            if (progress[i] >= duration)
            {
                // The game puts the molten metal in a new container; put it back into this same firing slot
                DummySlot result = new DummySlot();
                cr.Collectible.DoSmelt(Api.World, prov, slot, result);
                slot.Itemstack = result.Itemstack;
                slot.MarkDirty();
                for (int j = 0; j < prov.Slots.Length; j++) prov.Slots[j].MarkDirty();
                progress[i] = 0;
                lastCode[i] = slot.Itemstack?.Collectible?.Code?.ToString();
                MarkDirty(true);
            }
        }

        readonly float[] crucibleDuration = new float[InventoryKiln.MaxFiringSlots];
        readonly float[] crucibleMelt = new float[InventoryKiln.MaxFiringSlots];

        /// <summary>In-game hours that pass in dt real seconds, so firing follows the world clock like the pit kiln.</summary>
        float GameHours(float dt)
        {
            IGameCalendar cal = Api?.World?.Calendar;
            if (cal == null) return dt / 120f;
            return dt * cal.SpeedOfTime * cal.CalendarSpeedMul / 3600f;
        }

        /// <summary>
        /// In-game hours to fire one item at its minimum firing heat. The tier sets the hours for a 30 s base-game item
        /// (pots, moulds); slower items like crucibles (45 s) take proportionally longer.
        /// </summary>
        float RequiredTime(CombustibleProperties props)
        {
            return Math.Max(0.25f, FiringHours * Math.Max(1f, props.MeltingDuration) / 30f);
        }

        /// <summary>Rough in-game hours left for slot i at the current heat.</summary>
        float HoursLeft(int i, CombustibleProperties props)
        {
            float bonus = GameMath.Clamp(temperature / Math.Max(1, props.MeltingPoint), 1f, 2f);
            return Math.Max(0, RequiredTime(props) - progress[i]) / Math.Max(0.1f, bonus * VentMul);
        }

        public override string GetTimeLeftText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < FiringSlots; i++)
            {
                string t = GetSlotText(i);
                if (t.Length == 0) continue;
                sb.AppendLine(Lang.Get("forgeandfeast:slot-line", i + 1, t.Replace("\n", " - ")));
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Status line for one firing slot, shown in the GUI.</summary>
        public string GetSlotText(int i)
        {
            ItemStack stack = inventory.FiringSlot(i).Itemstack;
            if (stack == null) return "";
            if (stack.Collectible is BlockSmeltingContainer)
            {
                if (!CrucibleHasIngredients(i)) return Lang.Get("forgeandfeast:kiln-crucible-empty");
                BurnerSlots prov = inventory.Cook(i);
                if (!stack.Collectible.CanSmelt(Api.World, prov, stack, null)) return Lang.Get("forgeandfeast:kiln-crucible-nomatch");
                float mp = stack.Collectible.GetMeltingPoint(Api.World, prov, inventory.FiringSlot(i));
                if (temperature < mp) return Lang.Get("forgeandfeast:kiln-needs", (int)mp);
                float dur = Math.Max(5f, stack.Collectible.GetMeltingDuration(Api.World, prov, inventory.FiringSlot(i)));
                return Lang.Get("forgeandfeast:kiln-smelting", (int)GameMath.Clamp(100 * progress[i] / dur, 0, 100));
            }
            if (stack.Collectible is BlockSmeltedContainer) return Lang.Get("forgeandfeast:kiln-molten");
            if (!IsFireable(stack)) return Lang.Get("forgeandfeast:kiln-done");
            CombustibleProperties props = stack.Collectible.CombustibleProps;
            if (temperature < props.MeltingPoint) return Lang.Get("forgeandfeast:kiln-needs", (int)props.MeltingPoint);
            int pct = (int)GameMath.Clamp(100 * progress[i] / RequiredTime(props), 0, 100);
            if (temperature >= props.MeltingPoint)
                return Lang.Get("forgeandfeast:kiln-firing-hours", pct, Math.Max(1, (int)Math.Ceiling(HoursLeft(i, props))));
            return Lang.Get("forgeandfeast:kiln-firing", pct);
        }

        /// <summary>Draws the moulds and pots in the firing slots on the shelves behind the front opening.</summary>
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            bool drewBody = false;
            if (StovePipes.PipeOnTop(Api.World.BlockAccessor, Pos))
            {
                MeshData body = GetMeshWithoutStub(tesselator);
                if (body != null)
                {
                    mesher.AddMeshData(body);
                    drewBody = true;
                }
            }

            string side = Block?.Variant?["side"] ?? "north";
            float rot = 0;
            switch (side)
            {
                case "east": rot = 270; break;
                case "south": rot = 180; break;
                case "west": rot = 90; break;
            }

            for (int i = 0; i < FiringSlots; i++)
            {
                ItemStack s = inventory.FiringSlot(i).Itemstack;
                if (s == null) continue;

                MeshData mesh = ContentMesh.For(Api, tesselator, s);
                if (mesh == null) continue;

                // Spread evenly: up to 3 on the floor; 4 or 5 split between the floor and the shelf
                int n = FiringSlots;
                int bottom = n <= 3 ? n : (n + 1) / 2;
                int row = i < bottom ? 0 : 1;
                int inRow = row == 0 ? bottom : n - bottom;
                int col = row == 0 ? i : i - bottom;
                float x = (2.5f + 11f * (col + 0.5f) / inRow) / 16f - 0.5f;
                float z = 2.4f / 16f - 0.5f;
                float y = (row == 0 ? 5.4f : 9.0f) / 16f;
                float xr = x, zr = z;
                switch (side)
                {
                    case "east": xr = -z; zr = x; break;
                    case "south": xr = -x; zr = -z; break;
                    case "west": xr = z; zr = -x; break;
                }

                mesh = mesh.Clone();
                int glow = GlowLevel(i);
                if (glow > 0 && mesh.Flags != null && mesh.Rgba != null)
                {
                    // Hot metal: full-bright orange so it reads as molten
                    for (int v = 0; v < mesh.VerticesCount; v++)
                    {
                        mesh.Flags[v] |= glow;
                        mesh.Rgba[v * 4 + 1] = (byte)(mesh.Rgba[v * 4 + 1] * 0.75f);
                        mesh.Rgba[v * 4 + 2] = (byte)(mesh.Rgba[v * 4 + 2] * 0.35f);
                    }
                }
                if (rot != 0) mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, rot * GameMath.DEG2RAD, 0);
                mesh.Scale(new Vec3f(0.5f, 0, 0.5f), 0.3f, 0.3f, 0.3f);
                mesh.Translate(xr, y, zr);
                mesher.AddMeshData(mesh);
            }
            return drewBody;
        }

        public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                toggleInventoryDialogClient(byPlayer, () =>
                {
                    return new GuiDialogKilnOven(Lang.Get("forgeandfeast:kiln-title"), Inventory, Pos, Api as ICoreClientAPI, this);
                });
            }
            return true;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            for (int i = 0; i < InventoryKiln.MaxFiringSlots; i++)
            {
                tree.SetFloat("pkProg" + i, progress[i]);
                tree.SetString("pkCode" + i, lastCode[i] ?? "");
            }
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            for (int i = 0; i < InventoryKiln.MaxFiringSlots; i++)
            {
                progress[i] = tree.GetFloat("pkProg" + i);
                string c = tree.GetString("pkCode" + i, "");
                lastCode[i] = c.Length == 0 ? null : c;
            }
        }
    }
}
