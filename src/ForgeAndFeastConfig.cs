using Vintagestory.API.Common;

namespace ForgeAndFeast
{
    /// <summary>Server settings, in ModConfig/forgeandfeast.json.</summary>
    public class ForgeAndFeastConfig
    {
        /// <summary>
        /// When true, every melt in the Crucible Furnace leaves slag on top of the metal. Skim it off with tongs
        /// (right-click the furnace with tongs in hand) before pouring, or the casting comes out 10% short.
        /// </summary>
        public bool CrucibleFurnaceSlag = false;

        public static ForgeAndFeastConfig Current = new ForgeAndFeastConfig();

        public static void Load(ICoreAPI api)
        {
            ForgeAndFeastConfig cfg = null;
            try { cfg = api.LoadModConfig<ForgeAndFeastConfig>("forgeandfeast.json"); }
            catch (System.Exception e) { api.Logger.Warning("[forgeandfeast] Could not read forgeandfeast.json, using defaults: {0}", e.Message); }
            if (cfg == null)
            {
                cfg = new ForgeAndFeastConfig();
                try { api.StoreModConfig(cfg, "forgeandfeast.json"); } catch (System.Exception) { }
            }
            Current = cfg;
        }
    }
}
