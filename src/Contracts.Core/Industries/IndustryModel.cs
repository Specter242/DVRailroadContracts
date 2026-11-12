using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>What a line of an industry's traffic is (design.md, "Industry contracts", D11).</summary>
    public enum LineRole
    {
        /// <summary>What the industry makes and ships: coal at a coal mine.</summary>
        Product,

        /// <summary>What the industry takes in to work: mining machines and tools at a coal mine.</summary>
        Material,

        /// <summary>What it ships off as scrap or byproduct: scrap metal at a coal mine.</summary>
        Waste,
    }

    /// <summary>
    /// One line of an industry's traffic: a group of cargo it ships (product or waste) or
    /// takes in (material), the stations it trades that cargo with on the map (its stock
    /// consumers or suppliers), and its weight within its role's volume.
    /// </summary>
    public sealed class CargoLine
    {
        public CargoLine(LineRole role, IReadOnlyList<string> cargo, IReadOnlyList<string> partners, double weight = 1.0)
        {
            if (cargo == null || cargo.Count == 0) throw new ArgumentException("a line needs cargo", nameof(cargo));
            if (weight <= 0) throw new ArgumentException("a line's weight must be positive", nameof(weight));
            Role = role;
            Cargo = cargo;
            Partners = partners ?? Array.Empty<string>();
            Weight = weight;
        }

        public LineRole Role { get; }
        public IReadOnlyList<string> Cargo { get; }

        /// <summary>For a product or waste, the stations that take it; for a material, those that supply it.</summary>
        public IReadOnlyList<string> Partners { get; }

        public double Weight { get; }
        public bool Ships => Role != LineRole.Material;

        public override string ToString() => $"{Role} {string.Join("/", Cargo)}";
    }

    /// <summary>
    /// An industry a contract is taken with, and all of its traffic: a station, or one of the
    /// industries a large station is split into. The contract's level scales every line at once.
    /// Its lines' partners are stations.
    /// </summary>
    public sealed class Industry
    {
        public Industry(string id, string name, IReadOnlyList<CargoLine> lines, LineRole primary,
            int level5Primary = IndustrySettings.DefaultLevel5Carloads, string station = null)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("an industry needs an id", nameof(id));
            if (lines == null || lines.Count == 0) throw new ArgumentException($"industry {id} has no traffic", nameof(lines));
            if (primary == LineRole.Waste) throw new ArgumentException($"industry {id}: waste can't be its main traffic", nameof(primary));
            if (lines.All(l => l.Role != primary)) throw new ArgumentException($"industry {id} has no {primary} line for its main traffic");
            Id = id;
            Name = name ?? id;
            Station = station ?? id;
            Lines = lines;
            Primary = primary;
            Level5Primary = level5Primary;
        }

        public string Id { get; }
        public string Name { get; }

        /// <summary>The station it stands at; a large station holds several industries (design.md, "Industries").</summary>
        public string Station { get; }

        public IReadOnlyList<CargoLine> Lines { get; }

        /// <summary>The traffic its level is reckoned on: products for a producer, materials for a consumer such as a power plant or a city.</summary>
        public LineRole Primary { get; }

        /// <summary>Carloads a day of its main traffic at level 5 (D5).</summary>
        public int Level5Primary { get; }

        public IEnumerable<CargoLine> LinesOf(LineRole role) => Lines.Where(l => l.Role == role);
        public bool Has(LineRole role) => Lines.Any(l => l.Role == role);

        /// <summary>Carloads a day of a role at level 5: the main traffic in full, the others at their ratios.</summary>
        public int Level5(LineRole role)
        {
            if (!Has(role)) return 0;
            if (role == Primary) return Level5Primary;
            return (int)Math.Floor(Level5Primary * IndustrySettings.Ratio(role) + 0.5);
        }

        /// <summary>Carloads a day of a role at a level.</summary>
        public int Daily(LineRole role, int level) => IndustrySettings.DailyCarloads(Level5(role), level);

        /// <summary>A role's carloads at a level, shared among its lines by weight, earlier lines first: its standing split.</summary>
        public IReadOnlyList<(CargoLine Line, int Carloads)> Split(LineRole role, int level) => Share(role, Daily(role, level), 0);

        /// <summary>
        /// A number of a role's carloads shared among its lines by weight. Remainders go to the
        /// largest fractions; among equal ones the turn passes on a line each day
        /// (<paramref name="day"/>), so a role with more lines than carloads serves them all in
        /// turn. With earlier lines always first, the last lines got nothing every day: the
        /// Machine Factory's town takes ten goods and is due seven carloads at level 1 (owner,
        /// 2026-11-10: "let's fix the uneven cargo split").
        /// </summary>
        public IReadOnlyList<(CargoLine Line, int Carloads)> Share(LineRole role, int carloads, int day)
        {
            var lines = LinesOf(role).ToList();
            var total = Math.Max(0, carloads);
            if (lines.Count == 0 || total == 0)
                return lines.Select(l => (l, 0)).ToList();
            var weights = lines.Sum(l => l.Weight);
            var exact = lines.Select(l => total * l.Weight / weights).ToList();
            var counts = exact.Select(e => (int)Math.Floor(e)).ToList();
            var left = total - counts.Sum();
            var n = lines.Count;
            var first = ((day % n) + n) % n;
            foreach (var i in Enumerable.Range(0, n).OrderByDescending(i => Math.Round(exact[i] - counts[i], 9)).ThenBy(i => (i - first + n) % n).Take(left))
                counts[i]++;
            return lines.Select((l, i) => (l, counts[i])).ToList();
        }
    }

    /// <summary>An interchange: the only place cars are created or removed (design.md, "Cars").</summary>
    public sealed class Interchange
    {
        public Interchange(string id, string station)
        {
            Id = id;
            Station = station;
        }

        public string Id { get; }

        /// <summary>The station whose yard the interchange tracks belong to, for job anchoring.</summary>
        public string Station { get; }
    }

    /// <summary>
    /// Traffic between two interchanges, both ways (design.md, "Bridge contracts"): foreign loads
    /// arrive at one, billed to the other, a level's carloads a day each way.
    /// </summary>
    public sealed class Bridge
    {
        public Bridge(string id, string from, string to, IReadOnlyList<string> cargo,
            int level5Carloads = IndustrySettings.DefaultLevel5Carloads)
        {
            if (from == to) throw new ArgumentException($"bridge {id} starts and ends at {from}", nameof(to));
            if (cargo == null || cargo.Count == 0) throw new ArgumentException($"bridge {id} has no cargo", nameof(cargo));
            Id = id;
            From = from;
            To = to;
            Cargo = cargo;
            Level5Carloads = level5Carloads;
        }

        public string Id { get; }
        public string From { get; }
        public string To { get; }

        /// <summary>The cargo the foreign loads are drawn from, at random; each comes in the first car type that carries it.</summary>
        public IReadOnlyList<string> Cargo { get; }

        public int Level5Carloads { get; }
    }

    public sealed class IndustrySettings
    {
        /// <summary>One and a half times the largest stock job of 20 cars (design.md, "Contract volume").</summary>
        public const int DefaultLevel5Carloads = 30;

        /// <summary>Railroader's contract factors for levels 1–5.</summary>
        public static readonly IReadOnlyList<double> LevelFactors = new[] { 0.24, 0.34, 0.49, 0.70, 1.0 };

        /// <summary>
        /// A role's volume against the main traffic, when it isn't the main traffic: materials
        /// half, a consumer's own products a third, waste a sixth (design.md, "Contract volume").
        /// </summary>
        public static double Ratio(LineRole role) =>
            role == LineRole.Material ? 0.5 : role == LineRole.Product ? 1.0 / 3.0 : 1.0 / 6.0;

        public static double Factor(int level) => level < 1 ? 0.0 : LevelFactors[Math.Min(5, level) - 1];

        /// <summary>Carloads per contract day at <paramref name="level"/>, rounded half up.</summary>
        public static int DailyCarloads(int level5Carloads, int level) =>
            level < 1 ? 0 : (int)Math.Floor(level5Carloads * Factor(level) + 0.5);
    }

    /// <summary>The industries, the interchanges, the bridges, and which car types carry each cargo.</summary>
    public sealed class IndustryData
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> carTypes;

        public IndustryData(IReadOnlyList<Industry> industries, IReadOnlyList<Interchange> interchanges,
            IReadOnlyDictionary<string, IReadOnlyList<string>> carTypesByCargo, IReadOnlyList<Bridge> bridges = null,
            IndustrySettings settings = null)
        {
            if (interchanges == null || interchanges.Count == 0)
                throw new ArgumentException("at least one interchange is needed", nameof(interchanges));
            Industries = industries ?? Array.Empty<Industry>();
            Interchanges = interchanges;
            Bridges = bridges ?? Array.Empty<Bridge>();
            Settings = settings ?? new IndustrySettings();
            carTypes = carTypesByCargo ?? new Dictionary<string, IReadOnlyList<string>>();
            var ids = new HashSet<string>();
            foreach (var i in Industries)
            {
                if (!ids.Add(i.Id)) throw new ArgumentException($"duplicate industry id {i.Id}");
                foreach (var cargo in i.Lines.SelectMany(l => l.Cargo))
                    if (CarTypes(cargo).Count == 0) throw new ArgumentException($"industry {i.Id}: no car type carries {cargo}");
            }
            foreach (var b in Bridges)
            {
                if (!ids.Add(b.Id)) throw new ArgumentException($"duplicate contract id {b.Id}");
                if (!IsInterchange(b.From) || !IsInterchange(b.To))
                    throw new ArgumentException($"bridge {b.Id} must run between interchanges");
                foreach (var cargo in b.Cargo)
                    if (CarTypes(cargo).Count == 0) throw new ArgumentException($"bridge {b.Id}: no car type carries {cargo}");
            }
        }

        public IReadOnlyList<Industry> Industries { get; }
        public IReadOnlyList<Interchange> Interchanges { get; }
        public IReadOnlyList<Bridge> Bridges { get; }
        public IndustrySettings Settings { get; }

        /// <summary>Car types that can carry a cargo; the first is ordered when the interchange must supply.</summary>
        public IReadOnlyList<string> CarTypes(string cargo) =>
            cargo != null && carTypes.TryGetValue(cargo, out var types) ? types : Array.Empty<string>();

        public Industry Industry(string id) => Industries.FirstOrDefault(i => i.Id == id);

        public bool IsInterchange(string id) => Interchanges.Any(i => i.Id == id);
    }

    /// <summary>
    /// What the planner needs to know about a car on the map. The runtime
    /// builds these from the cars and the saved waybills.
    /// </summary>
    public sealed class FleetCar
    {
        public string Guid { get; set; }
        public string CarType { get; set; }

        /// <summary>Station or interchange the car stands at, or is billed toward if it is moving.</summary>
        public string Location { get; set; }

        /// <summary>Cargo key, or null when empty.</summary>
        public string Cargo { get; set; }

        /// <summary>True when the car already has work (an open waybill leg or a stock job); the planner leaves it alone.</summary>
        public bool Busy { get; set; }

        public bool Loaded => Cargo != null;
    }
}
