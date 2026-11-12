using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// What a held contract pays and costs on top of stock wages (design D7: "Railroader's
    /// downgrade penalties and quick-delivery bonus apply on top of stock pay"; owner,
    /// 2026-11-03: "we can add that"). After Railroader, on the industry contracts' levels
    /// and standing (ContractStanding):
    ///
    /// - **The quick-delivery bonus** at levels 2–5 is 4, 6, 8 or 10% of a contract job's base
    ///   wage, reckoned car by car on each car's share of it: full while the car's bill is under
    ///   a day old when the job is done, half on its second day, a quarter on its third, none
    ///   later. A passenger train's bill opens when the train is due.
    /// - **The penalty** for lowering or ending a contract is Railroader's $250 unit at this
    ///   game's pay scale (×30) for each level dropped, plus the unit for each day of history
    ///   short of six (at least one): a young contract costs more to give up.
    /// - **Poor performance forces a drop:** three scored days in a row averaging under 50%
    ///   take the contract down two levels at midnight (a level 1 or 2 contract ends), and the
    ///   penalty applies.
    /// </summary>
    /// <summary>
    /// A contract's level from midnight (0 ends it; its own level: no change), whether poor
    /// performance forced it, and why a raise asked for was not given (null if none was refused).
    /// </summary>
    public readonly struct MidnightChange
    {
        public MidnightChange(int target, bool forced, string raiseRefused)
        {
            Target = target;
            Forced = forced;
            RaiseRefused = raiseRefused;
        }

        public int Target { get; }
        public bool Forced { get; }
        public string RaiseRefused { get; }
    }

    public static class ContractTerms
    {
        /// <summary>Railroader's $250 penalty unit at this game's pay scale (design D7: 250 × 30).</summary>
        public const double PenaltyUnit = 250.0 * 30.0;

        public const int ForcedDropDays = 3;
        public const double ForcedDropAverage = 0.5;
        public const int ForcedDropLevels = 2;

        /// <summary>The quick-delivery bonus at a level, percent of the base wage on the bill's first day.</summary>
        public static double BonusPercent(int level)
        {
            switch (level)
            {
                case 2: return 4.0;
                case 3: return 6.0;
                case 4: return 8.0;
                case 5: return 10.0;
                default: return 0.0;
            }
        }

        /// <summary>The share of the bonus for a bill this many days old: 1 on its first day, ½ on its second, ¼ on its third, then none.</summary>
        public static double BonusShare(double ageDays)
        {
            if (ageDays < 0.0)
                ageDays = 0.0;
            var days = (int)Math.Floor(ageDays + 1e-9);
            return days == 0 ? 1.0 : days == 1 ? 0.5 : days == 2 ? 0.25 : 0.0;
        }

        /// <summary>The bonus on a wage (or a car's share of one) for a bill this old at a contract level, in whole dollars.</summary>
        public static double Bonus(double wage, int level, double ageDays) =>
            wage <= 0.0 ? 0.0 : Math.Round(wage * BonusPercent(level) * BonusShare(ageDays) / 100.0);

        /// <summary>
        /// The penalty for taking a contract from a level to a lower one (0 ends it), with this many
        /// days of history at the level; nothing for a raise, or for a contract not yet started.
        /// </summary>
        public static double Penalty(int level, int target, int historyDays)
        {
            var drop = level - Math.Max(0, target);
            if (level < 1 || drop <= 0)
                return 0.0;
            var days = Math.Max(1, historyDays);
            return PenaltyUnit * (drop + Math.Max(6 - days, 1));
        }

        /// <summary>The level poor performance forces a contract down to at midnight (0 ends it), or null if it holds.</summary>
        public static int? ForcedLevel(int level, IReadOnlyList<double> history)
        {
            if (level < 1 || history == null || history.Count < ForcedDropDays)
                return null;
            var recent = history.Skip(history.Count - ForcedDropDays).Average();
            return recent < ForcedDropAverage ? Math.Max(0, level - ForcedDropLevels) : (int?)null;
        }

        /// <summary>
        /// Whether a contract can be given up at no cost: until its first midnight. A contract
        /// taken waits for midnight to start, and giving it up before then was always free; the
        /// career's first contract starts at once (owner, 2026-11-10: "Faster service is fine"),
        /// and keeps that day to back out of.
        /// </summary>
        public static bool FreeToGiveUp(int takenDay, int today) => today <= takenDay;

        /// <summary>The level one of the player's contracts must have reached before the bridge is offered (owner, 2026-11-10: "the bridge contract needs to be a later game contract").</summary>
        public const int BridgeFromLevel = 3;

        /// <summary>Why the bridge can't be taken yet, for the kiosk's info line; null once a held contract is at <see cref="BridgeFromLevel"/>.</summary>
        public static string BridgeBlocker(IEnumerable<int> heldLevels) =>
            (heldLevels ?? Enumerable.Empty<int>()).Any(l => l >= BridgeFromLevel) ? null : $"Needs a contract at level {BridgeFromLevel}";

        /// <summary>The kiosk's line while the contracts are on hold (at most 37 characters).</summary>
        public const string HoldLine = "On hold: Persistent Jobs has changed";

        /// <summary>The kiosk's page while the contracts are on hold: what is missing, what that means, and what to do.</summary>
        public static string HoldPage(string why) =>
            $"Railroad Contracts can't work with the Persistent Jobs installed. {why}.\n\n" +
            "While it is on hold no cars come, the industries don't work, and nothing is scored or charged: your contracts keep their levels and their cars' waybills keep their ages.\n\n" +
            "Update Railroad Contracts, or put back the Persistent Jobs version its page names. The contracts go on by themselves at the next load.";

        /// <summary>The kiosk's page on penalties not yet paid; null with nothing owed.</summary>
        public static string OwedPage(double owed) =>
            owed < 1.0 ? null
                : $"You owe ${owed:N0} in contract penalties that your cash didn't cover when they were charged.\n\n" +
                  "It is taken from your cash as money comes in, all of it until the debt is paid, so wages you collect go to it first.\n\n" +
                  "Penalties are charged at midnight, for a contract lowered or ended, and for one that three poor days took down.";

        /// <summary>
        /// The kiosk's question before a contract is taken: when it starts and its cars come, how
        /// to pass the wait, and what backing out costs (the most an ending costs at level 1).
        /// <paramref name="now"/>: it starts at once, as a career's first contract does.
        /// </summary>
        public static string TakeQuestion(string name, int level, bool passenger, bool now)
        {
            var exit = $"${Penalty(1, 0, 1):N0}";
            if (now)
                return $"Take {name} at level {level}? It starts now: " +
                       (passenger ? "its trains run to the timetable from today, with coaches you own. "
                           : "its first cars come to the interchanges right away, then each day at 06:00. ") +
                       $"Giving it up before midnight is free; ending it later costs up to {exit}.";
            return $"Take {name} at level {level}? It starts at midnight; " +
                   (passenger ? "its trains run to the timetable from 06:00, with coaches you own. "
                       : "its cars come to the interchanges from 06:00, with their jobs. ") +
                   $"{SleepHint} Giving it up before midnight is free; ending it later costs up to {exit}.";
        }

        /// <summary>How to sleep through the wait for a new contract's first cars.</summary>
        public const string SleepHint = "To sleep through the wait, set Sleep Cooldown to Can always sleep in your difficulty preset.";

        /// <summary>
        /// The kiosk's one line on what midnight did, at most <paramref name="maxChars"/>: the
        /// contracts that dropped or ended with what it cost, then the raises refused, each named
        /// if that fits and counted if not; null with nothing to tell.
        /// </summary>
        public static string MidnightNotice(IReadOnlyList<string> drops, double charged, IReadOnlyList<string> refusals, int maxChars)
        {
            var dropped = drops?.Count ?? 0;
            var refused = refusals?.Count ?? 0;
            if (dropped + refused == 0)
                return null;
            var cost = charged > 0.0 ? $": -${charged:N0}" : "";
            var parts = new List<string>();
            if (dropped > 0)
                parts.Add(string.Join(", ", drops) + cost);
            if (refused > 0)
                parts.Add(string.Join(", ", refusals));
            var named = string.Join("; ", parts);
            if (named.Length <= maxChars)
                return named;
            parts.Clear();
            if (dropped > 0)
                parts.Add(refused > 0 ? $"{dropped} dropped{cost}" : $"{dropped} contracts dropped{cost}");
            if (refused > 0)
                parts.Add(refused == 1 && dropped == 0 ? "A raise was refused: see the contract" : $"{refused} not raised");
            return string.Join("; ", parts);
        }

        /// <summary>
        /// What midnight makes of a contract: every change is final then, and only then (owner,
        /// 2026-11-10: "Penalties shouldn't apply until midnight when the contract is finalized").
        /// - A contract the player asked to end goes to level 0.
        /// - Otherwise it goes to the level asked for (none asked: it holds), a raise only one
        ///   level and only with the standing for it (<see cref="ContractStanding.RaiseBlocker"/>).
        /// - Poor performance takes it lower than that, if it asks for more (<see cref="ForcedLevel"/>).
        /// The penalty is <see cref="Penalty"/> from the level to the target, at this history.
        /// </summary>
        public static MidnightChange AtMidnight(int level, int requested, bool ending, IReadOnlyList<double> history)
        {
            var target = ending && level >= 1 ? 0 : requested >= 1 ? requested : level;
            var forced = ForcedLevel(level, history);
            var isForced = forced.HasValue && target > forced.Value;
            if (isForced)
                target = forced.Value;
            string refused = null;
            if (level >= 1 && target > level)
            {
                refused = ContractStanding.RaiseBlocker(level, history);
                target = refused != null ? level : level + 1;
            }
            return new MidnightChange(target, isForced, refused);
        }
    }
}
