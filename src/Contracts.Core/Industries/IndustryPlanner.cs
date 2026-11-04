using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Sim;

namespace Contracts.Core.Industries
{
    /// <summary>A bill the planner writes for a car already on the map.</summary>
    public sealed class Assignment
    {
        public string Guid { get; set; }
        public AssignmentKind Kind { get; set; }

        /// <summary>Where the car goes next: a shipper to load at, a receiver, or an interchange.</summary>
        public string Destination { get; set; }

        /// <summary>For an empty sent to load: where the load goes once loaded (a receiver or an interchange).</summary>
        public string Then { get; set; }

        /// <summary>The cargo it carries or will be loaded with; null for a return.</summary>
        public string Cargo { get; set; }

        /// <summary>The contracted industry that ships it, if any.</summary>
        public string Shipper { get; set; }

        /// <summary>The contracted industry that takes it, if any.</summary>
        public string Receiver { get; set; }
    }

    public enum AssignmentKind
    {
        /// <summary>An empty sent to a shipper for loading.</summary>
        EmptyToShipper,

        /// <summary>A load already on the map, billed to a receiver that wants it (adopted cars, D3).</summary>
        LoadToReceiver,

        /// <summary>An empty nobody needs, returned off the map.</summary>
        ReturnEmpty,

        /// <summary>A load no contract wants, exported off the map (draining, D3).</summary>
        ExportLoad,
    }

    /// <summary>Cars the interchange service must place at its next run.</summary>
    public sealed class Order
    {
        public string Interchange { get; set; }

        /// <summary>The industry or bridge contract the cars are for.</summary>
        public string Contract { get; set; }

        public string CarType { get; set; }
        public string Cargo { get; set; }

        /// <summary>The cars' first destination: a shipper to load at, a receiver, or the far interchange of a bridge.</summary>
        public string BilledTo { get; set; }

        /// <summary>For empties ordered to load: where the loads go (a receiver or an interchange).</summary>
        public string Then { get; set; }

        /// <summary>True when the cars arrive loaded (imports and bridge traffic).</summary>
        public bool Loaded { get; set; }

        public int Count { get; set; }
    }

    /// <summary>One industry's day: what it is due, and how its traffic was routed.</summary>
    public sealed class IndustryDay
    {
        public Dictionary<LineRole, int> Due { get; } = new Dictionary<LineRole, int>();

        /// <summary>Loads it ships to a contracted industry on the map.</summary>
        public int ShippedOnMap { get; set; }

        /// <summary>Loads it ships off the map through an interchange.</summary>
        public int Exported { get; set; }

        /// <summary>Loads it takes from a contracted industry on the map (or a load already on the map).</summary>
        public int ReceivedOnMap { get; set; }

        /// <summary>Loads it takes from off the map, arriving loaded at an interchange.</summary>
        public int Imported { get; set; }
    }

    public sealed class DayPlan
    {
        public List<Assignment> Assignments { get; } = new List<Assignment>();
        public List<Order> Orders { get; } = new List<Order>();

        /// <summary>Each contracted industry's day, by industry id.</summary>
        public Dictionary<string, IndustryDay> Industries { get; } = new Dictionary<string, IndustryDay>();

        /// <summary>Each bridge's loads today, both ways, by bridge id.</summary>
        public Dictionary<string, int> Bridges { get; } = new Dictionary<string, int>();

        public int Due(string industry, LineRole role) =>
            Industries.TryGetValue(industry, out var d) && d.Due.TryGetValue(role, out var n) ? n : 0;

        public int Ordered(string contract, bool loaded) => Orders.Where(o => o.Contract == contract && o.Loaded == loaded).Sum(o => o.Count);
    }

    /// <summary>
    /// Plans one contract day for industry contracts (design.md, "Industry contracts", D11).
    ///
    /// Each contracted industry is due its level's carloads of every line: products and waste
    /// to ship, materials to take in. Contracted industries trade with each other first: a load
    /// one ships goes to another that takes that cargo from it, while that one still wants
    /// some today. What no contracted industry takes is exported through an interchange; what
    /// no contracted industry ships is imported through one, arriving loaded. Interchanges are
    /// chosen at random (D8).
    ///
    /// Loads already standing idle on the map go first to a contracted industry that wants
    /// them, else off the map (D3). Every load to ship needs an empty: idle empties at the
    /// shipper first, then empties elsewhere, one per industry in turn, then empties ordered at
    /// an interchange. Idle empties nobody wants go back to an interchange. Bridges order
    /// loaded foreign cars from one interchange to the other.
    ///
    /// Only cargo the railroad may carry is billed (<c>carries</c>; design D19: the player's
    /// licenses). A line with none of it is not due today; a load of it standing idle goes off
    /// the map.
    /// </summary>
    public static class IndustryPlanner
    {
        private sealed class Need
        {
            public Industry Industry;
            public CargoLine Line;
            public int Left;
        }

        private sealed class Load
        {
            public Industry Shipper;
            public string Cargo;
            public string To;
            public Industry Receiver;
        }

        public static DayPlan PlanDay(IndustryData data, IReadOnlyDictionary<string, int> levels, IReadOnlyList<FleetCar> fleet,
            SplitMix64 rng, Func<string, bool> carries = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            levels = levels ?? new Dictionary<string, int>();
            fleet = fleet ?? Array.Empty<FleetCar>();
            var plan = new DayPlan();
            int LevelOf(string id) => levels.TryGetValue(id, out var l) ? Math.Max(0, Math.Min(5, l)) : 0;
            bool Carried(string cargo) => carries == null || carries(cargo);
            List<string> CargoOf(CargoLine line) => line.Cargo.Where(Carried).ToList();

            // Today's dues, line by line.
            var active = data.Industries.Where(i => LevelOf(i.Id) > 0).ToList();
            var supply = new List<Need>();
            var demand = new List<Need>();
            foreach (var industry in active)
            {
                var day = plan.Industries[industry.Id] = new IndustryDay();
                foreach (LineRole role in Enum.GetValues(typeof(LineRole)))
                {
                    var split = industry.Split(role, LevelOf(industry.Id)).Where(s => CargoOf(s.Line).Count > 0).ToList();
                    day.Due[role] = split.Sum(s => s.Carloads);
                    foreach (var (line, carloads) in split.Where(s => s.Carloads > 0))
                        (line.Ships ? supply : demand).Add(new Need { Industry = industry, Line = line, Left = carloads });
                }
            }

            // A receiver for a cargo shipped by a shipper (or found on the map, with no shipper):
            // a contracted industry that takes that cargo from the shipper's station, and wants more today.
            Need ReceiverFor(string cargo, Industry shipper)
            {
                if (!Carried(cargo))
                    return null;
                var wanting = demand.Where(d => d.Left > 0 && d.Industry != shipper && d.Line.Cargo.Contains(cargo)
                                                && (shipper == null || d.Line.Partners.Contains(shipper.Station))).ToList();
                return wanting.Count == 0 ? null : wanting[rng.Next(wanting.Count)];
            }

            // Loads standing idle on the map: to a contracted industry that wants them, else off the map.
            var idle = fleet.Where(c => !c.Busy).OrderBy(c => c.Guid, StringComparer.Ordinal).ToList();
            foreach (var car in idle.Where(c => c.Loaded))
            {
                var receiver = ReceiverFor(car.Cargo, null);
                if (receiver != null)
                {
                    receiver.Left--;
                    plan.Industries[receiver.Industry.Id].ReceivedOnMap++;
                    plan.Assignments.Add(new Assignment
                    {
                        Guid = car.Guid, Kind = AssignmentKind.LoadToReceiver, Destination = receiver.Industry.Id,
                        Cargo = car.Cargo, Receiver = receiver.Industry.Id,
                    });
                }
                else
                {
                    plan.Assignments.Add(new Assignment
                    {
                        Guid = car.Guid, Kind = AssignmentKind.ExportLoad, Destination = PickInterchange(data, rng), Cargo = car.Cargo,
                    });
                }
            }

            // Contracted industries' loads: to another contracted industry, one load per line in turn, else exported.
            var loads = new List<Load>();
            var shipping = supply.ToList();
            rng.Shuffle(shipping);
            for (var progress = true; progress;)
            {
                progress = false;
                foreach (var s in shipping.Where(s => s.Left > 0))
                {
                    var choices = CargoOf(s.Line);
                    var cargo = choices[rng.Next(choices.Count)];
                    var receiver = ReceiverFor(cargo, s.Industry);
                    if (receiver == null)
                    {
                        // A cargo the line holds that some receiver wants, if the random one isn't.
                        foreach (var other in choices)
                            if ((receiver = ReceiverFor(other, s.Industry)) != null)
                            {
                                cargo = other;
                                break;
                            }
                    }
                    s.Left--;
                    progress = true;
                    if (receiver != null)
                    {
                        receiver.Left--;
                        plan.Industries[s.Industry.Id].ShippedOnMap++;
                        plan.Industries[receiver.Industry.Id].ReceivedOnMap++;
                        loads.Add(new Load { Shipper = s.Industry, Cargo = cargo, To = receiver.Industry.Id, Receiver = receiver.Industry });
                    }
                    else
                    {
                        plan.Industries[s.Industry.Id].Exported++;
                        loads.Add(new Load { Shipper = s.Industry, Cargo = cargo, To = PickInterchange(data, rng) });
                    }
                }
            }

            // What no contracted industry ships arrives loaded from off the map.
            var imports = new Dictionary<(string Interchange, string Industry, string Cargo), int>();
            foreach (var d in demand.Where(d => d.Left > 0))
            {
                plan.Industries[d.Industry.Id].Imported += d.Left;
                var choices = CargoOf(d.Line);
                for (; d.Left > 0; d.Left--)
                {
                    var key = (PickInterchange(data, rng), d.Industry.Id, choices[rng.Next(choices.Count)]);
                    imports[key] = imports.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }
            foreach (var kv in imports.OrderBy(k => k.Key.Industry, StringComparer.Ordinal).ThenBy(k => k.Key.Cargo, StringComparer.Ordinal)
                         .ThenBy(k => k.Key.Interchange, StringComparer.Ordinal))
                plan.Orders.Add(new Order
                {
                    Interchange = kv.Key.Interchange, Contract = kv.Key.Industry, Cargo = kv.Key.Cargo,
                    CarType = data.CarTypes(kv.Key.Cargo)[0], BilledTo = kv.Key.Industry, Loaded = true, Count = kv.Value,
                });

            // Empties for the loads: the shipper's own first, then the rest, one load per shipper in turn.
            var empties = idle.Where(c => !c.Loaded).ToList();
            var used = new HashSet<string>();
            var waiting = loads.ToList();
            void Allocate(Func<Load, FleetCar, bool> eligible)
            {
                var shippers = waiting.Select(l => l.Shipper).Distinct().ToList();
                rng.Shuffle(shippers);
                for (var progress = true; progress;)
                {
                    progress = false;
                    foreach (var shipper in shippers)
                    {
                        foreach (var load in waiting.Where(l => l.Shipper == shipper))
                        {
                            var types = data.CarTypes(load.Cargo);
                            var car = empties.FirstOrDefault(c => !used.Contains(c.Guid) && types.Contains(c.CarType) && eligible(load, c));
                            if (car == null)
                                continue;
                            used.Add(car.Guid);
                            waiting.Remove(load);
                            progress = true;
                            plan.Assignments.Add(new Assignment
                            {
                                Guid = car.Guid, Kind = AssignmentKind.EmptyToShipper, Destination = load.Shipper.Id, Then = load.To,
                                Cargo = load.Cargo, Shipper = load.Shipper.Id, Receiver = load.Receiver?.Id,
                            });
                            break;
                        }
                    }
                }
            }
            Allocate((l, c) => c.Location == l.Shipper.Id);
            Allocate((l, c) => true);

            // Loads still without a car: empties ordered at a random interchange, billed to the shipper.
            var ordered = new Dictionary<(string Interchange, string Shipper, string Cargo, string Then), int>();
            foreach (var load in waiting)
            {
                var key = (PickInterchange(data, rng), load.Shipper.Id, load.Cargo, load.To);
                ordered[key] = ordered.TryGetValue(key, out var n) ? n + 1 : 1;
            }
            foreach (var kv in ordered.OrderBy(k => k.Key.Shipper, StringComparer.Ordinal).ThenBy(k => k.Key.Cargo, StringComparer.Ordinal)
                         .ThenBy(k => k.Key.Then, StringComparer.Ordinal).ThenBy(k => k.Key.Interchange, StringComparer.Ordinal))
                plan.Orders.Add(new Order
                {
                    Interchange = kv.Key.Interchange, Contract = kv.Key.Shipper, Cargo = kv.Key.Cargo,
                    CarType = data.CarTypes(kv.Key.Cargo)[0], BilledTo = kv.Key.Shipper, Then = kv.Key.Then, Count = kv.Value,
                });

            // Empties nobody wants leave the map.
            foreach (var car in empties.Where(c => !used.Contains(c.Guid)))
            {
                var home = data.IsInterchange(car.Location) ? car.Location : PickInterchange(data, rng);
                plan.Assignments.Add(new Assignment { Guid = car.Guid, Kind = AssignmentKind.ReturnEmpty, Destination = home });
            }

            // Bridge traffic arrives loaded at one interchange, billed to the other, each way: cargo
            // the railroad may carry, each in the first car type that carries it.
            foreach (var b in data.Bridges)
            {
                var level = LevelOf(b.Id);
                var cargo = b.Cargo.Where(Carried).ToList();
                if (level < 1 || cargo.Count == 0) continue;
                var due = IndustrySettings.DailyCarloads(b.Level5Carloads, level);
                plan.Bridges[b.Id] = 2 * due;
                foreach (var (from, to) in new[] { (b.From, b.To), (b.To, b.From) })
                {
                    var byCargo = new Dictionary<string, int>();
                    for (var i = 0; i < due; i++)
                    {
                        var c = cargo[rng.Next(cargo.Count)];
                        byCargo[c] = byCargo.TryGetValue(c, out var n) ? n + 1 : 1;
                    }
                    foreach (var kv in byCargo.OrderBy(k => k.Key, StringComparer.Ordinal))
                        plan.Orders.Add(new Order
                        {
                            Interchange = from, Contract = b.Id, Cargo = kv.Key, CarType = data.CarTypes(kv.Key)[0],
                            BilledTo = to, Loaded = true, Count = kv.Value,
                        });
                }
            }
            return plan;
        }

        /// <summary>
        /// Where an empty freed at an unloader goes, at once, rather than at the next service
        /// (design.md, "Industry work"; owner, 2026-11-04: "add it to the outbound as soon as it's
        /// ready"): to a contracted industry that loads its car type with cargo the railroad may
        /// carry, the one that freed it first, then one at the same station, then any; with none,
        /// back to a random interchange. It is sent with no load planned: it waits at the shipper
        /// for the next service, which loads the shipper's own empties first.
        /// </summary>
        public static Assignment RouteFreedEmpty(IndustryData data, IReadOnlyDictionary<string, int> levels, string guid, string carType,
            string at, SplitMix64 rng, Func<string, bool> carries = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            levels = levels ?? new Dictionary<string, int>();
            bool Active(Industry i) => levels.TryGetValue(i.Id, out var l) && l >= 1;
            bool Loads(Industry i) => i.Lines.Any(line => line.Ships && line.Cargo.Any(c => (carries == null || carries(c)) && data.CarTypes(c).Contains(carType)));
            var shippers = data.Industries.Where(i => Active(i) && Loads(i)).ToList();
            var station = data.Industries.FirstOrDefault(i => i.Id == at)?.Station;
            var shipper = shippers.FirstOrDefault(i => i.Id == at)
                          ?? shippers.Where(i => station != null && i.Station == station).OrderBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault()
                          ?? (shippers.Count > 0 ? shippers.OrderBy(i => i.Id, StringComparer.Ordinal).ToList()[rng.Next(shippers.Count)] : null);
            return shipper != null
                ? new Assignment { Guid = guid, Kind = AssignmentKind.EmptyToShipper, Destination = shipper.Id, Shipper = shipper.Id }
                : new Assignment { Guid = guid, Kind = AssignmentKind.ReturnEmpty, Destination = PickInterchange(data, rng) };
        }

        private static string PickInterchange(IndustryData data, SplitMix64 rng) => rng.Pick(data.Interchanges).Id;
    }
}
