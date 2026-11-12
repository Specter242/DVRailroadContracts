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
    /// licenses it needs; until then the info line says which. For the player's own industry or
    /// bridge contract, the info line counts its cars still to move ("To move: 3 out / 5 in / 1
    /// waste"), its first row opens its Cars page (each car by line and what it needs next, with
    /// its job), and the next shows its cars a day (Ace, Discord, 2026-11-08). Then it can be
    /// raised, lowered, kept at its level (undoing a change asked for today) or ended, and its last
    /// row shows the quick-delivery bonus. Lowering and ending show their penalty. Each action
    /// asks first, on the kiosk's own confirm screen; level changes and new contracts take effect
    /// at midnight, an ending now.
    /// </summary>
    internal sealed class ContractScreen : ScrollableDisplayScreen
    {
        private sealed class Choice
        {
            public string Label, Value, Ask;
            // For a row that can't be done: why, on the info line while the row is highlighted.
            public string Hint;
            public Action Do;
            // An action whose result is a message for the player (a purchase), shown in full on the
            // kiosk's info screen; and a reason a row can't be done, too long for the info line.
            public Func<string> Act;
            public string Explain;
            // A long text to read, a page at a time on the info screen (the contract's cars), read when opened.
            public Func<string> Page;
        }

        private DisplayScreenSwitcher kiosk;
        private ContractsScreen list;
        private CareerManagerCustomActionChoiceScreen confirm;
        private CareerManagerInfoScreen info;
        private KioskLines lines;
        private ContractView contract;
        private ContractCarsView cars;
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
            cars = null;
            var desk = KioskMenu.Desk;
            if (contract == null || desk == null)
                return;
            if (contract.Id == Runtime.RuntimeDesk.SellId)
            {
                // Each vehicle the player bought, with the offer for it; the sale's result goes on the
                // info screen. One that can't be sold now says why there.
                foreach (var row in desk.Selling())
                {
                    var u = row;
                    choices.Add(u.Blocker != null
                        ? new Choice { Label = u.Label, Value = $"${u.Offer:N0}", Explain = $"{u.Label} can't be sold now.\n\n{u.Blocker}" }
                        : new Choice { Label = u.Label, Value = $"${u.Offer:N0}", Ask = u.Ask, Act = () => desk.Sell(u.Key) });
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
                var coaches = kind == Runtime.LocoShop.Kind.Coaches;
                var money = desk.Money;
                foreach (var row in desk.Shop(coaches, place))
                {
                    var it = row;
                    choices.Add(money < it.Price
                        ? new Choice { Label = it.Name, Value = $"${it.Price:N0}", Explain = Contracts.Core.Shop.VehicleSale.ShortOf(it.Name, it.Price, money) }
                        : new Choice
                        {
                            Label = it.Name, Value = $"${it.Price:N0}",
                            Ask = it.Ask,
                            Act = () => desk.Buy(coaches, it.Key, place),
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
                            // How to pass the wait (mikebackwards, Nexus, 2026-11-10: "Game says I'm not
                            // tired enough ... need to wait 6 real time hours"): the game's Sleep Cooldown
                            // setting, which the mod's page hadn't named.
                            // And what backing out costs, before it is taken (the End row said it only after).
                            Ask = Contracts.Core.Industries.ContractTerms.TakeQuestion(contract.Short, l, Runtime.RuntimeDesk.IsPassenger(contract.Id), desk.StartsAtOnce),
                            Do = () => desk.Take(contract, l),
                        });
                }
                return;
            }
            // Its cars now, a page of them by line and what each needs next, and its cars a day at its
            // level (Ace, Discord, 2026-11-08: "an easy way to keep track of cars in/out/waste").
            cars = desk.Cars(contract);
            if (cars != null)
            {
                var c = contract;
                choices.Add(new Choice
                {
                    Label = "Cars", Value = $"{cars.ToMove} to move",
                    Page = () => desk.Cars(c)?.Page ?? (c.Level < 1 ? "Its cars come from 06:00 the morning after it starts." : "None of its cars are on the map."),
                });
            }
            if (!Runtime.RuntimeDesk.IsPassenger(contract.Id))
                choices.Add(new Choice
                {
                    Label = "Cars a day", Value = desk.Volume(contract, contract.Level >= 1 ? contract.Level : contract.RequestedLevel),
                    Ask = desk.Legend(contract),
                });
            var asked = contract.RequestedLevel;
            // Asked to end: it ends at midnight, and until then keeping its level takes that back.
            if (contract.Ending)
            {
                var due = desk.Penalty(contract, 0);
                choices.Add(new Choice
                {
                    Label = "Ends at midnight", Value = due > 0.0 ? $"-${due:N0}" : "",
                    Ask = due > 0.0 ? $"Ends at midnight: -${due:N0} at most" : "Ends at midnight",
                });
                choices.Add(new Choice
                {
                    Label = $"Keep level {contract.Level}", Value = "",
                    Ask = $"Keep {contract.Short} at level {contract.Level}? It won't end at midnight.",
                    Do = () => desk.RequestLevel(contract, contract.Level),
                });
                AddBonus(contract);
                return;
            }
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
                    // Not yet: the row says how far it is, and the info line why while it is highlighted.
                    : new Choice { Label = $"Raise to level {asked + 1}", Value = desk.RaiseValue(contract) ?? "locked", Ask = blocker, Hint = blocker });
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
            // Ending is final at midnight, with its penalty then (a day more of history by then can
            // only make it less); a contract not yet started is given up at once, free.
            var ending = desk.Penalty(contract, 0);
            choices.Add(new Choice
            {
                Label = "End contract", Value = ending > 0.0 ? $"-${ending:N0}" : "",
                Ask = contract.Level < 1 ? $"Give up {contract.Short} now? It hasn't started, so it costs nothing."
                    : $"End {contract.Short} at midnight?" + (ending > 0.0 ? $" Penalty up to ${ending:N0}, charged then." : " It costs nothing before its first midnight.")
                      + " Until then you can keep it.",
                Do = () => desk.End(contract),
            });
            AddBonus(contract);
        }

        /// <summary>The quick-delivery bonus at the contract's level, for reading (D7).</summary>
        private void AddBonus(ContractView contract)
        {
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
                restInfo = $"Cash ${KioskMenu.Desk?.Money ?? 0.0:N0}";
                ShowInfoLine();
                return;
            }
            if (contract != null && Runtime.RuntimeDesk.ShopOf(contract.Id) is Runtime.LocoShop.Kind shop)
            {
                lines.Title.text = shop == Runtime.LocoShop.Kind.Coaches ? "Coaches" : "Locomotives";
                restInfo = $"Cash ${KioskMenu.Desk?.Money ?? 0.0:N0}";
                ShowInfoLine();
                return;
            }
            lines.Title.text = contract == null ? "" : contract.IsOffer ? contract.Short : $"{contract.Short}  {ContractsScreen.Standing(contract)}";
            restInfo = contract == null || KioskMenu.Desk == null ? ""
                : contract.Needs != null ? contract.Needs
                : contract.IsOffer ? KioskMenu.Desk.Legend(contract)
                // Not started yet: when its cars come (a contract's work starts at the first service after its first midnight).
                : contract.Level < 1 ? (Runtime.RuntimeDesk.IsPassenger(contract.Id) ? "Starts at midnight; trains from 06:00" : "Starts at midnight; cars from 06:00")
                : Runtime.RuntimeDesk.IsPassenger(contract.Id) ? $"{KioskMenu.Desk.Volume(contract, contract.RequestedLevel)} a day, {contract.OpenWaybills} open"
                // Its cars still to move, as they are now (the Cars row has the page of them).
                : cars?.Line ?? "";
            ShowInfoLine();
        }

        // The info line with no row's reason on it.
        private string restInfo = "";

        /// <summary>The info line: the highlighted row's reason if it has one, else the screen's own line.</summary>
        private void ShowInfoLine()
        {
            var at = IndexOfFirstDisplayedEntry + selector.Current;
            lines.Info.text = (selector.HasElements && at >= 0 && at < choices.Count ? choices[at].Hint : null) ?? restInfo;
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
                    ShowInfoLine();
                    break;
                case InputAction.Down when selector.HasElements:
                    ScrollDown();
                    ShowInfoLine();
                    break;
                case InputAction.Cancel:
                    kiosk.SetActiveDisplay(list);
                    break;
                case InputAction.Confirm when selector.HasElements:
                    var choice = choices[IndexOfFirstDisplayedEntry + selector.Current];
                    if (choice.Page != null)
                    {
                        // A page at a time: up and down turn the pages, confirm or cancel come back here.
                        InfoPages.Show(kiosk, info, this, PageTitle(contract), choice.Page());
                        break;
                    }
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
                            // At the kiosk of a player who joined, the host's answer replaces this page when it comes.
                            if (DeskNet.Remote.Active)
                                DeskNet.Remote.Awaiting(kiosk, info, this, choice.Label);
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

        /// <summary>The Cars page's title: the contract's, to which the info screen adds the page.</summary>
        internal static string PageTitle(ContractView c) => $"{c?.Short} cars";

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
