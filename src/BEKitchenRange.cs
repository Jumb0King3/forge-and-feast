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
    /// The Kitchen Range: one firebox heating 2-4 burners (by tier). Each burner cooks a pot of food
    /// the vanilla way, cooks single foods like a firepit, or bakes dough and pies like a clay oven.
    /// Baked goods stop at "perfect" and move to the output slot, so they don't char.
    /// </summary>
    public class BEKitchenRange : BEFuelHeater
    {
        readonly InventoryRange inventory;

        public int Burners = InventoryRange.MaxBurners;

        // Per burner progress, in seconds of cooking (or baking level for bakeables)
        readonly float[] progress = new float[InventoryRange.MaxBurners];
        readonly float[] maxProgress = new float[InventoryRange.MaxBurners];
        // What the burner is doing: 0 idle, 1 cooking, 2 baking, 3 waiting (needs heat or output space)
        readonly int[] mode = new int[InventoryRange.MaxBurners];
        readonly string[] lastInputCode = new string[InventoryRange.MaxBurners];

        string lastMeshKey = "";

        public BEKitchenRange()
        {
            inventory = new InventoryRange(null, null);
            inventory.SlotModified += OnSlotModified;
        }

        public override InventoryBase Inventory => inventory;
        public override string InventoryClassName => "forgeandfeastrange";
        public override ItemSlot FuelSlot => inventory.FuelSlot;

        public override void Initialize(ICoreAPI api)
        {
            inventory.Pos = Pos;
            base.Initialize(api);
            inventory.ActiveBurners = Burners;
        }

        protected override void ReadTierStats()
        {
            base.ReadTierStats();
            Burners = GameMath.Clamp(Block?.Attributes?["burners"].AsInt(2) ?? 2, 1, InventoryRange.MaxBurners);
            inventory.ActiveBurners = Burners;
        }

        void OnSlotModified(int slotId)
        {
            if (Api == null) return;
            MarkDirty(Api.Side == EnumAppSide.Server);
        }

        protected override bool HasActiveWork()
        {
            for (int b = 0; b < Burners; b++) if (!inventory.InputSlot(b).Empty) return true;
            return false;
        }

        #region Cooking

        protected override void ProcessContents(float dt)
        {
            for (int b = 0; b < Burners; b++)
            {
                ProcessBurner(b, dt);
            }
        }

        void ProcessBurner(int b, float dt)
        {
            ItemSlot input = inventory.InputSlot(b);
            ItemSlot output = inventory.OutputSlot(b);

            if (input.Empty)
            {
                progress[b] = 0;
                mode[b] = 0;
                lastInputCode[b] = null;
                return;
            }

            ItemStack stack = input.Itemstack;
            string code = stack.Collectible.Code?.ToString();
            if (code != lastInputCode[b])
            {
                progress[b] = 0;
                lastInputCode[b] = code;
                BakingProperties bp = BakingProperties.ReadFrom(stack);
                if (bp != null && bp.ResultCode != null) progress[b] = bp.LevelFrom;
            }

            BakingProperties bake = BakingProperties.ReadFrom(stack);
            if (bake != null && bake.ResultCode != null && stack.Attributes.GetBool("bakeable", true))
            {
                Bake(b, bake, dt);
                return;
            }

            BurnerSlots burner = inventory.Burner(b);
            bool canCook;
            if (stack.Collectible is BlockCookingContainer)
            {
                canCook = output.Empty && stack.Collectible.CanSmelt(Api.World, burner, stack, output.Itemstack);
            }
            else
            {
                canCook = stack.Collectible.CanSmelt(Api.World, burner, stack, output.Itemstack)
                    && stack.Collectible.CombustibleProps?.RequiresContainer != true;
            }

            float itemTemp = HeatBurnerContents(b, dt, canCook ? stack.Collectible.GetMeltingPoint(Api.World, burner, input) : 0);

            if (!canCook)
            {
                progress[b] = 0;
                mode[b] = 0;
                return;
            }

            float meltingPoint = stack.Collectible.GetMeltingPoint(Api.World, burner, input);
            float duration = stack.Collectible.GetMeltingDuration(Api.World, burner, input);
            maxProgress[b] = duration;

            if (itemTemp >= meltingPoint)
            {
                mode[b] = 1;
                float diff = itemTemp / Math.Max(1f, meltingPoint);
                progress[b] += GameMath.Clamp((int)diff, 1, 30) * dt * SpeedMul * VentMul;
            }
            else
            {
                mode[b] = 3;
                if (progress[b] > 0) progress[b] = Math.Max(0, progress[b] - dt);
            }

            if (progress[b] >= duration)
            {
                stack.Collectible.DoSmelt(Api.World, burner, input, output);
                progress[b] = 0;
                mode[b] = 0;
                lastInputCode[b] = input.Itemstack?.Collectible.Code?.ToString();
                input.MarkDirty();
                output.MarkDirty();
                for (int i = 0; i < 4; i++) burner.Slots[i].MarkDirty();
                MarkDirty(true);
            }
        }

        /// <summary>
        /// Heats the burner's contents toward the range temperature, like the firepit heats its input.
        /// With a pot in, the ingredients are heated; otherwise the item itself. Returns the content temperature.
        /// </summary>
        float HeatBurnerContents(int b, float dt, float meltingPoint)
        {
            ItemSlot input = inventory.InputSlot(b);
            ItemStack stack = input.Itemstack;
            ItemSlot[] cooking = inventory.HasPot(b) ? inventory.Burner(b).Slots : null;

            float oldTemp = GetContentTemp(stack, cooking);
            if (!(oldTemp < temperature)) return oldTemp;

            float f = (1 + GameMath.Clamp((temperature - oldTemp) / 30, 0, 1.6f)) * dt;
            if (meltingPoint > 0 && oldTemp >= meltingPoint) f /= 11;

            float newTemp = ChangeTemperature(oldTemp, temperature, f);
            int maxTemp = Math.Max(stack.Collectible.CombustibleProps?.MaxTemperature ?? 0, stack.ItemAttributes?["maxTemperature"].AsInt(0) ?? 0);
            if (maxTemp > 0) newTemp = Math.Min(maxTemp, newTemp);

            if (newTemp != oldTemp) SetContentTemp(stack, cooking, newTemp);
            return newTemp;
        }

        float GetContentTemp(ItemStack stack, ItemSlot[] cooking)
        {
            if (cooking != null)
            {
                bool have = false;
                float lowest = 0;
                for (int i = 0; i < cooking.Length; i++)
                {
                    ItemStack s = cooking[i].Itemstack;
                    if (s == null) continue;
                    float t = s.Collectible.GetTemperature(Api.World, s);
                    lowest = have ? Math.Min(lowest, t) : t;
                    have = true;
                }
                if (have) return lowest;
            }
            return stack.Collectible.GetTemperature(Api.World, stack);
        }

        void SetContentTemp(ItemStack stack, ItemSlot[] cooking, float value)
        {
            bool any = false;
            if (cooking != null)
            {
                for (int i = 0; i < cooking.Length; i++)
                {
                    ItemStack s = cooking[i].Itemstack;
                    if (s == null) continue;
                    s.Collectible.SetTemperature(Api.World, s, value);
                    any = true;
                }
            }
            if (!any) stack.Collectible.SetTemperature(Api.World, stack, value);
        }

        /// <summary>
        /// Bakes like the clay oven: the item climbs from LevelFrom to LevelTo, then turns into the next stage.
        /// The range never lets food go to the charred stage - a finished item moves to the output slot instead.
        /// </summary>
        void Bake(int b, BakingProperties bake, float dt)
        {
            ItemSlot input = inventory.InputSlot(b);
            ItemSlot output = inventory.OutputSlot(b);
            ItemStack stack = input.Itemstack;

            if (IsCharred(bake.ResultCode))
            {
                // Already done: move it out so it can't burn
                mode[b] = 0;
                TryMoveToOutput(input, output);
                return;
            }

            float browning = bake.Temp ?? 160f;
            float itemTemp = HeatBurnerContents(b, dt, 0);
            float timeToBake = stack.Collectible.CombustibleProps?.MeltingDuration * 10f ?? 150f;
            if (timeToBake <= 0) timeToBake = 150f;

            maxProgress[b] = 1;
            if (itemTemp <= browning)
            {
                mode[b] = 3;
                return;
            }

            mode[b] = 2;
            float diff = itemTemp / browning;
            progress[b] += GameMath.Clamp((int)diff, 1, 30) * dt * 1.2f * SpeedMul * VentMul / timeToBake;

            if (progress[b] <= bake.LevelTo) return;

            ItemStack result = ResolveBakeResult(stack, bake.ResultCode);
            if (result == null) return;

            IBakeableCallback callback = stack.Collectible as IBakeableCallback;
            if (callback != null) callback.OnBaked(stack, result);

            result.StackSize = Math.Max(1, stack.StackSize);
            result.Collectible.SetTemperature(Api.World, result, itemTemp);
            input.Itemstack = result;

            BakingProperties next = BakingProperties.ReadFrom(result);
            progress[b] = next?.LevelFrom ?? 0;
            lastInputCode[b] = result.Collectible.Code?.ToString();

            if (next == null || next.ResultCode == null || IsCharred(next.ResultCode))
            {
                TryMoveToOutput(input, output);
            }

            input.MarkDirty();
            MarkDirty(true);
        }

        static bool IsCharred(string code)
        {
            return code != null && code.IndexOf("charred", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        ItemStack ResolveBakeResult(ItemStack stack, string resultCode)
        {
            AssetLocation loc = AssetLocation.Create(resultCode, stack.Collectible.Code.Domain);
            if (stack.Class == EnumItemClass.Block)
            {
                Block block = Api.World.GetBlock(loc);
                return block == null ? null : new ItemStack(block);
            }
            Item item = Api.World.GetItem(loc);
            return item == null ? null : new ItemStack(item);
        }

        void TryMoveToOutput(ItemSlot input, ItemSlot output)
        {
            if (input.Empty) return;
            if (output.Empty)
            {
                output.Itemstack = input.Itemstack;
                input.Itemstack = null;
            }
            else
            {
                ItemStackMoveOperation op = new ItemStackMoveOperation(Api.World, EnumMouseButton.Left, 0, EnumMergePriority.AutoMerge, input.StackSize);
                input.TryPutInto(output, ref op);
            }
            input.MarkDirty();
            output.MarkDirty();
            MarkDirty(true);
        }

        #endregion

        #region GUI

        /// <summary>Status line for one burner, shown in the GUI.</summary>
        public string GetBurnerText(int b)
        {
            ItemSlot input = inventory.InputSlot(b);
            if (input.Empty)
            {
                return inventory.OutputSlot(b).Empty ? Lang.Get("forgeandfeast:burner-empty") : Lang.Get("forgeandfeast:burner-done");
            }

            string what = null;
            BlockCookingContainer pot = input.Itemstack.Collectible as BlockCookingContainer;
            if (pot != null)
            {
                what = pot.GetOutputText(Api.World, inventory.Burner(b), input);
                if (what == null) return Lang.Get("forgeandfeast:burner-norecipe");
            }

            int pct = maxProgress[b] > 0 ? (int)GameMath.Clamp(100 * progress[b] / maxProgress[b], 0, 100) : 0;
            string state;
            switch (mode[b])
            {
                case 1: state = Lang.Get("forgeandfeast:burner-cooking", pct); break;
                case 2: state = Lang.Get("forgeandfeast:burner-baking", pct); break;
                case 3: state = Lang.Get("forgeandfeast:burner-heating"); break;
                default: state = Lang.Get("forgeandfeast:burner-idle"); break;
            }
            return what == null ? state : state + "\n" + what;
        }

        public override bool OnPlayerRightClick(IPlayer byPlayer, BlockSelection blockSel)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                toggleInventoryDialogClient(byPlayer, () =>
                {
                    return new GuiDialogKitchenRange(Lang.Get("forgeandfeast:range-title"), Inventory, Pos, Api as ICoreClientAPI, this);
                });
            }
            return true;
        }

        #endregion

        #region Saving and rendering

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            for (int b = 0; b < InventoryRange.MaxBurners; b++)
            {
                tree.SetFloat("pkProg" + b, progress[b]);
                tree.SetFloat("pkMax" + b, maxProgress[b]);
                tree.SetInt("pkMode" + b, mode[b]);
                tree.SetString("pkCode" + b, lastInputCode[b] ?? "");
            }
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            for (int b = 0; b < InventoryRange.MaxBurners; b++)
            {
                progress[b] = tree.GetFloat("pkProg" + b);
                maxProgress[b] = tree.GetFloat("pkMax" + b);
                mode[b] = tree.GetInt("pkMode" + b);
                string c = tree.GetString("pkCode" + b, "");
                lastInputCode[b] = c.Length == 0 ? null : c;
            }

            if (Api?.Side == EnumAppSide.Client)
            {
                string key = MeshKey();
                if (key != lastMeshKey)
                {
                    lastMeshKey = key;
                    MarkDirty(true);
                }
            }
        }

        string MeshKey()
        {
            string key = "";
            for (int b = 0; b < Burners; b++)
            {
                ItemStack s = inventory.InputSlot(b).Itemstack ?? inventory.OutputSlot(b).Itemstack;
                key += (s?.Block?.Code?.ToString() ?? "-") + "|";
            }
            return key;
        }

        /// <summary>
        /// Where each burner sits on the cooktop, as an offset from the block centre, before the block's
        /// rotation. Must match the burner plates in the shapes (2: side by side, 3: two front + one back,
        /// 4: two by two). The model's front faces north, so the offset is rotated to the block's side.
        /// </summary>
        Vec3f BurnerOffset(int burners, int b)
        {
            float x, z;
            const float d = 3.2f / 16f;
            if (burners == 2) { x = b == 0 ? -d : d; z = 0; }
            else if (burners == 3) { x = b == 2 ? 0 : (b == 0 ? -d : d); z = b == 2 ? d : -d; }
            else { x = (b % 2 == 0) ? -d : d; z = b < 2 ? -d : d; }

            // Rotate to match the block (north 0, east 270, south 180, west 90 - same as the shape rotateY)
            string side = Block?.Variant?["side"] ?? "north";
            switch (side)
            {
                case "east": return new Vec3f(-z, 0, x);
                case "south": return new Vec3f(-x, 0, -z);
                case "west": return new Vec3f(z, 0, -x);
                default: return new Vec3f(x, 0, z);
            }
        }

        /// <summary>Draws any pot (or other block) sitting on each burner.</summary>
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            bool drewBody = false;
            if ((StovePipes.ConnectionMask(Api.World.BlockAccessor, Pos) & (1 << BlockFacing.UP.Index)) != 0)
            {
                MeshData body = GetMeshWithoutStub(tesselator);
                if (body != null)
                {
                    mesher.AddMeshData(body);
                    drewBody = true;
                }
            }

            for (int b = 0; b < Burners; b++)
            {
                ItemStack s = inventory.InputSlot(b).Itemstack ?? inventory.OutputSlot(b).Itemstack;
                if (s?.Block == null) continue;

                MeshData mesh;
                try
                {
                    tesselator.TesselateBlock(s.Block, out mesh);
                }
                catch (Exception)
                {
                    continue;
                }
                if (mesh == null) continue;

                Vec3f off = BurnerOffset(Burners, b);
                mesh = mesh.Clone();
                mesh.Scale(new Vec3f(0.5f, 0, 0.5f), 0.8f, 0.8f, 0.8f);
                mesh.Translate(off.X, 14.35f / 16f, off.Z);
                mesher.AddMeshData(mesh);
            }
            return drewBody;
        }

        #endregion
    }
}
