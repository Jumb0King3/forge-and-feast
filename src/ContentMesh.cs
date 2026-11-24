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
    /// Puts an item's textures into the block atlas so the item can be drawn as part of a block,
    /// the same way the game's shelves and display cases do it.
    /// </summary>
    public class ItemInBlockAtlas : ITexPositionSource
    {
        readonly ICoreClientAPI capi;
        readonly Item item;
        readonly System.Collections.IDictionary itemTextures;
        readonly System.Collections.IDictionary shapeTextures;

        /// <summary>Reads a dictionary member without naming its generic type (the mod compiler can't see System.Collections).</summary>
        static System.Collections.IDictionary Member(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            System.Reflection.FieldInfo f = t.GetField(name);
            if (f != null) return f.GetValue(obj) as System.Collections.IDictionary;
            System.Reflection.PropertyInfo p = t.GetProperty(name);
            return p?.GetValue(obj) as System.Collections.IDictionary;
        }

        public ItemInBlockAtlas(ICoreClientAPI capi, Item item)
        {
            this.capi = capi;
            this.item = item;
            itemTextures = Member(item, "Textures");
            try
            {
                AssetLocation sl = item.Shape?.Base?.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
                Shape shape = sl == null ? null : Shape.TryGet(capi, sl);
                shapeTextures = Member(shape, "Textures");
            }
            catch (Exception) { shapeTextures = null; }
        }

        public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

        public TextureAtlasPosition this[string textureCode]
        {
            get
            {
                AssetLocation path = null;
                if (itemTextures != null)
                {
                    CompositeTexture tex = (itemTextures.Contains(textureCode) ? itemTextures[textureCode] : (itemTextures.Contains("all") ? itemTextures["all"] : null)) as CompositeTexture;
                    if (tex != null) path = tex.Baked?.BakedName ?? tex.Base;
                }
                if (path == null && shapeTextures != null && shapeTextures.Contains(textureCode)) path = shapeTextures[textureCode] as AssetLocation;
                if (path == null) path = new AssetLocation(textureCode);
                return Get(path);
            }
        }

        TextureAtlasPosition Get(AssetLocation path)
        {
            TextureAtlasPosition pos = capi.BlockTextureAtlas[path];
            if (pos != null) return pos;
            IAsset asset = capi.Assets.TryGet(path.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
            if (asset == null) return capi.BlockTextureAtlas.UnknownTexturePosition;
            int subId;
            capi.BlockTextureAtlas.GetOrInsertTexture(path, out subId, out pos, () => asset.ToBitmap(capi));
            return pos ?? capi.BlockTextureAtlas.UnknownTexturePosition;
        }
    }

    /// <summary>Meshes for things shown sitting in or on a stove.</summary>
    public static class ContentMesh
    {
        /// <summary>A mesh for any item or block stack, textured from the block atlas. Null if it can't be drawn.</summary>
        public static MeshData For(ICoreAPI api, ITesselatorAPI tesselator, ItemStack stack)
        {
            ICoreClientAPI capi = api as ICoreClientAPI;
            if (capi == null || stack?.Collectible == null) return null;
            string key = (stack.Class == EnumItemClass.Block ? "b:" : "i:") + stack.Collectible.Code;
            Dictionary<string, MeshData> cache = ObjectCacheUtil.GetOrCreate(capi, "forgeandfeast-contentmeshes", () => new Dictionary<string, MeshData>());
            lock (cache)
            {
                MeshData mesh;
                if (cache.TryGetValue(key, out mesh)) return mesh;
                try
                {
                    if (stack.Block != null && stack.Block.Id != 0) tesselator.TesselateBlock(stack.Block, out mesh);
                    else if (stack.Item != null) tesselator.TesselateItem(stack.Item, out mesh, new ItemInBlockAtlas(capi, stack.Item));
                }
                catch (Exception)
                {
                    mesh = null;
                }
                cache[key] = mesh;
                return mesh;
            }
        }

        /// <summary>
        /// A pot with its food showing, like the firepit draws it: raw ingredients while cooking, the meal once cooked.
        /// Falls back to the plain pot.
        /// </summary>
        public static MeshData Pot(ICoreAPI api, ITesselatorAPI tesselator, ItemStack potStack, ItemStack[] ingredients, out bool showsFood)
        {
            showsFood = false;
            ICoreClientAPI capi = api as ICoreClientAPI;
            if (capi == null || potStack?.Block == null) return null;
            try
            {
                MealMeshCache meals = capi.ModLoader.GetModSystem<MealMeshCache>();
                if (meals != null)
                {
                    if (potStack.Block is BlockCookingContainer cooking && ingredients != null && ingredients.Length > 0)
                    {
                        int servings;
                        CookingRecipe recipe = cooking.GetMatchingCookingRecipe(capi.World, ingredients, out servings);
                        if (recipe != null)
                        {
                            MeshData m = meals.GenMealInContainerMesh(potStack.Block, recipe, ingredients);
                            if (m != null) { showsFood = true; return m; }
                        }
                    }
                    else if (potStack.Block is BlockCookedContainerBase cooked)
                    {
                        ItemStack[] contents = cooked.GetNonEmptyContents(capi.World, potStack);
                        CookingRecipe recipe = cooked.GetCookingRecipe(capi.World, potStack);
                        if (contents != null && contents.Length > 0)
                        {
                            MeshData m = meals.GenMealInContainerMesh(potStack.Block, recipe, contents);
                            if (m != null) { showsFood = true; return m; }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // fall through to the plain pot
            }
            return For(api, tesselator, potStack);
        }

        /// <summary>Turns a stove-relative position to match which way the stove faces.</summary>
        public static void Place(MeshData mesh, string side, float scale, float x, float y, float z, float spinDeg = 0)
        {
            float rot = 0;
            float xr = x, zr = z;
            switch (side)
            {
                case "east": rot = 270; xr = -z; zr = x; break;
                case "south": rot = 180; xr = -x; zr = -z; break;
                case "west": rot = 90; xr = z; zr = -x; break;
            }
            if (rot + spinDeg != 0) mesh.Rotate(new Vec3f(0.5f, 0, 0.5f), 0, (rot + spinDeg) * GameMath.DEG2RAD, 0);
            mesh.Scale(new Vec3f(0.5f, 0, 0.5f), scale, scale, scale);
            mesh.Translate(xr, y, zr);
        }
    }
}
