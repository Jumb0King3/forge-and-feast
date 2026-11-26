using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>
    /// What the smoker turns things into. Works by item code, so Forge and Feast needs no other mod to load:
    /// - anything ending in "-raw" becomes the same code ending in "-smoked" if that item exists
    ///   (Rustic Cookery's meat slabs, and the smoked red meat and bushmeat it adds to the base game);
    /// - otherwise the "-cured" version if that exists (the base game's cured meat: smoking cures it);
    /// - a whole base game fish becomes Rustic Cookery's smoked fish when that mod is installed.
    /// </summary>
    public static class Smoking
    {
        static readonly Dictionary<string, AssetLocation> cache = new Dictionary<string, AssetLocation>();

        public static AssetLocation ResultCode(IWorldAccessor world, ItemStack stack)
        {
            if (world == null || stack?.Item == null || stack.Collectible.Code == null) return null;
            string key = stack.Collectible.Code.ToString();
            lock (cache)
            {
                AssetLocation hit;
                if (cache.TryGetValue(key, out hit)) return hit;
                hit = Find(world, stack.Collectible.Code);
                cache[key] = hit;
                return hit;
            }
        }

        /// <summary>
        /// Raw meats from other mods that have no smoked or cured version of their own. They smoke into the
        /// base game's cured meat of the same kind, one for one. Only used when the mod has no "-smoked"
        /// version (Forge and Feast adds one for Feverstone Wilds when that mod is installed).
        /// </summary>
        static readonly Dictionary<string, string> Equivalents = new Dictionary<string, string>
        {
            { "feverstonewilds:bisonleg-raw", "game:redmeat-cured" },
            { "feverstonewilds:ribs-raw", "game:redmeat-cured" },
            { "feverstonewilds:cockatrice-tail-raw", "game:poultry-cured" },
        };

        static AssetLocation Find(IWorldAccessor world, AssetLocation code)
        {
            string path = code.Path;
            if (path.EndsWith("-raw"))
            {
                string stem = path.Substring(0, path.Length - 4);
                AssetLocation smoked = new AssetLocation(code.Domain, stem + "-smoked");
                if (world.GetItem(smoked) != null) return smoked;
                AssetLocation cured = new AssetLocation(code.Domain, stem + "-cured");
                if (world.GetItem(cured) != null) return cured;

                string mapped;
                if (Equivalents.TryGetValue(code.ToString(), out mapped))
                {
                    AssetLocation target = new AssetLocation(mapped);
                    return world.GetItem(target) != null ? target : null;
                }
                return null;
            }
            // Whole fish from the base game: fish-{habitat}-{type}-{species}-{age}
            if (code.Domain == "game" && path.StartsWith("fish-") && path.Split('-').Length >= 4)
            {
                AssetLocation smokedFish = new AssetLocation("rusticcookery", "smoked" + path);
                if (world.GetItem(smokedFish) != null) return smokedFish;
            }
            return null;
        }

        public static bool CanSmoke(IWorldAccessor world, ItemStack stack) => ResultCode(world, stack) != null;

        /// <summary>Forgets earlier lookups, e.g. when a world with a different set of mods loads.</summary>
        public static void ClearCache()
        {
            lock (cache) cache.Clear();
        }

        /// <summary>The smoked stack. Keeps the item's own attributes (fish size and so on) but starts it fresh.</summary>
        public static ItemStack Smoke(IWorldAccessor world, ItemStack stack)
        {
            AssetLocation code = ResultCode(world, stack);
            Item item = code == null ? null : world.GetItem(code);
            if (item == null) return null;
            ItemStack result = new ItemStack(item, stack.StackSize);
            if (stack.Attributes != null)
            {
                result.Attributes = stack.Attributes.Clone();
                result.Attributes.RemoveAttribute("transitionstate");
                result.Attributes.RemoveAttribute("temperature");
            }
            return result;
        }
    }

    /// <summary>A rail hook: holds one piece of meat or fish that the smoker can smoke.</summary>
    public class ItemSlotSmokeHook : ItemSlotSurvival
    {
        public ItemSlotSmokeHook(InventoryBase inventory) : base(inventory)
        {
            MaxSlotStackSize = 1;
        }

        public override bool CanHold(ItemSlot sourceSlot)
        {
            return base.CanHold(sourceSlot) && Smoking.CanSmoke(inventory?.Api?.World, sourceSlot?.Itemstack);
        }

        public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
        {
            return base.CanTakeFrom(sourceSlot, priority) && Smoking.CanSmoke(inventory?.Api?.World, sourceSlot?.Itemstack);
        }
    }

    /// <summary>Fuel slot plus up to 10 hooks. Meat keeps a little longer while it hangs in the smoke.</summary>
    public class InventorySmoker : InventoryBase
    {
        public const int MaxHooks = 10;
        ItemSlot[] slots;

        public InventorySmoker(string inventoryID, ICoreAPI api) : base(inventoryID, api)
        {
            slots = GenEmptySlots(1 + MaxHooks);
            baseWeight = 4f;
        }

        public int ActiveHooks = 4;

        public ItemSlot FuelSlot => slots[0];
        public ItemSlot Hook(int i) => slots[1 + i];

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
            if (i == 0) return new ItemSlotSurvival(this);
            return new ItemSlotSmokeHook(this);
        }

        public override bool CanContain(ItemSlot sinkSlot, ItemSlot sourceSlot)
        {
            int id = GetSlotId(sinkSlot);
            if (id >= 1 + ActiveHooks) return false;
            return base.CanContain(sinkSlot, sourceSlot);
        }

        public override float GetSuitability(ItemSlot sourceSlot, ItemSlot targetSlot, bool isMerge)
        {
            int id = GetSlotId(targetSlot);
            ItemStack stack = sourceSlot.Itemstack;
            if (id >= 1 + ActiveHooks) return 0;
            if (id >= 1) return Smoking.CanSmoke(Api?.World, stack) ? 2.2f : 0;
            CombustibleProperties props = stack?.Collectible?.CombustibleProps;
            if (props == null || props.BurnTemperature <= 0) return 0;
            return base.GetSuitability(sourceSlot, targetSlot, isMerge);
        }

        public override float GetTransitionSpeedMul(EnumTransitionType transType, ItemStack stack)
        {
            float mul = base.GetTransitionSpeedMul(transType, stack);
            if (transType == EnumTransitionType.Perish) mul *= 0.5f;
            return mul;
        }

        public override void FromTreeAttributes(ITreeAttribute tree)
        {
            SlotTreeHelper.ReadSlots(this, tree, slots);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            SlotsToTreeAttributes(slots, tree);
        }
    }

    /// <summary>
    /// The Smoker: a 2-block farm smokehouse. A small iron firebox keeps the chamber at 60-120 C and
    /// fills it with smoke; meat and fish hanging on the rails are smoked on the in-game clock
    /// (about 24 in-game hours on Copper, faster on higher tiers). Wood gives full smoke; charcoal and
    /// coal burn hot but clean, so they smoke at a quarter of the speed.
    /// </summary>
    public class BESmoker : BEFuelHeater
    {
        public const float MinTemp = 60f;
        /// <summary>In-game hours to smoke a piece on a Copper smoker with wood. Rustic Cookery's default is 2880 s (24 h).</summary>
        public const float BaseHours = 24f;
        /// <summary>Fuels burning at or above this temperature (charcoal, coal, coke) give little smoke.</summary>
        public const int CleanFuelTemp = 1000;

        readonly InventorySmoker inventory;
        readonly float[] progress = new float[InventorySmoker.MaxHooks];

        public BESmoker()
        {
            inventory = new InventorySmoker(null, null);
            inventory.SlotModified += id => { if (Api != null) MarkDirty(Api.Side == EnumAppSide.Server); };
        }

        public override InventoryBase Inventory => inventory;
        public override string InventoryClassName => "smoker";
        public override ItemSlot FuelSlot => inventory.FuelSlot;

        public int Hooks => inventory.ActiveHooks;

        /// <summary>The wisp comes out of the vent on the roof ridge.</summary>
        public override double StubTop => 2.0;

        protected override void ReadTierStats()
        {
            base.ReadTierStats();
            inventory.ActiveHooks = GameMath.Clamp(Block?.Attributes?["racks"].AsInt(4) ?? 4, 1, InventorySmoker.MaxHooks);
        }

        bool CleanFuel => fuelTemperature >= CleanFuelTemp;

        bool Working => IsBurning && temperature >= MinTemp;

        public override float CurrentSmokeMul => Working && AnyHanging() ? SmokeMul * 1.5f : SmokeMul;

        bool AnyHanging()
        {
            for (int i = 0; i < Hooks; i++) if (!inventory.Hook(i).Empty) return true;
            return false;
        }

        protected override bool HasActiveWork() => AnyHanging();

        protected override void ProcessContents(float dt)
        {
            float add = Working ? GameHours(dt) * SpeedMul * VentMul * (CleanFuel ? 0.25f : 1f) : 0f;
            for (int i = 0; i < InventorySmoker.MaxHooks; i++)
            {
                ItemSlot slot = inventory.Hook(i);
                if (i >= Hooks || slot.Empty || !Smoking.CanSmoke(Api.World, slot.Itemstack))
                {
                    progress[i] = 0;
                    continue;
                }
                if (add <= 0) continue;   // smoking pauses while the fire is out, it doesn't reset

                progress[i] += add;
                if (progress[i] < BaseHours) continue;

                ItemStack smoked = Smoking.Smoke(Api.World, slot.Itemstack);
                if (smoked != null)
                {
                    slot.Itemstack = smoked;
                    slot.MarkDirty();
                    MarkDirty(true);
                }
                progress[i] = 0;
            }
        }

        /// <summary>In-game hours that pass in dt real seconds.</summary>
        float GameHours(float dt)
        {
            IGameCalendar cal = Api?.World?.Calendar;
            if (cal == null) return dt / 120f;
            return dt * cal.SpeedOfTime * cal.CalendarSpeedMul / 3600f;
        }

        /// <summary>Real-time hours left for the slowest piece, for the status line.</summary>
        float HoursLeft(out int pct)
        {
            float worst = 0; float lowest = BaseHours;
            for (int i = 0; i < Hooks; i++)
            {
                ItemSlot slot = inventory.Hook(i);
                if (slot.Empty || !Smoking.CanSmoke(Api.World, slot.Itemstack)) continue;
                lowest = Math.Min(lowest, progress[i]);
                worst = Math.Max(worst, BaseHours - progress[i]);
            }
            pct = (int)GameMath.Clamp(100 * lowest / BaseHours, 0, 100);
            float rate = SpeedMul * VentMul * (CleanFuel ? 0.25f : 1f);
            return rate <= 0 ? worst : worst / rate;
        }

        public string GetStatusText()
        {
            if (!AnyHanging()) return Lang.Get("forgeandfeast:smoker-empty");
            if (!IsBurning) return Lang.Get("forgeandfeast:smoker-nofire");
            if (temperature < MinTemp) return Lang.Get("forgeandfeast:smoker-heating", (int)MinTemp);
            int pct;
            float hours = HoursLeft(out pct);
            string text = Lang.Get("forgeandfeast:smoker-working", pct, Math.Max(1, (int)Math.Ceiling(hours)));
            if (CleanFuel) text += "\n" + Lang.Get("forgeandfeast:smoker-cleanfuel");
            return text;
        }

        protected override string DisplayKey()
        {
            System.Text.StringBuilder k = new System.Text.StringBuilder();
            for (int i = 0; i < InventorySmoker.MaxHooks; i++) k.Append(inventory.Hook(i).Itemstack?.Collectible?.Code).Append(',');
            return k.ToString();
        }

        /// <summary>With the door open you can see the meat hanging from the two rails.</summary>
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            if (!DoorsOpen) return false;
            string side = Block?.Variant?["side"] ?? "north";

            for (int i = 0; i < Hooks; i++)
            {
                ItemStack s = inventory.Hook(i).Itemstack;
                if (s == null) continue;
                MeshData mesh = ContentMesh.For(Api, tesselator, s);
                if (mesh == null) continue;
                MeshData m = mesh.Clone();
                if (m.VerticesCount == 0) continue;

                // Item models come flat or upright depending on the item: stand a flat piece up so it hangs
                float[] bb = Bounds(m);
                float sx = bb[3] - bb[0], sy = bb[4] - bb[1], sz = bb[5] - bb[2];
                if (sy < sz && sy < sx)
                {
                    m.Rotate(new Vec3f((bb[0] + bb[3]) / 2, (bb[1] + bb[4]) / 2, (bb[2] + bb[5]) / 2), GameMath.PIHALF, 0, 0);
                    bb = Bounds(m);
                    sx = bb[3] - bb[0]; sy = bb[4] - bb[1]; sz = bb[5] - bb[2];
                }

                // Centre it on its hook, and size it to hang between the rails without touching its neighbours
                m.Translate(0.5f - (bb[0] + bb[3]) / 2, 0, 0.5f - (bb[2] + bb[5]) / 2);
                float scale = 0.55f;
                if (sy > 0.001f) scale = Math.Min(scale, 0.19f / sy);
                if (sx > 0.001f) scale = Math.Min(scale, 0.13f / sx);
                if (sz > 0.001f) scale = Math.Min(scale, 0.2f / sz);

                // Two rails (front and back), up to five pieces each
                int rail = i % 2, n = i / 2;
                float x = (3.5f + n * 2.25f) / 16f - 0.5f;
                float z = (rail == 0 ? 6.2f : 10.2f) / 16f - 0.5f;

                ContentMesh.Place(m, side, scale, x, 0, z, (i * 37) % 30 - 15);
                float top = MaxY(m);
                m.Translate(0, 19.9f / 16f - top, 0);
                mesher.AddMeshData(m);
            }
            return false;
        }

        static float[] Bounds(MeshData mesh)
        {
            float[] b = { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                for (int k = 0; k < 3; k++)
                {
                    float v = mesh.xyz[i * 3 + k];
                    if (v < b[k]) b[k] = v;
                    if (v > b[k + 3]) b[k + 3] = v;
                }
            }
            return b;
        }

        static float MaxY(MeshData mesh)
        {
            float max = float.MinValue;
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                float y = mesh.xyz[i * 3 + 1];
                if (y > max) max = y;
            }
            return max == float.MinValue ? 0 : max;
        }

        public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                toggleInventoryDialogClient(byPlayer, () =>
                {
                    string title = Block?.GetPlacedBlockName(Api.World, Pos) ?? Lang.Get("forgeandfeast:smoker-title");
                    return new GuiDialogSmoker(title, Inventory, Pos, Api as ICoreClientAPI, this);
                });
            }
            return true;
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            for (int i = 0; i < InventorySmoker.MaxHooks; i++) tree.SetFloat("pkSmoke" + i, progress[i]);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            for (int i = 0; i < InventorySmoker.MaxHooks; i++) progress[i] = tree.GetFloat("pkSmoke" + i);
        }
    }

    public class GuiDialogSmoker : GuiDialogBlockEntity
    {
        readonly BESmoker be;
        long tickId;

        public GuiDialogSmoker(string title, InventoryBase inventory, BlockPos pos, ICoreClientAPI capi, BESmoker be)
            : base(title, inventory, pos, capi)
        {
            if (IsDuplicate) return;
            this.be = be;
        }

        void Compose()
        {
            int hooks = be?.Hooks ?? 4;
            int cols = Math.Min(5, hooks);
            int[] hookIds = new int[hooks];
            for (int i = 0; i < hooks; i++) hookIds[i] = 1 + i;

            ElementBounds fuelBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 30, 1, 1);
            ElementBounds fuelTextBounds = ElementBounds.Fixed(60, 40, 400, 30);
            ElementBounds hookLabel = ElementBounds.Fixed(0, 88, 300, 22);
            ElementBounds hookBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 112, cols, (hooks + cols - 1) / cols);
            double statusY = 112 + 54 * ((hooks + cols - 1) / cols) + 10;
            ElementBounds statusBounds = ElementBounds.Fixed(0, statusY, 440, 44);
            ElementBounds contentBounds = ElementBounds.Fixed(0, 0, 440, statusY + 48);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(contentBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            SingleComposer = capi.Gui
                .CreateCompo("forgeandfeastsmoker" + BlockEntityPosition, dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(DialogTitle, OnTitleBarClose)
                .BeginChildElements(bgBounds)
                    .AddItemSlotGrid(Inventory, DoSendPacket, 1, new int[] { 0 }, fuelBounds, "fuelslot")
                    .AddDynamicText("", CairoFont.WhiteDetailText(), fuelTextBounds, "fueltext")
                    .AddStaticText(Lang.Get("forgeandfeast:smoker-hooks"), CairoFont.WhiteSmallishText(), hookLabel)
                    .AddItemSlotGrid(Inventory, DoSendPacket, cols, hookIds, hookBounds, "hookslots")
                    .AddDynamicText("", CairoFont.WhiteDetailText().WithFontSize(13), statusBounds, "status")
                .EndChildElements()
                .Compose();
            Refresh(0);
        }

        void Refresh(float dt)
        {
            if (SingleComposer == null || be == null) return;
            SingleComposer.GetDynamicText("fueltext")?.SetNewText(be.GetFuelText());
            SingleComposer.GetDynamicText("status")?.SetNewText(be.GetStatusText());
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
            SingleComposer?.GetSlotGrid("hookslots")?.OnGuiClosed(capi);
            base.OnGuiClosed();
        }
    }
}
