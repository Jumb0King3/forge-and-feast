using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// The Potbelly Stove block. It is a base game firepit underneath (so cooking pots, serving meals,
    /// shift-adding fuel, lighting with a firestarter and other mods that cook on a firepit all work),
    /// that faces the player when placed and joins up with stovepipes.
    /// The block code ends in the burnstate (cold / lit / extinct), as the firepit code expects.
    /// </summary>
    public class BlockPotbellyStove : BlockFirepit
    {
        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref string failureCode)
        {
            if (!CanPlaceBlock(world, byPlayer, blockSel, ref failureCode)) return false;
            BlockFacing[] hv = SuggestedHVOrientation(byPlayer, blockSel);
            Block placed = world.GetBlock(CodeWithVariant("side", hv[0].Opposite.Code));
            if (placed == null) return false;
            placed.DoPlaceBlock(world, byPlayer, blockSel, itemstack);
            return true;
        }

        public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos)
        {
            base.OnNeighbourBlockChange(world, pos, neibpos);
            // Redraw so a stovepipe placed on top replaces the stove's own flue stub
            if (world.Side == EnumAppSide.Client) world.BlockAccessor.MarkBlockDirty(pos);
        }
    }

    /// <summary>
    /// The Potbelly Stove's block entity: the base game firepit with stove tiers.
    /// Tier stats come from the block attributes: speedMul (cooking speed), burnMul (fuel lasts longer
    /// in a closed box) and maxTemp (the hottest it gets, whatever the fuel).
    ///
    /// The firepit draws its contents with renderers placed for a firepit on the ground. On a stove they
    /// would end up inside the body, so those are switched off and the contents are drawn here instead,
    /// sitting on the hot plate. The smoke vent behaviour gives the chimney bonus as it does for firepits.
    /// </summary>
    public class BEPotbellyStove : BlockEntityFirepit
    {
        /// <summary>Top of the cooking plate in the model, in blocks.</summary>
        public const float PlateY = 14.5f / 16f;

        public float SpeedMul = 1f;
        public float BurnMul = 1f;
        public int MaxTemp = 1000;

        float lastCookTime;
        string lastContentKey;

        /// <summary>Players with the stove's window open. While any are, its doors stand open.</summary>
        readonly HashSet<string> openers = new HashSet<string>();
        bool doorsOpen;
        public bool DoorsOpen => doorsOpen;

        static readonly FieldInfo RendererField = typeof(BlockEntityFirepit).GetField("renderer", BindingFlags.Instance | BindingFlags.NonPublic);

        public override float BurnDurationModifier => BurnMul;

        public override string DialogTitle
        {
            get
            {
                string name = Block?.GetPlacedBlockName(Api?.World, Pos);
                return string.IsNullOrEmpty(name) ? Lang.Get("forgeandfeast:potbelly-title") : name;
            }
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            JsonObject attr = Block?.Attributes;
            if (attr != null)
            {
                SpeedMul = attr["speedMul"].AsFloat(1f);
                BurnMul = attr["burnMul"].AsFloat(1f);
                MaxTemp = attr["maxTemp"].AsInt(1000);
            }

            if (api.Side == EnumAppSide.Server)
            {
                RegisterGameTickListener(OnStoveTick, 100);
            }
            else if (api is ICoreClientAPI capi)
            {
                DisableFirepitRenderer(capi);
            }
        }

        /// <summary>Removes the firepit's own content renderer; this stove draws its contents itself.</summary>
        void DisableFirepitRenderer(ICoreClientAPI capi)
        {
            try
            {
                if (RendererField?.GetValue(this) is FirepitContentsRenderer r)
                {
                    r.Dispose();
                    RendererField.SetValue(this, null);
                }
                capi.Event.UnregisterEventBusListener(OnSetTransform);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[forgeandfeast] potbelly stove: could not switch off the firepit renderer: " + e.Message);
            }
        }

        /// <summary>Tier speed and heat cap, applied on top of the firepit's own cooking.</summary>
        void OnStoveTick(float dt)
        {
            PruneOpeners();
            if (maxTemperature > MaxTemp) maxTemperature = MaxTemp;
            if (furnaceTemperature > MaxTemp) furnaceTemperature = MaxTemp;

            float now = inputStackCookingTime;
            float delta = now - lastCookTime;
            if (delta > 0 && IsBurning && SpeedMul != 1f)
            {
                inputStackCookingTime = Math.Max(0, now + delta * (SpeedMul - 1f));
            }
            lastCookTime = inputStackCookingTime;
        }

        public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
        {
            base.OnReceivedClientPacket(fromPlayer, packetid, data);
            if (packetid == (int)EnumBlockEntityPacketId.Open || packetid == (int)EnumBlockContainerPacketId.OpenInventory)
            {
                openers.Add(fromPlayer.PlayerUID);
                SetDoors(true);
            }
            else if (packetid == (int)EnumBlockEntityPacketId.Close)
            {
                openers.Remove(fromPlayer.PlayerUID);
                if (openers.Count == 0) SetDoors(false);
            }
        }

        /// <summary>Closes the doors when nobody is using the stove any more (or after a reload).</summary>
        void PruneOpeners()
        {
            if (openers.Count > 0)
            {
                string[] ids = new string[openers.Count];
                openers.CopyTo(ids);
                foreach (string id in ids)
                {
                    IPlayer p = Api.World.PlayerByUid(id);
                    if (p?.Entity == null || p.Entity.Pos.DistanceTo(Pos.ToVec3d()) > 10) openers.Remove(id);
                }
            }
            if (openers.Count == 0 && doorsOpen) SetDoors(false);
        }

        void SetDoors(bool open)
        {
            if (Api?.Side != EnumAppSide.Server || doorsOpen == open) return;
            doorsOpen = open;
            MarkDirty(true);
            Api.World.PlaySoundAt(new AssetLocation("game:sounds/block/metaldoor"),
                Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f, 0.9f);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("pkDoors", doorsOpen);
        }

        #region Drawing

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            bool drewBody = false;
            bool pipeUp = (StovePipes.ConnectionMask(Api.World.BlockAccessor, Pos) & (1 << BlockFacing.UP.Index)) != 0;
            if (pipeUp || doorsOpen)
            {
                MeshData body = StoveMeshes.Body(Api, Block, tesselator, doorsOpen, pipeUp);
                if (body != null)
                {
                    mesher.AddMeshData(body);
                    drewBody = true;
                }
            }

            try
            {
                MeshData content = ContentMeshOnPlate(tesselator);
                if (content != null) mesher.AddMeshData(content);
            }
            catch (Exception e)
            {
                Api.Logger.Warning("[forgeandfeast] potbelly stove: could not draw contents: " + e.Message);
            }

            return drewBody;
        }

        /// <summary>
        /// The pot, dish or food on the stove, moved up so its lowest point rests on the hot plate.
        /// Uses the same sources as the firepit: a mesh the item supplies for firepits (Rustic Cookery's
        /// roasting dish), a pot with its food, inFirePitProps, or the plain item.
        /// </summary>
        MeshData ContentMeshOnPlate(ITesselatorAPI tesselator)
        {
            ItemStack s = inputStack ?? outputStack;
            if (s?.Collectible == null) return null;

            MeshData mesh = null;

            IInFirepitMeshSupplier supplier = s.Collectible.GetCollectibleInterface<IInFirepitMeshSupplier>();
            if (supplier != null)
            {
                EnumFirepitModel model = EnumFirepitModel.Normal;
                MeshData m = supplier.GetMeshWhenInFirepit(s, Api.World, Pos, ref model);
                if (m != null) mesh = m.Clone();
            }

            if (mesh == null && (s.Block is BlockCookingContainer || s.Block is BlockCookedContainerBase))
            {
                ItemStack[] ingredients = (s == inputStack && s.Block is BlockCookingContainer) ? NonEmpty(otherCookingSlots) : null;
                bool showsFood;
                MeshData pot = ContentMesh.Pot(Api, tesselator, s, ingredients, out showsFood);
                if (pot != null) mesh = pot.Clone();
            }

            if (mesh == null && s.Class == EnumItemClass.Block)
            {
                InFirePitProps props = GetRenderProps(s);
                if (props != null)
                {
                    tesselator.TesselateBlock(s.Block, out mesh);
                    mesh?.ModelTransform(props.Transform);
                }
            }

            if (mesh == null)
            {
                MeshData m = ContentMesh.For(Api, tesselator, s);
                if (m == null) return null;
                mesh = m.Clone();
                float scale = s.Block != null ? 0.5f : 0.55f;
                mesh.Scale(new Vec3f(0.5f, 0, 0.5f), scale, scale, scale);
            }

            if (mesh == null || mesh.VerticesCount == 0) return null;
            mesh.Translate(0, PlateY - MinY(mesh), 0);
            return mesh;
        }

        static float MinY(MeshData mesh)
        {
            float min = float.MaxValue;
            for (int i = 0; i < mesh.VerticesCount; i++)
            {
                float y = mesh.xyz[i * 3 + 1];
                if (y < min) min = y;
            }
            return min == float.MaxValue ? 0 : min;
        }

        static ItemStack[] NonEmpty(ItemSlot[] slots)
        {
            if (slots == null) return null;
            List<ItemStack> list = new List<ItemStack>();
            for (int i = 0; i < slots.Length; i++) if (!slots[i].Empty) list.Add(slots[i].Itemstack);
            return list.ToArray();
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAttributes(tree, worldForResolving);
            doorsOpen = tree.GetBool("pkDoors");

            // Redraw when what sits on the plate changes (the firepit only redraws for its own renderer)
            if (Api?.Side == EnumAppSide.Client)
            {
                string key = ContentKey();
                if (key != lastContentKey)
                {
                    lastContentKey = key;
                    MarkDirty(true);
                }
            }
        }

        string ContentKey()
        {
            System.Text.StringBuilder k = new System.Text.StringBuilder();
            k.Append(doorsOpen).Append('|').Append(inputStack?.Collectible?.Code).Append('|').Append(outputStack?.Collectible?.Code).Append('|');
            ItemSlot[] cs = otherCookingSlots;
            if (cs != null) for (int i = 0; i < cs.Length; i++) k.Append(cs[i].Itemstack?.Collectible?.Code).Append(',');
            return k.ToString();
        }

        #endregion
    }

    /// <summary>Shared mesh helpers for stoves that are not BEFuelHeater stations.</summary>
    public static class StoveMeshes
    {
        /// <summary>The block's model without its element named "pipe", for when a stovepipe sits on top.</summary>
        public static MeshData WithoutStub(ICoreAPI api, Block block, ITesselatorAPI tesselator)
        {
            return Body(api, block, tesselator, false, true);
        }

        /// <summary>
        /// The stove's model: the "-open" shape (doors swung open) when open, and without its element named
        /// "pipe" when a stovepipe sits on top.
        /// </summary>
        public static MeshData Body(ICoreAPI api, Block block, ITesselatorAPI tesselator, bool open, bool noStub)
        {
            CompositeShape cs = block?.Shape;
            if (cs?.Base == null) return null;
            Dictionary<string, MeshData> cache = ObjectCacheUtil.GetOrCreate(api, "forgeandfeast-stovebodies", () => new Dictionary<string, MeshData>());
            string key = block.Id + (open ? "o" : "c") + (noStub ? "n" : "s");
            lock (cache)
            {
                MeshData mesh;
                if (cache.TryGetValue(key, out mesh)) return mesh;

                AssetLocation baseLoc = cs.Base.Clone();
                if (open) baseLoc.Path += "-open";
                AssetLocation loc = baseLoc.WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                Shape shape = Shape.TryGet(api, loc);
                if (shape?.Elements == null) return null;

                string[] keep = null;
                if (noStub)
                {
                    List<string> names = new List<string>();
                    for (int i = 0; i < shape.Elements.Length; i++)
                    {
                        if (shape.Elements[i].Name != "pipe") names.Add(shape.Elements[i].Name);
                    }
                    keep = names.ToArray();
                }

                tesselator.TesselateShape(block, shape, out mesh, new Vec3f(cs.rotateX, cs.rotateY, cs.rotateZ), null, keep);
                cache[key] = mesh;
                return mesh;
            }
        }
    }
}
