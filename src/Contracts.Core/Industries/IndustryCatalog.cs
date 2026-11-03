using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// A stock cargo group as the runtime reads it from the game: the cargo, the stations that
    /// ship it, and the stations that take it. Persistent Jobs dumps the same groups.
    /// </summary>
    public sealed class StockCargoGroup
    {
        public StockCargoGroup(IReadOnlyList<string> cargo, IReadOnlyList<string> sources, IReadOnlyList<string> destinations)
        {
            Cargo = cargo ?? Array.Empty<string>();
            Sources = sources ?? Array.Empty<string>();
            Destinations = destinations ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> Cargo { get; }
        public IReadOnlyList<string> Sources { get; }
        public IReadOnlyList<string> Destinations { get; }
    }

    /// <summary>
    /// One industry of a station that is split into several (owner, 2026-10-31: "split large
    /// destinations"): the cargo it ships and the cargo it takes in. A stock group goes to the
    /// first part whose cargo covers all of the group's; a group no part covers is split cargo
    /// by cargo; what no part names goes to the station's last part.
    /// </summary>
    public sealed class IndustryPart
    {
        public IndustryPart(string id, string name, IEnumerable<string> ships, IEnumerable<string> takes, bool consumer = false)
        {
            Id = id;
            Name = name;
            Ships = new HashSet<string>(ships ?? Array.Empty<string>());
            Takes = new HashSet<string>(takes ?? Array.Empty<string>());
            Consumer = consumer;
        }

        public string Id { get; }
        public string Name { get; }
        public ISet<string> Ships { get; }
        public ISet<string> Takes { get; }

        /// <summary>Reckoned on what it takes in, though it ships something too (a town).</summary>
        public bool Consumer { get; }
    }

    /// <summary>
    /// The choices the stock data doesn't make (design.md, "Industries"): which cargo is waste,
    /// which industries are reckoned on what they take in, the waste the stock game leaves out
    /// (owner, 2026-10-31: "coal mine will have coal production out, require materials in, and
    /// waste out"), and how the large stations split into industries.
    /// </summary>
    public sealed class IndustryProfile
    {
        /// <summary>Cargo that is scrap or a byproduct when an industry ships it.</summary>
        public ISet<string> WasteCargo { get; } = new HashSet<string>
        {
            "ScrapMetal", "ScrapWood", "ScrapContainers", "WoodChips", "SpentNuclearFuel", "Biohazard",
        };

        /// <summary>Unsplit stations reckoned on what they take in, though they ship something too: City West.</summary>
        public ISet<string> Consumers { get; } = new HashSet<string> { "CW" };

        /// <summary>Waste lines added to the stock ones: the mines' worn machinery, as scrap metal to the Steel Mill.</summary>
        public IList<(string Industry, string Cargo, string To)> ExtraWaste { get; } = new List<(string, string, string)>
        {
            ("CME", "ScrapMetal", "SM"), ("CMS", "ScrapMetal", "SM"),
            ("IME", "ScrapMetal", "SM"), ("IMW", "ScrapMetal", "SM"),
        };

        /// <summary>
        /// Short names for the kiosk's list column, shown after the station's id, for stations
        /// that are one industry; the stock names ("Machine Factory &amp; Town") are too long.
        /// The id stands in when missing.
        /// </summary>
        public IDictionary<string, string> Names { get; } = new Dictionary<string, string>
        {
            ["CME"] = "Coal Mine East", ["CMS"] = "Coal Mine South", ["CP"] = "Coal Power Plant",
            ["CW"] = "City West", ["FM"] = "Farm", ["FRC"] = "Forest Central", ["FRS"] = "Forest South",
            ["HMB"] = "Harbor Military", ["IME"] = "Iron Mine East", ["IMW"] = "Iron Mine West",
            ["MB"] = "Military Base", ["MFMB"] = "Factory Military", ["OR"] = "Oil Refinery",
            ["OWC"] = "Oil Well Central", ["OWN"] = "Oil Well North", ["SM"] = "Steel Mill", ["SW"] = "Sawmill",
        };

        private static readonly string[] Steel = { "SteelRolls", "SteelBillets", "SteelSlabs", "SteelBentPlates" };
        private static readonly string[] ImportedGoods =
        {
            "ElectronicsKrugmann", "ElectronicsAAG", "ElectronicsNovae", "ElectronicsTraeg",
            "ToolsBrohm", "ToolsAAG", "ToolsNovae", "ToolsTraeg", "ChemicalsSperex",
        };
        private static readonly string[] Vehicles = { "NewCars", "CityBuses", "Trams", "SemiTrailers", "Tractors", "ForestryTrailers" };
        private static readonly string[] Machines = { "Excavators", "MiningTrucks", "CraneParts" };
        private static readonly string[] Scrap = { "ScrapMetal", "ScrapWood" };

        /// <summary>What a town takes in: food, produce, fuel, gas, cars and buses, and consumer goods.</summary>
        public static readonly string[] TownGoods = new[]
        {
            "Bread", "DairyProducts", "MeatProducts", "CannedFood", "CatFood", "Alcohol",
            "Eggs", "TemperateFruits", "Vegetables", "Fish", "TropicalFruits",
            "Diesel", "Gasoline", "Methane", "NewCars", "CityBuses", "ImportedNewCars", "Medicine",
            "ClothingNeoGamma", "ClothingNovae", "ClothingTraeg", "ClothingObco", "Furniture",
            "ElectronicsIskar", "ToolsIskar", "ChemicalsIskar",
        }.Concat(ImportedGoods).ToArray();

        /// <summary>
        /// The large stations, each split into the industries it holds (design.md, "Industries"):
        /// Harbor into its oil terminal, gas plant, import and export docks and town; the three
        /// factory towns into factory and town; City South into city and fishery. The last part
        /// of each takes what the others don't.
        /// </summary>
        public IDictionary<string, IList<IndustryPart>> Splits { get; } = new Dictionary<string, IList<IndustryPart>>
        {
            ["HB"] = new List<IndustryPart>
            {
                new IndustryPart("HB-OIL", "Oil Terminal", new[] { "Diesel", "Gasoline" }, new[] { "CrudeOil", "Diesel", "Gasoline" }),
                new IndustryPart("HB-GAS", "Gas Plant",
                    new[] { "Acetylene", "CryoHydrogen", "Ammonia", "SodiumHydroxide", "Argon", "CryoOxygen", "Nitrogen", "AmmoniumNitrate" },
                    new[] { "Methane" }),
                new IndustryPart("HB-IMPORT", "Imports",
                    ImportedGoods.Concat(new[] { "ClothingNeoGamma", "ClothingNovae", "ClothingTraeg", "Medicine", "ImportedNewCars", "TropicalFruits", "Fish", "ForestryTrailers" })
                        .Concat(Machines), null),
                new IndustryPart("HB-EXPORT", "Exports", new[] { "ScrapContainers" },
                    Vehicles.Concat(Machines).Concat(Steel).Concat(new[]
                    {
                        "SteelRails", "Plywood", "Boards", "Sleepers", "Pipes", "Flour", "CannedFood", "CatFood", "Alcohol",
                        "ElectronicsIskar", "ToolsIskar", "ChemicalsIskar", "Furniture", "ClothingObco",
                    }), consumer: true),
                new IndustryPart("HB-TOWN", "Town", Scrap, TownGoods, consumer: true),
            },
            ["MF"] = new List<IndustryPart>
            {
                new IndustryPart("MF", "Machine Factory", Vehicles.Concat(Machines).Concat(Scrap),
                    Steel.Concat(ImportedGoods).Concat(new[] { "Boards", "Acetylene", "CryoOxygen", "ElectronicsIskar", "ToolsIskar", "ChemicalsIskar" })),
                new IndustryPart("MF-TOWN", "Town", null, TownGoods, consumer: true),
            },
            ["GF"] = new List<IndustryPart>
            {
                new IndustryPart("GF", "Goods Factory",
                    new[] { "ElectronicsIskar", "ToolsIskar", "ChemicalsIskar", "Furniture", "ClothingObco", "Pipes" }.Concat(Scrap),
                    Steel.Concat(ImportedGoods).Concat(new[] { "Plywood", "Boards", "WoodChips", "Cotton", "Wool", "CryoHydrogen", "Ammonia", "SodiumHydroxide" })),
                new IndustryPart("GF-TOWN", "Town", null, TownGoods, consumer: true),
            },
            ["FF"] = new List<IndustryPart>
            {
                new IndustryPart("FF", "Food Factory",
                    new[] { "Bread", "DairyProducts", "MeatProducts", "CannedFood", "CatFood", "Alcohol" }.Concat(Scrap),
                    new[]
                    {
                        "Pigs", "Cows", "Poultry", "Sheep", "Goats", "Wheat", "Corn", "SunflowerSeeds", "TemperateFruits",
                        "Vegetables", "Milk", "Eggs", "Flour", "Nitrogen", "Fish",
                    }),
                new IndustryPart("FF-TOWN", "Town", null, TownGoods, consumer: true),
            },
            ["CS"] = new List<IndustryPart>
            {
                new IndustryPart("CS-FISH", "Fishery", new[] { "Fish" }, null),
                new IndustryPart("CS", "City South", Scrap, TownGoods, consumer: true),
            },
        };

        public static IndustryProfile Default() => new IndustryProfile();
    }

    /// <summary>
    /// Builds the industries from the stock cargo groups (design.md, "Industries"). Each station
    /// is one industry, or several when the profile splits it. What an industry ships is a
    /// product or waste line, what it takes in a material line. Groups with the same cargo
    /// merge into one line, with all their stations as partners. Groups of empty containers
    /// (cargo named "Empty...") are car moves, not traffic, and are left out: the planner moves
    /// empties itself.
    /// </summary>
    public static class IndustryCatalog
    {
        private sealed class Draft
        {
            public IndustryPart Part;
            public readonly List<(LineRole Role, List<string> Cargo, List<string> Partners)> Lines =
                new List<(LineRole, List<string>, List<string>)>();

            public void Add(string station, LineRole role, IEnumerable<string> cargo, IEnumerable<string> partners)
            {
                var key = cargo.ToList();
                var line = Lines.FirstOrDefault(l => l.Role == role && l.Cargo.SequenceEqual(key));
                if (line.Cargo == null)
                    Lines.Add((role, key, partners.Where(p => p != station).Distinct().ToList()));
                else
                    line.Partners.AddRange(partners.Where(p => p != station && !line.Partners.Contains(p)).Distinct());
            }
        }

        public static IReadOnlyList<Industry> Build(IEnumerable<StockCargoGroup> groups, IndustryProfile profile = null)
        {
            profile = profile ?? IndustryProfile.Default();
            var traffic = groups.Where(g => g.Cargo.Count > 0 && !g.Cargo.All(c => c.StartsWith("Empty", StringComparison.Ordinal))).ToList();
            var stations = traffic.SelectMany(g => g.Sources.Concat(g.Destinations))
                .Concat(profile.ExtraWaste.SelectMany(w => new[] { w.Industry, w.To }))
                .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            var industries = new List<Industry>();
            foreach (var station in stations)
            {
                var name = profile.Names.TryGetValue(station, out var n) ? n : station;
                var split = profile.Splits.TryGetValue(station, out var parts) && parts.Count > 0;
                var drafts = split
                    ? parts.Select(p => new Draft { Part = p }).ToList()
                    : new List<Draft> { new Draft { Part = new IndustryPart(station, name, null, null, profile.Consumers.Contains(station)) } };

                void Add(LineRole role, IReadOnlyList<string> cargo, IEnumerable<string> partners)
                {
                    if (!split)
                    {
                        drafts[0].Add(station, role, cargo, partners);
                        return;
                    }
                    ISet<string> Set(Draft d) => role == LineRole.Material ? d.Part.Takes : d.Part.Ships;
                    var whole = drafts.FirstOrDefault(d => cargo.All(Set(d).Contains));
                    if (whole != null)
                    {
                        whole.Add(station, role, cargo, partners);
                        return;
                    }
                    var list = partners.ToList();
                    foreach (var piece in cargo.GroupBy(c => drafts.FirstOrDefault(d => Set(d).Contains(c)) ?? drafts[drafts.Count - 1]))
                        piece.Key.Add(station, role, piece.ToList(), list);
                }

                foreach (var g in traffic.Where(g => g.Sources.Contains(station)))
                    Add(g.Cargo.All(profile.WasteCargo.Contains) ? LineRole.Waste : LineRole.Product, g.Cargo, g.Destinations);
                foreach (var g in traffic.Where(g => g.Destinations.Contains(station)))
                    Add(LineRole.Material, g.Cargo, g.Sources);
                foreach (var w in profile.ExtraWaste.Where(w => w.Industry == station))
                    Add(LineRole.Waste, new[] { w.Cargo }, new[] { w.To });
                foreach (var w in profile.ExtraWaste.Where(w => w.To == station))
                    Add(LineRole.Material, new[] { w.Cargo }, new[] { w.Industry });

                foreach (var d in drafts)
                {
                    // An industry that ships only scrap and takes nothing has no contract to offer.
                    if (d.Lines.Count == 0 || d.Lines.All(l => l.Role == LineRole.Waste))
                        continue;
                    var hasProducts = d.Lines.Any(l => l.Role == LineRole.Product);
                    var primary = hasProducts && !d.Part.Consumer ? LineRole.Product
                        : d.Lines.Any(l => l.Role == LineRole.Material) ? LineRole.Material : LineRole.Product;
                    industries.Add(new Industry(d.Part.Id, d.Part.Name,
                        d.Lines.Select(l => new CargoLine(l.Role, l.Cargo, l.Partners)).ToList(), primary, station: station));
                }
            }
            return industries;
        }
    }
}
