using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// The interchange service again, for the cars it had no room for (owner, 2026-11-10: "if an
    /// interchange is full it needs to attempt serving again later"; design, "Interchange
    /// service"). A cut goes only on an interchange track with no car on it, so a day's orders
    /// can be left over.
    ///
    /// - **What is left** waits off the map: each order's cars not yet placed.
    /// - **The service comes again** every <see cref="Hours"/> game hours while any wait: it takes
    ///   away the cars set out for pickup, then places what now fits.
    /// - **The next day's 06:00 service** plans the day afresh, so what still waits then is
    ///   dropped: the backlog never grows past a day's orders.
    /// - **A contract no longer held** gets none of its waiting cars.
    /// </summary>
    public static class ServiceRetry
    {
        /// <summary>Game hours from a service that left cars waiting to the next try (Railroader's extra service).</summary>
        public const double Hours = 2.5;

        /// <summary>When the service tries again after one that ended now, in contract days.</summary>
        public static double NextAt(double now) => now + Hours / 24.0;

        /// <summary>Whether a retry is due: cars wait, and its time has come.</summary>
        public static bool Due(int waiting, double? retryAt, double? now) =>
            waiting > 0 && retryAt.HasValue && now.HasValue && now.Value >= retryAt.Value;

        /// <summary>What a service leaves waiting: each order with the cars it placed none of yet, as orders of their own.</summary>
        public static List<Order> Left(IEnumerable<(Order Order, int Placed)> orders)
        {
            var left = new List<Order>();
            foreach (var (order, placed) in orders ?? Enumerable.Empty<(Order, int)>())
            {
                var count = order == null ? 0 : order.Count - Math.Max(0, placed);
                if (count <= 0)
                    continue;
                left.Add(new Order
                {
                    Interchange = order.Interchange, Contract = order.Contract, CarType = order.CarType, Cargo = order.Cargo,
                    BilledTo = order.BilledTo, Then = order.Then, Loaded = order.Loaded, Count = count,
                });
            }
            return left;
        }

        /// <summary>The waiting orders of contracts still held.</summary>
        public static List<Order> Held(IEnumerable<Order> waiting, Func<string, bool> held) =>
            (waiting ?? Enumerable.Empty<Order>()).Where(o => o != null && o.Count > 0 && held(o.Contract)).ToList();

        /// <summary>The cars waiting, all orders together.</summary>
        public static int Cars(IEnumerable<Order> waiting) => (waiting ?? Enumerable.Empty<Order>()).Sum(o => Math.Max(0, o?.Count ?? 0));

        /// <summary>
        /// The kiosk's page on the cars waiting: how many at each interchange (by the name
        /// <paramref name="nameOf"/> gives it), when the service comes again, and how to make
        /// room; null with none waiting.
        /// </summary>
        public static string Page(IEnumerable<Order> waiting, Func<string, string> nameOf, double? retryAt, double? now)
        {
            var places = (waiting ?? Enumerable.Empty<Order>()).Where(o => o != null && o.Count > 0)
                .GroupBy(o => nameOf(o.Interchange) ?? o.Interchange, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Sum(o => o.Count)} at {g.Key}").ToList();
            if (places.Count == 0)
                return null;
            var again = retryAt.HasValue && now.HasValue ? $" The service comes again {At(retryAt.Value, now.Value)}." : "";
            return $"New cars wait off the map for a free interchange track: {string.Join(", ", places)}.{again}\n\n" +
                   "A track is free when no car stands on it. Each time the service comes it takes away the cars set out for pickup, " +
                   "then places the cars that fit. Take your cars off an interchange track to make room sooner.\n\n" +
                   "Cars still waiting at the next 06:00 service are dropped: that service brings the new day's cars.";
        }

        /// <summary>A time said against the clock now (both in contract days): "at 08:30", "tomorrow at 02:10", or "now".</summary>
        public static string At(double when, double now)
        {
            if (when <= now)
                return "now";
            var minutes = (int)Math.Round((when - Math.Floor(when)) * 24 * 60);
            var day = (int)Math.Floor(when);
            if (minutes >= 24 * 60)
            {
                minutes = 0;
                day++;
            }
            var time = $"{minutes / 60:00}:{minutes % 60:00}";
            return day > (int)Math.Floor(now) ? $"tomorrow at {time}" : $"at {time}";
        }
    }
}
