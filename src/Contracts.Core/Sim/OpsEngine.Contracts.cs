using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Contracts.Core.Data;
using Contracts.Core.State;

namespace Contracts.Core.Sim
{
    /// <summary>
    /// Standing contracts as Railroader runs them (operations-design.md, 20.3
    /// and 20.3.1; RR Model.Ops.Contract, ContractExtensions, Industry and
    /// OpsController.DayDidChange). Each contract industry holds a level 0–5.
    /// The level's factor scales the industry's storage and rates (its traffic)
    /// and the pay of moves to and from it. At every midnight the day's
    /// performance is sampled from the age of its open waybills, and then the
    /// level changes: to the player's request, or down two levels for three
    /// poor days. A downgrade costs a penalty.
    /// </summary>
    public sealed partial class OpsEngine
    {
        /// <summary>Railroader's contract percent for levels 1–5 (ContractExtensions.NumbersForTier).</summary>
        public static readonly double[] LevelFactors = { 0.24, 0.34, 0.49, 0.70, 1.0 };

        /// <summary>Railroader keeps a week of daily performance (Industry.PerformanceHistoryLimit).</summary>
        public const int HistoryDays = 7;

        /// <summary>A late car counts in the next midnight's sample as a waybill this many days old (no credit).</summary>
        public const double LateAgeDays = 5.0;

        public bool ContractsOn => Settings.Contracts.Enabled;

        public static double LevelFactor(int level) => level < 1 ? 0.0 : LevelFactors[Math.Min(5, level) - 1];

        private static int StartLevel(Industry industry, OpsSettings settings) =>
            Math.Max(0, Math.Min(5, industry.ContractLevel ?? settings.Contracts.StartLevel));

        private static double Factor(Industry industry, int level, OpsSettings settings) =>
            !settings.Contracts.Enabled || !industry.UsesContract ? 1.0 : LevelFactor(level);

        public bool UnderContract(Industry industry) => ContractsOn && industry != null && industry.UsesContract;

        public int Level(Industry industry) => State.Industries.TryGetValue(industry.Id, out var s) ? s.Level : 0;

        /// <summary>The factor on an industry's storage and rates: its level's, or 1 outside contracts.</summary>
        public double ContractFactor(Industry industry) => industry == null ? 1.0 : Factor(industry, Level(industry), Settings);

        /// <summary>The factor on the pay of moves to and from an industry.</summary>
        public double PayFactor(Industry industry) =>
            UnderContract(industry) && Settings.Contracts.LevelScalesPay ? LevelFactor(Level(industry)) : 1.0;

        /// <summary>Railroader orders nothing for a contract industry without a contract.</summary>
        public bool ShouldOrder(IndustryComponent c) => c.Orders && (!UnderContract(c.Industry) || Level(c.Industry) > 0);

        /// <summary>Mean of the performance history, or null without any.</summary>
        public double? AveragePerformance(Industry industry)
        {
            var history = State.Industries[industry.Id].History;
            return history.Count == 0 ? (double?)null : history.Average(h => h.Value);
        }

        /// <summary>
        /// The levels the player may ask for (RR ContractExtensions.AvailableContracts).
        /// With a contract: up to two above the current level for a week averaging
        /// over 95%, one above for over 90%, the same for over 70%, else one
        /// below; never above the current level with under three days of history,
        /// nor more than one above with under five. Without one: up to the
        /// configured start maximum. Level 0 ends the contract.
        /// </summary>
        public int MaxAvailableLevel(Industry industry)
        {
            var s = State.Industries[industry.Id];
            if (s.Level < 1)
                return Settings.Contracts.MaxStartLevel;
            var average = AveragePerformance(industry) ?? 0.0;
            var max = average > 0.95 ? s.Level + 2 : average > 0.9 ? s.Level + 1 : average > 0.7 ? s.Level : s.Level - 1;
            if (s.History.Count < 3)
                max = Math.Min(max, s.Level);
            if (s.History.Count < 5)
                max = Math.Min(max, s.Level + 1);
            return Math.Max(0, Math.Min(5, max));
        }

        /// <summary>Railroader's downgrade penalty (ContractExtensions.PenaltyForChange), in this game's money.</summary>
        public double PenaltyFor(Industry industry, int target)
        {
            var s = State.Industries[industry.Id];
            var drop = s.Level - target;
            if (drop <= 0)
                return 0.0;
            var days = Math.Max(1, s.History.Count);
            var unit = Settings.Contracts.PenaltyBase * Settings.PayScale;
            return unit * drop + unit * Math.Max(6 - days, 1);
        }

        /// <summary>
        /// The player asks for a contract level, applied at the next midnight
        /// (RR Industry.ModifyContract). Asking for the current level withdraws
        /// a request. False with the reason when the level is not available.
        /// </summary>
        public bool RequestLevel(string industryId, int level, out string message)
        {
            var industry = Data.Industries.FirstOrDefault(i => i.Id == industryId);
            if (industry == null || !UnderContract(industry))
            {
                message = $"no contract industry '{industryId}'";
                return false;
            }
            var s = State.Industries[industry.Id];
            if (level == s.Level)
            {
                s.RequestedLevel = null;
                message = $"{industry.Name}: staying at level {s.Level}";
                return true;
            }
            var max = MaxAvailableLevel(industry);
            if (level < 0 || level > max)
            {
                message = $"{industry.Name}: level {level} is not available; up to {max} now ({PerformanceText(industry)})";
                return false;
            }
            s.RequestedLevel = level;
            var penalty = PenaltyFor(industry, level);
            message = $"{industry.Name}: {(level == 0 ? "ending the contract" : $"level {s.Level} → {level}")} at midnight" +
                      (penalty > 0 ? $", penalty ${penalty.ToString("N0", CultureInfo.InvariantCulture)}" : "");
            Info(message);
            return true;
        }

        public string PerformanceText(Industry industry)
        {
            var s = State.Industries[industry.Id];
            var average = AveragePerformance(industry);
            return average.HasValue ? $"performance {average.Value * 100:F0}% over {s.History.Count} days" : "no performance history yet";
        }

        // ------------------------------------------------------------ spotting

        /// <summary>Railroader's quick-delivery bonus in percent of the base pay, by level (Contract.TimelyDeliveryBonus).</summary>
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

        /// <summary>
        /// The quick-delivery bonus for spotting at a contract industry
        /// (RR IndustryContext.PayWaybill and Contract.TimelyDeliveryBonus): the
        /// level's percent of the base pay on the first day after the waybill
        /// was written (whole days since, rounded down, 0), half on the second,
        /// a quarter on the third, none later.
        /// </summary>
        public double DeliveryBonus(Industry industry, double basePay, double daysSinceWritten)
        {
            if (!UnderContract(industry) || basePay <= 0)
                return 0.0;
            var days = (int)Math.Floor(daysSinceWritten + 1e-9);
            var share = days == 0 ? 1.0 : days == 1 ? 0.5 : days == 2 ? 0.25 : 0.0;
            return Math.Round(basePay * BonusPercent(Level(industry)) * share / 100.0);
        }

        /// <summary>
        /// At spotting: count the car received, add the quick-delivery bonus of
        /// the destination's contract, and pay a late car at the late factor
        /// (the bonus is reckoned on the full base pay, as Railroader does).
        /// </summary>
        private void Received(Waybill wb)
        {
            var industry = (Data.Destination(wb.Dest) as IndustryComponent)?.Industry;
            if (industry != null && State.Industries.TryGetValue(industry.Id, out var received))
                received.ReceivedCars++;
            if (!ContractsOn || wb.Pay <= 0)
                return;
            wb.Bonus = DeliveryBonus(industry, wb.Pay, (Now - wb.Created) / OpsClock.MinutesPerDay);
            if (Now - wb.Created <= Settings.Contracts.DueHours * 60.0 + Epsilon)
                return;
            wb.Late = true;
            wb.Pay = Math.Round(wb.Pay * Settings.Contracts.LatePayFactor);
            foreach (var involved in Involved(wb).Where(UnderContract))
                State.Industries[involved.Id].LateSpots.Add(Now);
        }

        /// <summary>The industries a waybill runs to or from.</summary>
        private IEnumerable<Industry> Involved(Waybill wb)
        {
            var dest = (Data.Destination(wb.Dest) as IndustryComponent)?.Industry;
            var origin = (Data.Destination(wb.Origin) as IndustryComponent)?.Industry;
            if (dest != null)
                yield return dest;
            if (origin != null && origin != dest)
                yield return origin;
        }

        // ------------------------------------------------------------ midnight

        private void RunMidnights()
        {
            while (Now + Epsilon >= State.NextMidnightAt)
            {
                var midnight = State.NextMidnightAt;
                if (ContractsOn)
                {
                    UpdatePerformance(midnight);
                    foreach (var industry in Data.Industries.Where(UnderContract))
                        RollContract(industry);
                }
                State.Day++;
                State.NextMidnightAt = midnight + OpsClock.MinutesPerDay;
            }
        }

        /// <summary>
        /// The day's performance (RR OpsController.UpdatePerformance and
        /// Industry.CalculatePerformance): the mean age in days, less grace days,
        /// of the industry's open paying waybills older than 0.1 day, plus five
        /// days for each car spotted late since the last midnight, mapped from
        /// 5 days → 0 to 1 day → 1. With no such waybills it is 1, but a perfect
        /// day before any car was received records nothing.
        /// </summary>
        private void UpdatePerformance(double midnight)
        {
            var ages = Data.Industries.ToDictionary(i => i.Id, _ => new List<double>());
            foreach (var wb in State.Waybills.Values.Where(w => w.State == WaybillState.Open && w.Pay > 0 && w.Created <= midnight))
            {
                var age = (midnight - wb.Created) / OpsClock.MinutesPerDay - wb.GraceDays;
                foreach (var industry in Involved(wb))
                    ages[industry.Id].Add(age);
            }
            foreach (var industry in Data.Industries.Where(UnderContract))
            {
                var s = State.Industries[industry.Id];
                var list = ages[industry.Id].Where(a => a > 0.1).ToList();
                list.AddRange(s.LateSpots.Where(t => t <= midnight).Select(_ => LateAgeDays));
                s.LateSpots.RemoveAll(t => t <= midnight);
                if (s.Level < 1)
                    continue;
                var performance = list.Count == 0 ? 1.0 : InverseLerp(5.0, 1.0, list.Average());
                if (performance > 0.99 && s.ReceivedCars < 1)
                    continue;
                s.History.Add(new PerformanceEntry { Day = State.Day + 1, Value = Math.Round(performance, 4) });
                while (s.History.Count > HistoryDays)
                    s.History.RemoveAt(0);
            }
        }

        /// <summary>
        /// The midnight level change (RR Industry.RollToNextContract): three
        /// recent days averaging under 50% force the level down two; otherwise
        /// the player's request applies. A downgrade costs the penalty; a new
        /// level clears the history; level 0 ends the contract and sends cars
        /// bound for the industry home.
        /// </summary>
        private void RollContract(Industry industry)
        {
            var s = State.Industries[industry.Id];
            var next = s.RequestedLevel;
            var reason = "";
            if (s.Level > 0 && s.History.Count >= 3 && s.History.Skip(s.History.Count - 3).Average(h => h.Value) < 0.5)
            {
                var proposed = Math.Max(0, s.Level - 2);
                if ((next ?? s.Level) > proposed)
                {
                    next = proposed;
                    reason = " (low performance)";
                }
            }
            s.RequestedLevel = null;
            if (!next.HasValue || next.Value == s.Level)
                return;
            var penalty = PenaltyFor(industry, next.Value);
            if (penalty > 0)
            {
                result.Commands.Add(new PayCommand { Amount = -penalty, Memo = $"contract penalty: {industry.Name}" });
                State.Ledger.Add(new LedgerEntry { T = Now, Kind = "penalty", Amount = -penalty, Memo = $"{industry.Name} level {s.Level} → {next.Value}{reason}" });
                State.Stats.Revenue -= penalty;
            }
            var before = s.Level;
            s.Level = next.Value;
            s.History.Clear();
            s.ReceivedCars = 0;
            if (s.Level == 0)
                ReturnWaybillsFrom(industry);
            Notify($"Contract with {industry.Name} {(s.Level == 0 ? "has ended" : $"is now at level {s.Level}")}{reason}" +
                   (penalty > 0 ? $" (${penalty.ToString("N0", CultureInfo.InvariantCulture)} penalty)" : ""));
            Info($"contract {industry.Id}: level {before} → {s.Level}{reason}");
        }

        /// <summary>Cars bound for an industry without a contract go home unpaid (RR OpsController.ReturnWaybillsFrom).</summary>
        private void ReturnWaybillsFrom(Industry industry)
        {
            var ids = new HashSet<string>(industry.Components.Select(c => c.FullId));
            foreach (var wb in State.Waybills.Values.Where(w => ids.Contains(w.Dest) && w.State != WaybillState.AwaitingPickup).ToList())
            {
                var car = State.Cars[wb.CarGuid];
                State.Waybills.Remove(wb.Id);
                car.WaybillId = null;
                car.Progress = 0.0;
                RouteHome(car, car.LoadId, car.Home, paid: false);
            }
        }

        private static double InverseLerp(double a, double b, double value) =>
            Math.Max(0.0, Math.Min(1.0, (value - a) / (b - a)));
    }
}
