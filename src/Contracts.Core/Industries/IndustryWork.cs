using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>A car spotted on an industry's track: when it was spotted, and when its work ends once it has begun.</summary>
    public sealed class WorkingCar
    {
        public WorkingCar(string id, double spotted, double? ready = null)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Spotted = spotted;
            Ready = ready;
        }

        public string Id { get; }

        /// <summary>When the car was spotted, in contract days.</summary>
        public double Spotted { get; }

        /// <summary>When the industry is done with it, in contract days; set once the car ahead of it is under way.</summary>
        public double? Ready { get; set; }
    }

    /// <summary>
    /// An industry loading and unloading the cars spotted on its tracks, over time (owner,
    /// 2026-11-03, after Railroader: "some industries can take multiple days for one car if it's
    /// a very small industry ... Higher contracts would mean the industry is bigger and would
    /// process faster"). Each line works one car at a time, in the order they were spotted, at
    /// its daily carloads at the contract's level: a level 1 coal mine's 7 loads of coal a day
    /// take about 3.4 hours a car. A finished car goes out at once (owner, 2026-11-04: "add it to
    /// the outbound as soon as it's ready").
    /// </summary>
    public static class IndustryWork
    {
        /// <summary>The slowest a line works: a car in four days.</summary>
        public const double MinCarsPerDay = 0.25;

        /// <summary>
        /// The line a car is worked on: a line that ships its cargo when it is loaded there, one
        /// that takes it in when it is unloaded; null if the industry has none.
        /// </summary>
        public static CargoLine LineFor(Industry industry, string cargo, bool loading)
        {
            if (industry == null || cargo == null)
                return null;
            return industry.Lines.FirstOrDefault(l => l.Ships == loading && l.Cargo.Contains(cargo));
        }

        /// <summary>
        /// Cars a day a line works at a level: its share of its role's daily carloads, by weight,
        /// unrounded (so a line due under one car a day works a car in more than a day), but never
        /// fewer than the whole carloads it is billed each day (Industry.Split), so its queue
        /// can't grow from one day to the next; and at least <see cref="MinCarsPerDay"/>. A
        /// contract not yet started (level 0) works at level 1.
        /// </summary>
        public static double CarsPerDay(Industry industry, CargoLine line, int level)
        {
            if (industry == null) throw new ArgumentNullException(nameof(industry));
            if (line == null) throw new ArgumentNullException(nameof(line));
            level = Math.Max(1, Math.Min(5, level));
            var lines = industry.LinesOf(line.Role).ToList();
            var weights = lines.Sum(l => l.Weight);
            var share = weights > 0 ? industry.Daily(line.Role, level) * line.Weight / weights : 0.0;
            var billed = industry.Split(line.Role, level).Where(s => ReferenceEquals(s.Line, line)).Select(s => s.Carloads).DefaultIfEmpty(0).Max();
            return Math.Max(MinCarsPerDay, Math.Max(share, billed));
        }

        /// <summary>
        /// A line's queue brought up to <paramref name="now"/>: each car not yet given its ready
        /// time gets it, starting when it was spotted or when the car ahead is done, whichever is
        /// later; cars go in the order they were spotted (then by id). Returns the cars done by
        /// now, in that order. A car's ready time, once set, stays, so a level changed later
        /// changes only the cars spotted after it.
        /// </summary>
        public static List<WorkingCar> Advance(IEnumerable<WorkingCar> queue, double daysPerCar, double now)
        {
            if (daysPerCar <= 0) throw new ArgumentOutOfRangeException(nameof(daysPerCar), "a car takes some time");
            var cars = (queue ?? Enumerable.Empty<WorkingCar>()).Where(c => c != null)
                .OrderBy(c => c.Spotted).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            var free = double.NegativeInfinity;
            foreach (var car in cars)
            {
                if (car.Ready == null)
                    car.Ready = Math.Max(car.Spotted, free) + daysPerCar;
                free = Math.Max(free, car.Ready.Value);
            }
            return cars.Where(c => c.Ready.Value <= now + 1e-9).ToList();
        }

        /// <summary>Days per car at a rate of cars a day.</summary>
        public static double DaysPerCar(double carsPerDay) => 1.0 / Math.Max(MinCarsPerDay, carsPerDay);

        /// <summary>
        /// When a car will be ready, said against the clock now (both in contract days): "ready
        /// 14:20", "ready tomorrow 02:10", "ready in 3 days", or "ready now".
        /// </summary>
        public static string When(double ready, double now)
        {
            if (ready <= now)
                return "ready now";
            var days = (int)Math.Floor(ready) - (int)Math.Floor(now);
            if (days >= 2)
                return $"ready in {days} days";
            var minutes = (int)Math.Round((ready - Math.Floor(ready)) * 24 * 60);
            if (minutes >= 24 * 60)
                minutes = 24 * 60 - 1;
            var time = $"{minutes / 60:00}:{minutes % 60:00}";
            return days == 1 ? $"ready tomorrow {time}" : $"ready {time}";
        }
    }
}
