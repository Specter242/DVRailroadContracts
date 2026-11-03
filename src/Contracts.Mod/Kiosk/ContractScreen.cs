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
        }

        private DisplayScreenSwitcher kiosk;
        private ContractsScreen list;
        private CareerManagerCustomActionChoiceScreen confirm;
        private KioskLines lines;
        private ContractView contract;
        private readonly List<Choice> choices = new List<Choice>();

        public ContractView Contract => contract;
        protected override int TotalSlotCount => choices.Count;

        internal void Init(DisplayScreenSwitcher kiosk, ContractsScreen list, CareerManagerCustomActionChoiceScreen confirm, KioskLines lines)
        {
            this.kiosk = kiosk;
            this.list = list;
            this.confirm = confirm;
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
            if (Runtime.RuntimeDesk.ShopOf(contract.Id) is Runtime.LocoShop.Kind kind)
            {
                var place = list.Place;
                foreach (var item in Runtime.LocoShop.Catalogue(kind))
                {
                    var it = item;
                    choices.Add(new Choice
                    {
                        Label = it.Name, Value = $"${it.Price:N0}",
                        Ask = $"Buy {it.Name} for ${it.Price:N0}? It is delivered here at once.",
                        Do = () => KioskMenu.Tell(Runtime.LocoShop.Buy(it, place)),
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
                            Ask = $"Take {contract.Short} at level {l}? It starts at midnight.",
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
                    if (choice.Do == null)
                    {
                        // Not available: say why on the info line.
                        lines.Info.text = choice.Ask;
                        break;
                    }
                    // Cancel on the confirm screen comes back here; confirm acts, then shows the list.
                    confirm.SetCustomActionData(() =>
                    {
                        choice.Do();
                        confirm.OverrideReturnScreen(list);
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
