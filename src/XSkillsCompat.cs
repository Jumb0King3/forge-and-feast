using System;
using Vintagestory.API.Common;

namespace ForgeAndFeast
{
    /// <summary>
    /// xSkills (and its 1.22 fork) gives cooking XP and perks to the "owner" of a firepit or oven: an xSkills block
    /// entity behaviour that Forge and Feast adds to its stoves by patch when xSkills is installed. The base game
    /// firepit sets the owner when a player opens it; the stoves do the same here. Nothing happens without xSkills.
    /// </summary>
    public static class XSkillsCompat
    {
        public static void SetOwner(BlockEntity be, IPlayer player)
        {
            if (be == null || player == null) return;
            // Read the behaviour list by reflection: its List type lives in an assembly the game's mod
            // compiler doesn't reference, so naming it in code stops the mod compiling
            System.Collections.IEnumerable behaviors = null;
            try { behaviors = typeof(BlockEntity).GetField("Behaviors")?.GetValue(be) as System.Collections.IEnumerable; }
            catch (Exception) { behaviors = null; }
            if (behaviors == null) return;
            foreach (object o in behaviors)
            {
                BlockEntityBehavior b = o as BlockEntityBehavior;
                if (b == null || b.GetType().Name != "BlockEntityBehaviorOwnable") continue;
                try
                {
                    b.GetType().GetProperty("Owner")?.SetValue(b, player);
                    be.MarkDirty();
                }
                catch (Exception) { }
            }
        }
    }
}
