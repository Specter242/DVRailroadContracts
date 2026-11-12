using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// A held contract's performance, and when it may be raised (owner, 2026-11-02: "gate
    /// upgrading contracts to performance, consistent performance"). After Railroader:
    ///
    /// - **A bill** is open from when a car is billed for a move until the move is done: an
    ///   empty from its order until it is loaded at the shipper, a load from its loading (or
    ///   order) until it is unloaded at the receiver or leaves at an interchange.
    /// - **Each midnight scores the day** from the ages of the contract's bills: those still
    ///   open (but older than <see cref="FreshDays"/>), and those closed since the last midnight
    ///   at their final age. A mean age of a day or less scores 100%, five days 0, linearly
    ///   between. A day without bills to judge is not scored.
    /// - **History** keeps the last <see cref="HistoryDays"/> scored days at the current level;
    ///   a level change clears it.
    /// - **A raise** (one level at a time) needs at least <see cref="RaiseDays"/> scored days at
    ///   the current level, averaging over <see cref="RaiseAverage"/>.
    /// </summary>
    public static class ContractStanding
    {
        public const int HistoryDays = 7;
        public const int RaiseDays = 3;
        public const double RaiseAverage = 0.9;
        public const double FreshDays = 0.1;
        public const double PerfectAgeDays = 1.0;
        public const double FailedAgeDays = 5.0;

        /// <summary>The day's score, 0–1, from open bills' ages at midnight and closed bills' final ages (days); null with nothing to judge.</summary>
        public static double? DayScore(IEnumerable<double> openAges, IEnumerable<double> closedAges)
        {
            var ages = openAges.Where(a => a > FreshDays).Concat(closedAges.Where(a => a >= 0.0)).ToList();
            if (ages.Count == 0)
                return null;
            var score = (ages.Average() - FailedAgeDays) / (PerfectAgeDays - FailedAgeDays);
            return Math.Round(Math.Max(0.0, Math.Min(1.0, score)), 4);
        }

        /// <summary>Adds a day's score, keeping the last <see cref="HistoryDays"/>.</summary>
        public static void Record(List<double> history, double score)
        {
            history.Add(score);
            while (history.Count > HistoryDays)
                history.RemoveAt(0);
        }

        /// <summary>The mean of the history, or null without any.</summary>
        public static double? Average(IReadOnlyCollection<double> history) =>
            history == null || history.Count == 0 ? (double?)null : history.Average();

        /// <summary>
        /// Null if a contract at this level with this history may be raised a level now;
        /// otherwise why not, short enough for the kiosk's info line (about 37 characters).
        /// </summary>
        public static string RaiseBlocker(int level, IReadOnlyCollection<double> history)
        {
            if (level < 1)
                return "It starts at midnight";
            if (level >= 5)
                return "Level 5 is the top";
            var days = history?.Count ?? 0;
            if (days < RaiseDays)
                return $"Needs {RaiseDays} days over {RaiseAverage * 100:0}%; has {days}";
            var average = history.Average();
            if (average <= RaiseAverage)
                return $"Needs over {RaiseAverage * 100:0}%; {Math.Floor(average * 100):0}% in {days} days";
            return null;
        }

        /// <summary>
        /// How far a contract is toward the days a raise needs, for the kiosk's narrow columns
        /// ("2/3d"), or null once it has them, at level 5, or before it starts (captain75tur,
        /// Nexus, 2026-11-10: "L1 %100. how can i L2").
        /// </summary>
        public static string RaiseDaysShort(int level, int days) =>
            level < 1 || level >= 5 || days >= RaiseDays ? null : $"{Math.Max(0, days)}/{RaiseDays}d";

        /// <summary>
        /// A blocked raise's row value, at most 12 characters: the days so far ("2/3 days"), or
        /// that the average is short ("needs >90%"); null if it may be raised.
        /// </summary>
        public static string RaiseBlockedValue(int level, IReadOnlyCollection<double> history)
        {
            if (RaiseBlocker(level, history) == null)
                return null;
            var days = history?.Count ?? 0;
            return level < 1 || level >= 5 ? "locked" : days < RaiseDays ? $"{days}/{RaiseDays} days" : $"needs >{RaiseAverage * 100:0}%";
        }

        /// <summary>
        /// The kiosk's words when midnight refuses a raise the player asked for: the contract
        /// stays at its level, and why, short enough for the info line with a short id.
        /// </summary>
        public static string RaiseRefused(string id, int level, IReadOnlyCollection<double> history)
        {
            var days = history?.Count ?? 0;
            return days < RaiseDays
                ? $"{id} stays L{level}: {days} of {RaiseDays} days"
                : $"{id} stays L{level}: {Math.Floor(history.Average() * 100):0}%, needs over {RaiseAverage * 100:0}%";
        }
    }
}
