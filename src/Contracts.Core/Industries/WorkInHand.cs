using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// The cars each contract still has in work when a day is planned, which its request for the
    /// day is less (owner, 2026-11-10: "it needs to come with the request of the industry. So if
    /// the industry is backed up because it's lacking supplies it needs to produce, then it's not
    /// going to request further shipments until it can actually fulfill its production").
    ///
    /// A car is in an industry's hand, by the role of its traffic there:
    /// - **materials:** a load billed to the industry, until it is unloaded there;
    /// - **products and waste:** an empty sent to load there, and then its load, until the load
    ///   is unloaded at its receiver or set out at an interchange.
    ///
    /// A load from one contracted industry to another is in both hands: a product of the one, a
    /// material of the other. Idle cars and cars set out for the pickup are in nobody's: the plan
    /// bills idle cars itself. A bridge's loads are in its hand by the interchange they are
    /// billed to, until set out there.
    /// </summary>
    public sealed class WorkInHand
    {
        private readonly Dictionary<(string Contract, string Key), int> counts = new Dictionary<(string, string), int>();

        public void Add(string industry, LineRole role) => Add(industry, role.ToString());

        public void AddBridge(string bridge, string toInterchange) => Add(bridge, "to " + toInterchange);

        private void Add(string contract, string key)
        {
            if (contract == null)
                return;
            counts[(contract, key)] = counts.TryGetValue((contract, key), out var n) ? n + 1 : 1;
        }

        /// <summary>An industry's cars in work in a role.</summary>
        public int Of(string industry, LineRole role) => industry != null && counts.TryGetValue((industry, role.ToString()), out var n) ? n : 0;

        /// <summary>A bridge's loads in work toward an interchange.</summary>
        public int OfBridge(string bridge, string toInterchange) => bridge != null && counts.TryGetValue((bridge, "to " + toInterchange), out var n) ? n : 0;

        /// <summary>The work in hand, from the contract cars as the runtime knows them (their waybills' stages).</summary>
        public static WorkInHand From(IEnumerable<BilledCar> cars, IndustryData data)
        {
            var hand = new WorkInHand();
            if (data == null)
                return hand;
            var bridges = new HashSet<string>(data.Bridges.Select(b => b.Id), StringComparer.Ordinal);
            foreach (var car in cars ?? Enumerable.Empty<BilledCar>())
            {
                if (car == null || car.Stage == CarStage.Idle || car.Stage == CarStage.OffMap)
                    continue;
                if (car.Contract != null && bridges.Contains(car.Contract))
                {
                    hand.AddBridge(car.Contract, car.Next);
                    continue;
                }
                foreach (var industry in data.Industries)
                {
                    var toHere = car.Next == industry.Id;
                    if (!toHere && car.Contract != industry.Id)
                        continue;
                    if (RoleAt(car, industry, toHere) is LineRole role)
                        hand.Add(industry.Id, role);
                }
            }
            return hand;
        }

        /// <summary>The role of a car's traffic at an industry it is bound for or billed to, or null if it isn't in the industry's hand.</summary>
        private static LineRole? RoleAt(BilledCar car, Industry industry, bool toHere)
        {
            LineRole? Shipped() => car.Cargo == null ? (LineRole?)null
                : industry.Lines.FirstOrDefault(l => l.Ships && l.Cargo.Contains(car.Cargo))?.Role;
            switch (car.Stage)
            {
                // An empty to load here: a product or waste by the cargo planned; one with no load planned waits for the next plan.
                case CarStage.ToShipper:
                case CarStage.Loading:
                case CarStage.BeingLoaded:
                    return toHere ? Shipped() : null;
                // A load: a material of the industry it goes to, a product (or waste) of the one that shipped it.
                case CarStage.ToReceiver:
                case CarStage.Unloading:
                case CarStage.BeingUnloaded:
                    return toHere ? LineRole.Material : Shipped();
                // A load on its way off the map, shipped by this industry.
                case CarStage.ToInterchange:
                    return toHere ? null : Shipped();
                default:
                    return null;
            }
        }
    }
}
