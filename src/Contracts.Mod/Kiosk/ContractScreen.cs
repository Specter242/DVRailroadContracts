using System;
using System.Collections.Generic;
using System.Linq;
using DV.ServicePenalty.UI;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// One contract: its industry and state on the title and info lines, and what can be done
    /// with it as rows. An offer can be taken at any level (the value column shows each
    /// level's carloads a day: out, in and waste for an industry), once the player holds the
    /// licenses it needs; until then the info line says which. The player's own can be raised, lowered, kept at its level
    /// (undoing a change asked for today) or ended, and its last row shows the quick-delivery
    /// bonus. Lowering and ending show their penalty. Each action asks first, on the kiosk's
    /// own confirm screen; level changes and new contracts take effect at midnight, an ending now.
    /// </summary>
    internal sealed class ContractScreen : ScrollableDisplayScreen
    {
        private sealed class Choice
        {
            public string Label, Value, Ask;
            public Action Do;
            // An action whose result is a message for the player (a purchase), shown in full on the
            // kiosk's info screen; and a reason a row can't be done, too long for the info line.
            public Func<string> Act;
            public string Explain;
        }

        private DisplayScreenSwitcher kiosk;
        private ContractsScreen list;
        private CareerManagerCustomActionChoiceScreen confirm;
        private CareerManagerInfoScreen info;
        private KioskLines lines;
        private ContractView contract;
        private readonly List<Choice> choices = new List<Choice>();

        public ContractView Contract => contract;
        protected override int TotalSlotCount => choices.Count;

        internal void Init(DisplayScreenSwitcher kiosk, ContractsScreen list, CareerManagerCustomActionChoiceScreen confirm, CareerManagerInfoScreen info, KioskLines lines)
        {
            this.kiosk = kiosk;
            this.list = list;
            this.confirm = confirm;
            this.info = info;
            this.lines = lines;
            scrollUpArrow = lines.ArrowUp;
            scrollDownArrow = lines.ArrowDown;
            activeSlotCount = lines.Rows.Count;
            selector = new IntIterator(0, 0, isWrappable: true);
        }

        /// <summary>Sets the contract the screen opens on; called just before it is activated from the list.</summary>
        internal void Show(ContractView c)
        {
            contract = c;
            selector.Reset();
            IndexOfFirstDisplayedEntry = 0;
        }

        private void Build()
        {
            choices.Clear();
            var desk = KioskMenu.Desk;
            if (contract == null || desk == null)
                return;
            if (contract.Id == Runtime.RuntimeDesk.SellId)
            {
                // Each vehicle the player bought, with the offer for it; the sale's result goes on the
                // info screen. One that can't be sold now says why there.
                foreach (var unit in Runtime.VehicleSales.Owned())
                {
                    var u = unit;
                    choices.Add(u.Blocker != null
                        ? new Choice { Label = u.Label, Value = $"${u.Offer:N0}", Explain = $"{u.Label} can't be sold now.\n\n{u.Blocker}" }
                        : new Choice { Label = u.Label, Value = $"${u.Offer:N0}", Ask = Runtime.VehicleSales.Ask(u), Act = () => Runtime.VehicleSales.Sell(u) });
                }
                if (choices.Count == 0)
                    choices.Add(new Choice { Label = "Nothing to sell", Value = "", Ask = "Locomotives and coaches you buy here can be sold back." });
                return;
            }
            if (Runtime.RuntimeDesk.ShopOf(contract.Id) is Runtime.LocoShop.Kind kind)
            {
                // A purchase's result, or why it can't be made, goes on the kiosk's info screen in
                // full (a Discord report, 2026-11-03: the refusal ran past the info line's edge).
                var place = list.Place;
                foreach (var item in Runtime.LocoShop.Catalogue(kind))
                {
                    var it = item;
                    choices.Add(Runtime.LocoShop.Money < it.Price
                        ? new Choice { Label = it.Name, Value = $"${it.Price:N0}", Explain = Runtime.LocoShop.ShortOf(it) }
                        : new Choice
                        {
                            Label = it.Name, Value = $"${it.Price:N0}",
                            Ask = Runtime.LocoShop.AskToBuy(it, place),
                            Act = () => Runtime.LocoShop.Buy(it, place),
                        });
                }
                return;
            }
            if (contract.IsOffer)
            {
                // A contract starts at level 1 and grows a level at a time (owner, 2026-11-01).
                // The spike's stand-in still offers every level.
                var top = Runtime.RuntimeDesk.IsStandIn(desk) ? 5 : 1;
                for (var level = 1; level <= top; level++)
                {
                    var l = level;
                    // Licenses gate the contract (D19): without them, the row says what it needs.
                    choices.Add(contract.Needs != null
                        ? new Choice { Label = $"Take at level {l}", Value = "locked", Ask = contract.Needs }
                        : new Choice
                        {
                            Label = $"Take at level {l}", Value = desk.Volume(contract, l),
                            Ask = $"Take {contract.Short} at level {l}? It starts at midnight. " +
                                  (Runtime.RuntimeDesk.IsPassenger(contract.Id)
                                      ? "From 06:00 the next morning its trains run to the timetable, with coaches you own."
                                      : "From 06:00 the next morning its cars come and go through the interchanges, with their jobs."),
                            Do = () => desk.Take(contract, l),
                        });
                }
                return;
            }
            var asked = contract.RequestedLevel;
            // A raise is one level from the current one, with the standing for it (owner,
            // 2026-11-02: "gate upgrading contracts to performance, consistent performance").
            if (asked < 5 && asked == contract.Level && contract.Level >= 1)
            {
                var blocker = desk.RaiseBlocker(contract);
                choices.Add(blocker == null
                    ? new Choice
                    {
                        Label = $"Raise to level {asked + 1}", Value = desk.Volume(contract, asked + 1),
                        Ask = $"Raise {contract.Short} to level {asked + 1} from midnight?",
                        Do = () => desk.RequestLevel(contract, asked + 1),
                    }
                    : new Choice { Label = $"Raise to level {asked + 1}", Value = "locked", Ask = blocker });
            }
            // Lowering costs the penalty at midnight; a day more of history by then can only make it less.
            if (asked > 1)
            {
                var penalty = desk.Penalty(contract, asked - 1);
                choices.Add(new Choice
                {
                    Label = $"Lower to level {asked - 1}", Value = penalty > 0.0 ? $"-${penalty:N0}" : "",
                    Ask = $"Lower {contract.Short} to level {asked - 1} from midnight?" + (penalty > 0.0 ? $" Penalty up to ${penalty:N0}." : ""),
                    Do = () => desk.RequestLevel(contract, asked - 1),
                });
            }
            if (contract.Level > 0 && asked != contract.Level)
                choices.Add(new Choice
                {
                    Label = $"Keep level {contract.Level}", Value = "",
                    Ask = $"Keep {contract.Short} at level {contract.Level}?",
                    Do = () => desk.RequestLevel(contract, contract.Level),
                });
            var ending = desk.Penalty(contract, 0);
            choices.Add(new Choice
            {
                Label = "End contract", Value = ending > 0.0 ? $"-${ending:N0}" : "",
                Ask = $"End {contract.Short} now?" + (ending > 0.0 ? $" Penalty ${ending:N0}." : ""),
                Do = () => desk.End(contract),
            });
            // The quick-delivery bonus at its level, for reading (D7).
            var bonus = Contracts.Core.Industries.ContractTerms.BonusPercent(contract.Level);
            choices.Add(new Choice
            {
                Label = "Quick delivery bonus", Value = bonus > 0.0 ? $"{bonus:0}%" : "from L2",
                Ask = bonus > 0.0 ? $"{bonus:0}% on day 1, {bonus / 2:0.#}% day 2, {bonus / 4:0.##}% day 3"
                    : "Level 2 pays 4%, L3 6%, L4 8%, L5 10%",
            });
        }

        public override void Activate(IDisplayScreen previousScreen)
        {
            Build();
            SetSelectorWithinBounds();
            SetIndexOfFirstDisplayWithinBounds();
            Scroll(IndexOfFirstDisplayedEntry, selector.Current);
            // The info line holds about 37 characters, so the player's level and performance go on the title.
            if (contract != null && contract.Id == Runtime.RuntimeDesk.SellId)
            {
                lines.Title.text = "Sell vehicles";
                lines.Info.text = $"Cash ${Runtime.LocoShop.Money:N0}";
                return;
            }
            if (contract != null && Runtime.RuntimeDesk.ShopOf(contract.Id) is Runtime.LocoShop.Kind shop)
            {
                lines.Title.text = shop == Runtime.LocoShop.Kind.Coaches ? "Coaches" : "Locomotives";
                lines.Info.text = $"Cash ${Runtime.LocoShop.Money:N0}";
                return;
            }
            lines.Title.text = contract == null ? "" : contract.IsOffer ? contract.Short : $"{contract.Short}  {ContractsScreen.Value(contract)}";
            lines.Info.text = contract == null || KioskMenu.Desk == null ? ""
                : contract.Needs != null ? contract.Needs
                : contract.IsOffer ? KioskMenu.Desk.Legend(contract)
                // Not started yet: when its cars come (a contract's work starts at the first service after its first midnight).
                : contract.Level < 1 ? (Runtime.RuntimeDesk.IsPassenger(contract.Id) ? "Starts at midnight; trains from 06:00" : "Starts at midnight; cars from 06:00")
                : Runtime.RuntimeDesk.IsPassenger(contract.Id) ? $"{KioskMenu.Desk.Volume(contract, contract.RequestedLevel)} a day, {contract.OpenWaybills} open"
                : $"{KioskMenu.Desk.Volume(contract, contract.RequestedLevel)} a day, {contract.OpenWaybills} open, {contract.OnOrder} ordered";
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
                    kiosk.SetActiveDisplay(list);
                    break;
                case InputAction.Confirm when selector.HasElements:
                    var choice = choices[IndexOfFirstDisplayedEntry + selector.Current];
                    if (choice.Explain != null)
                    {
                        // Not available, for a reason too long for the info line: the info screen says it in full.
                        KioskMenu.ShowInfo(info, this, choice.Label, choice.Explain);
                        kiosk.SetActiveDisplay(info);
                        break;
                    }
                    if (choice.Do == null && choice.Act == null)
                    {
                        // Not available: say why on the info line.
                        lines.Info.text = choice.Ask;
                        break;
                    }
                    // Cancel on the confirm screen comes back here. Confirm acts, then shows the list, or
                    // for an action with a result (a purchase), the result in full and then this screen.
                    confirm.SetCustomActionData(() =>
                    {
                        if (choice.Act != null)
                        {
                            KioskMenu.ShowInfo(info, this, choice.Label, choice.Act());
                            confirm.OverrideReturnScreen(info);
                        }
                        else
                        {
                            choice.Do();
                            confirm.OverrideReturnScreen(list);
                        }
                    }, this, choice.Label, choice.Ask);
                    kiosk.SetActiveDisplay(confirm);
                    break;
            }
        }

        public override void PopulateTextsFromIndex(int startingIndex)
        {
            base.PopulateTextsFromIndex(startingIndex);
            var shown = Math.Min(choices.Count - startingIndex, activeSlotCount);
            for (var i = 0; i < shown; i++)
            {
                lines.Rows[i].Id.text = choices[startingIndex + i].Label;
                lines.Rows[i].Value.text = choices[startingIndex + i].Value;
            }
            lines.ClearRows(Math.Max(shown, 0));
        }

        public override void HighlightSelected(int newHighlight, int prevHighlighted = -1)
        {
            if (prevHighlighted != -1 && prevHighlighted != newHighlight)
                lines.Color(prevHighlighted, kiosk.REGULAR_COLOR);
            if (newHighlight != -1)
                lines.Color(newHighlight, kiosk.HIGHLIGHTED_COLOR);
        }

        public IEnumerable<string> Shown() =>
            lines.Rows.Where(r => !string.IsNullOrEmpty(r.Id.text)).Select(r => $"{r.Id.text} [{r.Value.text}]");
    }
}
