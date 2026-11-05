using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Shop
{
    /// <summary>What a vehicle in the kiosk's shop is, for its price.</summary>
    public enum VehicleKind
    {
        /// <summary>A steam locomotive, with its tender if it has one.</summary>
        Steam,
        /// <summary>Any other locomotive: diesel, electric, battery, the handcar.</summary>
        Locomotive,
        Caboose,
        Coach,
    }

    /// <summary>A row of the shop: its kind, whether it is the game's own, its weight, and the game's value of it.</summary>
    public sealed class Vehicle
    {
        public Vehicle(string name, VehicleKind kind, bool stock, double mass, double value)
        {
            Name = name ?? "?";
            Kind = kind;
            Stock = stock;
            Mass = mass;
            Value = value;
        }

        public string Name { get; }
        public VehicleKind Kind { get; }

        /// <summary>One of the game's own vehicles, not a custom one.</summary>
        public bool Stock { get; }

        /// <summary>Its cars' weight together, in the game's unit.</summary>
        public double Mass { get; }

        /// <summary>The game's value of it: what it charges to rebuild its cars' bodies, wheels and powertrains.</summary>
        public double Value { get; }
    }

    /// <summary>
    /// What the shop charges (owner, 2026-11-04: "the Hudson is over a million dollars. I agree the
    /// coaches could be cheaper").
    /// - The game's own locomotives and cabooses cost the game's value of them.
    /// - Coaches cost a third of theirs: a coach is valued almost wholly on its body, and at full
    ///   value one cost about as much as a DE2, a train of four more than a DE6.
    /// - A custom vehicle costs what the game's own vehicles of its kind cost per unit of weight,
    ///   times its weight: steam like the S282 and S060, other locomotives like the stock diesels,
    ///   coaches like the stock coaches. A custom vehicle's own values are its author's, and can be
    ///   anything (a Hudson's came to over a million). One that weighs nothing costs its own value.
    /// Prices are rounded to $500, and are at least $500.
    /// </summary>
    public static class VehiclePricing
    {
        public const double CoachShare = 1.0 / 3.0;
        public const double Step = 500.0;

        /// <summary>A stock vehicle's price before rounding: its value, a coach's a third of it.</summary>
        private static double StockPrice(Vehicle v) => v.Kind == VehicleKind.Coach ? v.Value * CoachShare : v.Value;

        /// <summary>
        /// The price per unit of weight of the stock vehicles of a kind (their prices over their
        /// weights, together); steam with no stock steam goes as other locomotives; null with none.
        /// </summary>
        public static double? Rate(VehicleKind kind, IEnumerable<Vehicle> stock)
        {
            var list = (stock ?? Enumerable.Empty<Vehicle>()).Where(v => v != null && v.Stock && v.Mass > 0 && v.Value > 0).ToList();
            var of = list.Where(v => v.Kind == kind).ToList();
            if (of.Count == 0 && kind == VehicleKind.Steam)
                of = list.Where(v => v.Kind == VehicleKind.Locomotive).ToList();
            var mass = of.Sum(v => v.Mass);
            return mass > 0 ? of.Sum(StockPrice) / mass : (double?)null;
        }

        /// <summary>A vehicle's price, given the stock vehicles the shop sells.</summary>
        public static double Price(Vehicle vehicle, IReadOnlyCollection<Vehicle> stock)
        {
            if (vehicle == null) throw new ArgumentNullException(nameof(vehicle));
            double raw;
            if (vehicle.Stock || vehicle.Mass <= 0)
                raw = vehicle.Stock ? StockPrice(vehicle) : vehicle.Value;
            else
                raw = Rate(vehicle.Kind, stock) is double rate ? rate * vehicle.Mass : vehicle.Value;
            return Math.Max(Step, Math.Round(raw / Step, MidpointRounding.AwayFromZero) * Step);
        }
    }
}
