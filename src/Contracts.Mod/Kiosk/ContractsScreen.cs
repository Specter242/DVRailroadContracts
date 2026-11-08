using System.Collections.Generic;
using System.Linq;
using DV.ServicePenalty.UI;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// The Contracts list: the shops, the player's contracts, then every contract on offer,
    /// at every kiosk (owner, 2026-11-03: "manage contracts without having to travel all over
    /// the map"), this place's own first. Each row shows the contract and, in the value column,
    /// "offer", "locked" (licenses missing), or for the player's own its cars still to move, level
    /// and performance ("9 L2 85%"); the player's own go most cars to move first (Ace, Discord,
    /// 2026-11-08). Confirm opens the contract; cancel goes back to the main menu. It scrolls like
    /// the stock lists, and is read again each time it opens.
    /// </summary>
    internal sealed class ContractsScreen : ScrollableDisplayScreen
    {
        private DisplayScreenSwitcher kiosk;
        private CareerManagerMainScreen main;
        private ContractScreen detail;
        private KioskLines lines;
        private KioskPlace place;
        private readonly List<ContractView> entries = new List<ContractView>();
        private int offers;

        public IReadOnlyList<ContractView> Entries => entries;
        public KioskPlace Place => place;

        protected override int TotalSlotCount => entries.Count;

        internal void Init(DisplayScreenSwitcher kiosk, CareerManagerMainScreen main, ContractScreen detail, KioskLines lines)
        {
            this.kiosk = kiosk;
            this.main = main;
            this.detail = detail;
            this.lines = lines;
            scrollUpArrow = lines.ArrowUp;
            scrollDownArrow = lines.ArrowDown;
            activeSlotCount = lines.Rows.Count;
            selector = new IntIterator(0, 0, isWrappable: true);
        }

        private void Refresh()
        {
            place = place ?? KioskPlaces.Of(kiosk);
            entries.Clear();
            var desk = KioskMenu.Desk;
            if (desk == null)
                return;
            // The shops come first (owner, 2026-11-01: "Trains will need to be purchasable";
            // 2026-11-02: "where do we buy coaches?"), with the spike's stand-in desk too.
            entries.Add(new ContractView { Id = Runtime.RuntimeDesk.ShopId, Short = "Buy locomotives", IsOffer = false });
            entries.Add(new ContractView { Id = Runtime.RuntimeDesk.CoachShopId, Short = "Buy coaches", IsOffer = false });
            // Bought vehicles sold back (owner, 2026-11-07: "You should be re-railing them or selling them").
            entries.Add(new ContractView { Id = Runtime.RuntimeDesk.SellId, Short = "Sell vehicles", IsOffer = false });
            // Contract cars with no job now (set out for the pickup, idle...): a row that says which and why.
            var jobless = desk.Jobless();
            if (jobless.Count > 0)
                entries.Add(new ContractView { Id = Runtime.RuntimeDesk.JoblessId, Short = JoblessTitle, IsOffer = false, OpenWaybills = jobless.Count });
            entries.AddRange(desk.Mine());
            var offered = desk.Offers(place);
            offers = offered.Count;
            entries.AddRange(offered);
        }

        public override void Activate(IDisplayScreen previousScreen)
        {
            Refresh();
            SetSelectorWithinBounds();
            if (ReferenceEquals(previousScreen, main))
            {
                selector.Reset();
                IndexOfFirstDisplayedEntry = 0;
            }
            else
            {
                SetIndexOfFirstDisplayWithinBounds();
            }
            Scroll(IndexOfFirstDisplayedEntry, selector.Current);
            lines.Title.text = place == null ? KioskMenu.Entry : $"{KioskMenu.Entry}: {place.Name}";
            var mine = entries.Count(e => !e.IsOffer && Runtime.RuntimeDesk.IsContract(e.Id));
            var message = KioskMenu.TakeMessage();
            var owed = KioskMenu.Desk?.Owed ?? 0.0;
            lines.Info.text = message ?? KioskMenu.Desk?.Notice
                              ?? (owed >= 1.0 ? $"Owed ${owed:N0}; {mine} yours, {offers} on offer"
                                  : offers + mine == 0 ? "No contracts on offer" : $"{mine} yours, {offers} on offer");
        }

        public override void Disable()
        {
            lines.Title.text = lines.Info.text = string.Empty;
            lines.ClearRows();
            base.Disable();
        }

        public override void HandleInputAction(InputAction input)
        {
            switch (input)
            {
                case InputAction.Up when selector.HasElements:
                    ScrollUp();
                    break;
                case InputAction.Down when selector.HasElements:
                    ScrollDown();
                    break;
                case InputAction.Cancel:
                    kiosk.SetActiveDisplay(main);
                    break;
                case InputAction.Confirm when selector.HasElements:
                    var entry = entries[IndexOfFirstDisplayedEntry + selector.Current];
                    if (entry.Id == Runtime.RuntimeDesk.JoblessId)
                    {
                        // Which cars, why, and when each is dealt with: on the info screen, back here after.
                        // A page at a time: up and down turn the pages, confirm comes back here.
                        var info = kiosk.GetComponentInChildren<CareerManagerInfoScreen>(true);
                        InfoPages.Show(kiosk, info, this, JoblessTitle, KioskMenu.Desk?.Jobless().Text ?? "Every contract car has a job.");
                        break;
                    }
                    detail.Show(entry);
                    kiosk.SetActiveDisplay(detail);
                    break;
            }
        }

        public override void PopulateTextsFromIndex(int startingIndex)
        {
            base.PopulateTextsFromIndex(startingIndex);
            var shown = System.Math.Min(entries.Count - startingIndex, activeSlotCount);
            for (var i = 0; i < shown; i++)
            {
                var c = entries[startingIndex + i];
                lines.Rows[i].Id.text = c.Short;
                lines.Rows[i].Value.text = Value(c);
            }
            lines.ClearRows(System.Math.Max(shown, 0));
        }

        public const string JoblessTitle = "Cars without jobs";

        internal static string Value(ContractView c)
        {
            if (Runtime.RuntimeDesk.ShopOf(c.Id) != null || c.Id == Runtime.RuntimeDesk.SellId)
                return "shop";
            if (c.Id == Runtime.RuntimeDesk.JoblessId)
                return $"{c.OpenWaybills}";
            if (c.IsOffer)
                return c.Needs != null ? "locked" : "offer";
            return c.ToMove.HasValue ? $"{c.ToMove.Value} {Standing(c)}" : Standing(c);
        }

        /// <summary>The player's contract's level and performance ("L2 85%", "L2>3 85%", "new L1"), for the list and the contract's title.</summary>
        internal static string Standing(ContractView c)
        {
            if (c.Level == 0)
                return $"new L{c.RequestedLevel}";
            var perf = c.Performance.HasValue ? $" {c.Performance.Value * 100f:0}%" : "";
            return c.RequestedLevel != c.Level ? $"L{c.Level}>{c.RequestedLevel}{perf}" : $"L{c.Level}{perf}";
        }

        public override void HighlightSelected(int newHighlight, int prevHighlighted = -1)
        {
            if (prevHighlighted != -1 && prevHighlighted != newHighlight)
                lines.Color(prevHighlighted, kiosk.REGULAR_COLOR);
            if (newHighlight != -1)
                lines.Color(newHighlight, kiosk.HIGHLIGHTED_COLOR);
        }

        /// <summary>The list's rows as the screen shows them now (for tests).</summary>
        public IEnumerable<string> Shown() =>
            lines.Rows.Where(r => !string.IsNullOrEmpty(r.Id.text)).Select(r => $"{r.Id.text} [{r.Value.text}]");
    }
}
