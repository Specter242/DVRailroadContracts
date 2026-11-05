using System;
using System.Collections.Generic;
using DV.ServicePenalty.UI;
using HarmonyLib;
using TMPro;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// A long text on the kiosk's info screen, a page at a time (a Discord report, 2026-11-05: the
    /// "Cars without jobs" list ran off the bottom, and its up and down buttons only closed it;
    /// owner: "Looks good. Continue"). The stock screen shows one paragraph and goes back on any
    /// button. Shown through here, its paragraph lays the text out in pages (TextMeshPro's page
    /// mode), the title says which page of how many, and up and down turn the pages; confirm and
    /// cancel go back as before. The paragraph's own layout comes back when the screen closes.
    /// </summary>
    internal static class InfoPages
    {
        private sealed class Paging
        {
            public string Title;
            public TextOverflowModes Overflow;
            public int Page = 1, Count = 1;
        }

        private static readonly Dictionary<CareerManagerInfoScreen, Paging> showing = new Dictionary<CareerManagerInfoScreen, Paging>();

        /// <summary>Shows a text on the info screen a page at a time, going back to <paramref name="back"/> after.</summary>
        public static void Show(DisplayScreenSwitcher kiosk, CareerManagerInfoScreen screen, IDisplayScreen back, string title, string text)
        {
            KioskMenu.ShowInfo(screen, back, title, text);
            kiosk.SetActiveDisplay(screen);
            try
            {
                var paging = new Paging { Title = title, Overflow = screen.paragraph.overflowMode };
                screen.paragraph.overflowMode = TextOverflowModes.Page;
                screen.paragraph.pageToDisplay = 1;
                screen.paragraph.ForceMeshUpdate();
                paging.Count = Math.Max(1, screen.paragraph.textInfo?.pageCount ?? 1);
                showing[screen] = paging;
                Turn(screen, paging, 0);
            }
            catch (Exception e)
            {
                Main.Error($"kiosk: paging the info screen failed; it shows the text whole: {e.Message}");
            }
        }

        /// <summary>The number of pages showing, and the page (for the probe); (0, 0) when the screen isn't paging.</summary>
        internal static (int Page, int Count) State(CareerManagerInfoScreen screen) =>
            screen != null && showing.TryGetValue(screen, out var p) ? (p.Page, p.Count) : (0, 0);

        private static void Turn(CareerManagerInfoScreen screen, Paging paging, int by)
        {
            paging.Page = Math.Max(1, Math.Min(paging.Count, paging.Page + by));
            screen.paragraph.pageToDisplay = paging.Page;
            screen.title.text = paging.Count > 1 ? $"{paging.Title} {paging.Page}/{paging.Count}" : paging.Title;
        }

        [HarmonyPatch(typeof(CareerManagerInfoScreen), nameof(CareerManagerInfoScreen.HandleInputAction))]
        private static class PageButtons
        {
            private static bool Prefix(CareerManagerInfoScreen __instance, InputAction input)
            {
                if ((input != InputAction.Up && input != InputAction.Down) || !showing.TryGetValue(__instance, out var paging))
                    return true;
                Turn(__instance, paging, input == InputAction.Down ? 1 : -1);
                return false;
            }
        }

        [HarmonyPatch(typeof(CareerManagerInfoScreen), nameof(CareerManagerInfoScreen.Disable))]
        private static class Closed
        {
            private static void Prefix(CareerManagerInfoScreen __instance)
            {
                if (!showing.TryGetValue(__instance, out var paging))
                    return;
                showing.Remove(__instance);
                __instance.paragraph.overflowMode = paging.Overflow;
                __instance.paragraph.pageToDisplay = 1;
            }
        }
    }
}
