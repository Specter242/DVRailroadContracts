using System;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// What the runtime makes of the world's day against the last contract day it rolled over
    /// (the review of 2026-11-10; owner: "fix all of the other stuff").
    ///
    /// - **A day on:** its midnight is scored, as ever.
    /// - **Several days on at once** (the contracts were off for a while, or the clock was set
    ///   on): only the last midnight is scored. The days between are skipped, and the open bills
    ///   are moved on by them, so nothing ages while nobody could have moved it. Scoring each
    ///   missed midnight in one go took a contract down, with its penalties, in a single tick.
    /// - **The clock set back:** no midnight; the contract day follows the clock, and the open
    ///   bills are moved back with it, keeping their ages.
    /// </summary>
    public static class DayRoll
    {
        /// <summary>The days to skip unscored, and the days the clock went back; both 0 for the same day or the next. <paramref name="lastDay"/> under 0: no day rolled yet.</summary>
        public static (int Skipped, int Back) Plan(int lastDay, int today)
        {
            if (lastDay < 0 || today == lastDay)
                return (0, 0);
            if (today < lastDay)
                return (0, lastDay - today);
            return (Math.Max(0, today - lastDay - 1), 0);
        }

        /// <summary>A bill's opening (or any time kept in contract days) moved by whole days; null stays null.</summary>
        public static double? Moved(double? when, int days) => when.HasValue ? when.Value + days : (double?)null;
    }
}
