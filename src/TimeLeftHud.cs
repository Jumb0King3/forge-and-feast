using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace ForgeAndFeast
{
    /// <summary>
    /// A small box under the block tooltip that says how much time is left on the base game's fires:
    /// the firepit and the charcoal pit (the pit kiln shows it in its own tooltip). (Forge and Feast's own blocks put theirs in the tooltip.)
    /// </summary>
    public class TimeLeftHud : HudElement
    {
        string shown;

        public override string ToggleKeyCombinationCode => null;

        public TimeLeftHud(ICoreClientAPI capi) : base(capi)
        {
            capi.Event.RegisterGameTickListener(Update, 250);
        }

        void Update(float dt)
        {
            string text = "";
            try { text = Describe(); }
            catch (Exception) { text = ""; }
            if (text == shown) return;
            shown = text;

            if (text.Length == 0)
            {
                ClearComposers();
                return;
            }

            int lines = text.Split('\n').Length;
            double w = 330, h = 18 + lines * 22;
            ElementBounds dialogBounds = ElementBounds.Fixed(EnumDialogArea.CenterTop, 0, 112, w, h);
            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, w, h);
            ElementBounds textBounds = ElementBounds.Fixed(12, 9, w - 24, h - 14);
            SingleComposer = capi.Gui.CreateCompo("ff-timeleft", dialogBounds)
                .AddDialogBG(bgBounds, false)
                .AddStaticText(text, CairoFont.WhiteSmallText(), textBounds)
                .Compose();
        }

        string Describe()
        {
            IClientPlayer player = capi.World.Player;
            BlockSelection sel = player?.CurrentBlockSelection;
            if (sel?.Position == null) return "";
            BlockEntity be = capi.World.BlockAccessor.GetBlockEntity(sel.Position);
            if (be == null) return "";
            double now = capi.World.Calendar.TotalHours;

            BlockEntityFirepit pit = be as BlockEntityFirepit;
            if (pit != null)
            {
                string text = "";
                if (pit.IsBurning)
                {
                    float fuel = pit.fuelBurnTime;
                    CombustibleProperties fp = pit.fuelStack?.Collectible?.CombustibleProps;
                    if (fp != null && fp.BurnDuration > 0 && fp.BurnTemperature > 0) fuel += fp.BurnDuration * pit.BurnDurationModifier * pit.fuelStack.StackSize;
                    text = Lang.Get("forgeandfeast:firepit-fuel-left", BEFuelHeater.FormatSeconds(fuel));
                }
                ItemStack input = pit.inputStack;
                if (pit.IsBurning && input?.Collectible?.CombustibleProps?.SmeltedStack != null)
                {
                    float max = pit.maxCookingTime();
                    if (max > 0)
                    {
                        string cook = Lang.Get("forgeandfeast:firepit-cooking-left", BEFuelHeater.FormatSeconds(max - pit.inputStackCookingTime));
                        text = text.Length > 0 ? text + "\n" + cook : cook;
                    }
                }
                return text;
            }

            if (be is BlockEntityCharcoalPit)
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                object state = be.GetType().GetField("state", flags)?.GetValue(be);
                object done = be.GetType().GetField("finishedAfterTotalHours", flags)?.GetValue(be);
                if (state == null || done == null || state.ToString() != "Lit") return "";
                double left = Convert.ToDouble(done) - now;
                if (left <= 0) return "";
                return Lang.Get("forgeandfeast:charcoalpit-left", FormatHours(left));
            }
            return "";
        }

        /// <summary>In-game hours as "2 d 3 h", "5 h 20 min" or "40 min".</summary>
        public static string FormatHours(double hours)
        {
            int minutes = (int)Math.Ceiling(Math.Max(0, hours) * 60);
            int d = minutes / (24 * 60), h = (minutes / 60) % 24, m = minutes % 60;
            if (d > 0) return Lang.Get("forgeandfeast:time-days", d, h);
            if (h > 0) return Lang.Get("forgeandfeast:time-hours", h, m);
            return Lang.Get("forgeandfeast:time-min-only", Math.Max(1, m));
        }
    }
}
