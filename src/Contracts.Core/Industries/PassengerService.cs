using System;
using System.Collections.Generic;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// A city's passenger contract (design D13, "Passenger service"; owner, 2026-11-03: "yes we
    /// need passenger service"): trains a day departing the city, on Passenger Jobs' routes.
    ///
    /// - **A timetable** (owner, 2026-11-03: "it should be regular daily service"): the level's
    ///   trains depart at even intervals from 06:00 to 22:00, every day. Each service (06:00)
    ///   writes the day's departures. A departure is open, like a bill, from its time until its
    ///   train's job is done; one not run counts against the contract as an open bill does
    ///   (ContractStanding).
    /// - **Never more than two days' worth** wait: beyond that the oldest is dropped, and counts
    ///   as a failed bill (<see cref="ContractStanding.FailedAgeDays"/>).
    /// </summary>
    public static class PassengerService
    {
        public const string Prefix = "PAX-";

        /// <summary>Trains a day departing the city, by level (1 to 5).</summary>
        public static int Trains(int level) => new[] { 0, 2, 3, 4, 6, 8 }[Math.Max(0, Math.Min(5, level))];

        public static string IdOf(string city) => Prefix + city;

        /// <summary>The city of a passenger contract's id, or null for any other contract.</summary>
        public static string CityOf(string id) =>
            id != null && id.StartsWith(Prefix, StringComparison.Ordinal) && id.Length > Prefix.Length ? id.Substring(Prefix.Length) : null;

        public const double FirstHour = 6.0, LastHour = 22.0;

        /// <summary>The hours of a day's departures at a level: evenly from 06:00, the last before 22:00.</summary>
        public static IReadOnlyList<double> Hours(int level)
        {
            var n = Trains(level);
            var hours = new List<double>();
            for (var i = 0; i < n; i++)
                hours.Add(FirstHour + i * (LastHour - FirstHour) / n);
            return hours;
        }

        /// <summary>
        /// A day's departures for a contract at a level, each due at its time (contract days: the
        /// day number plus the time of day); the oldest beyond two days' worth are dropped, their
        /// failure added to <paramref name="closed"/>. Returns how many were dropped.
        /// </summary>
        public static int MakeDue(List<double> due, int level, int day, List<double> closed)
        {
            if (due == null) throw new ArgumentNullException(nameof(due));
            foreach (var hour in Hours(level))
                due.Add(day + hour / 24.0);
            var dropped = 0;
            while (due.Count > 2 * Trains(level))
            {
                due.RemoveAt(0);
                closed?.Add(ContractStanding.FailedAgeDays);
                dropped++;
            }
            return dropped;
        }
    }
}
