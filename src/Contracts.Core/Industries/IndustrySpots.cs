using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// Some of a station's tracks where its industry works some of its cargo: the tracks as the
    /// game names them within the station ("B7S"), the cargo, and what is there.
    /// </summary>
    public sealed class Spot
    {
        public Spot(string station, IReadOnlyList<string> tracks, IReadOnlyList<string> cargo, string what)
        {
            Station = station ?? throw new ArgumentNullException(nameof(station));
            Tracks = tracks ?? Array.Empty<string>();
            Cargo = cargo ?? Array.Empty<string>();
            What = what ?? "";
        }

        public string Station { get; }
        public IReadOnlyList<string> Tracks { get; }
        public IReadOnlyList<string> Cargo { get; }
        public string What { get; }
    }

    /// <summary>
    /// Where an industry works each cargo (owner, 2026-11-05: at the Steel Mill "the S-B tracks
    /// are under the dock crane. The A5S is at the factory dock", while the stock jobs load and
    /// unload everything on one track, as they were made around a warehouse machine for the
    /// whole industry). A cargo a spot names goes to that spot's tracks; any other goes to the
    /// tracks of the station's warehouse machines, as in the stock game. The stations' tracks
    /// were found with the survey (Contracts.Mod Runtime/Survey, design "Industry spots").
    /// </summary>
    public static class IndustrySpots
    {
        private static readonly string[] Tools = { "ToolsIskar", "ToolsBrohm", "ToolsAAG", "ToolsNovae", "ToolsTraeg" };
        private static readonly string[] MineMaterials = { "Excavators", "MiningTrucks", "CraneParts", "ToolsIskar", "ToolsBrohm", "ToolsAAG", "ToolsNovae", "ToolsTraeg" };
        private static readonly string[] Steel = { "SteelRolls", "SteelBillets", "SteelSlabs", "SteelBentPlates" };
        private static readonly string[] Grain = { "Wheat", "Corn", "SunflowerSeeds", "Flour" };
        private static readonly string[] Lumber = { "Plywood", "Boards", "Sleepers" };
        private static readonly string[] Military = { "Ammunition", "Tanks", "MilitaryCars", "MilitaryTrucks", "MilitarySupplies", "AttackHelicopters", "Missiles", "SpentNuclearFuel", "Biohazard" };
        private static readonly string[] Vehicles = { "NewCars", "ImportedNewCars", "CityBuses", "Trams", "SemiTrailers", "Tractors", "Excavators", "MiningTrucks", "CraneParts", "ForestryTrailers" };
        private static readonly string[] Liquids =
            { "CrudeOil", "Diesel", "Gasoline", "Methane", "Alcohol", "CryoHydrogen", "CryoOxygen", "Nitrogen", "Argon", "Acetylene", "Ammonia", "SodiumHydroxide" };
        private static readonly string[] Goods =
        {
            "ElectronicsIskar", "ElectronicsKrugmann", "ElectronicsAAG", "ElectronicsNovae", "ElectronicsTraeg", "ClothingObco", "ClothingNeoGamma",
            "ClothingNovae", "ClothingTraeg", "Furniture", "ChemicalsIskar", "ChemicalsSperex", "Medicine", "ToolsIskar", "ToolsBrohm", "ToolsAAG",
            "ToolsNovae", "ToolsTraeg", "Bread", "DairyProducts", "MeatProducts", "CannedFood", "CatFood", "Eggs", "TemperateFruits", "TropicalFruits",
            "Vegetables", "Fish", "Cotton", "Wool",
        };

        private static string[] Of(params string[][] groups) => groups.SelectMany(g => g).Distinct().ToArray();

        /// <summary>
        /// The spots, from the survey of 2026-11-05 (results/survey-all1: each station's tracks, what
        /// stands beside them, and views from above). A cargo is named only at a station whose
        /// industries carry it. City South, City West, Oil Well Central and the Machine Factory's
        /// military base have one industry track for everything, and no spots.
        /// </summary>
        public static readonly IReadOnlyList<Spot> All = new[]
        {
            new Spot("CME", new[] { "B5S" }, MineMaterials, "the mine's warehouse"),
            new Spot("CMS", new[] { "A1S" }, MineMaterials, "beside the mine's buildings"),
            new Spot("CP", new[] { "A4S", "A5S" }, Tools, "the depot"),
            new Spot("FF", new[] { "D4S" }, Grain, "the factory's silos"),
            new Spot("FM", new[] { "A3L" }, Grain, "the grain silos"),
            new Spot("FRC", new[] { "C1S" }, new[] { "Logs" }, "the log pile"),
            new Spot("FRS", new[] { "B1L", "B7S" }, new[] { "Logs" }, "the log piles"),
            new Spot("GF", new[] { "B1L", "B2S", "B3S" }, Of(Steel, new[] { "Plywood", "Boards", "Pipes", "NewCars", "ImportedNewCars", "CityBuses" }), "under the portal crane"),
            new Spot("GF", new[] { "D1S" }, Goods, "the warehouse dock"),
            new Spot("HB", new[] { "C3L", "C1S" }, Liquids, "the oil terminal"),
            new Spot("HB", new[] { "D7L", "D5S" }, Of(Goods.Where(g => g != "Cotton" && g != "Wool").ToArray(), Steel, Lumber, Vehicles,
                new[] { "SteelRails", "Pipes", "ScrapMetal", "ScrapWood", "ScrapContainers" }), "under the portal cranes, by the containers"),
            new Spot("HMB", new[] { "B1L", "B3S" }, Military, "under the gantry crane"),
            new Spot("IME", new[] { "C1S" }, MineMaterials, "along the mine's sheds"),
            new Spot("IMW", new[] { "B8L", "B7S" }, new[] { "IronOre" }, "the ore piles and the loader"),
            new Spot("IMW", new[] { "B1S" }, MineMaterials, "the mine's sheds"),
            new Spot("MB", new[] { "A1L", "A2S" }, Military, "the underground depot"),
            new Spot("MF", new[] { "C1L", "C2S", "B1S" }, Of(Steel, new[] { "Boards" }, Vehicles), "under the portal crane"),
            new Spot("OR", new[] { "A1L", "A2S", "A3S" }, new[] { "CrudeOil", "Diesel", "Gasoline", "Methane" }, "the loading racks"),
            new Spot("OR", new[] { "A4S" }, Tools, "the depot"),
            new Spot("OWN", new[] { "B2S" }, Tools, "the depot"),
            // Steel under the portal crane (owner, 2026-11-05); the ore and coal stay on A7L, at the
            // ore pile and the coaling tower.
            new Spot("SM", new[] { "B7S", "B8S" }, Of(Steel, new[] { "SteelRails" }), "under the portal crane"),
            new Spot("SM", new[] { "A5S" }, Of(Tools, new[] { "ScrapContainers", "Argon", "CryoOxygen" }), "the factory dock"),
            new Spot("SW", new[] { "B1S", "C4S" }, Lumber, "the lumber sheds"),
        };

        /// <summary>
        /// The tracks at a station (as "B7S") where its industry works a cargo's cars, in the order
        /// the spots list them; empty if no spot there names the cargo (the warehouse machines'
        /// tracks serve, as in the stock game).
        /// </summary>
        public static IReadOnlyList<string> TracksFor(string station, string cargo, IEnumerable<Spot> spots = null)
        {
            if (station == null || cargo == null)
                return Array.Empty<string>();
            return (spots ?? All).Where(s => s.Station == station && s.Cargo.Contains(cargo))
                .SelectMany(s => s.Tracks).Distinct().ToList();
        }

        /// <summary>Every spot track of a station: its industry's, so not storage for the cars the mod moves out of the way.</summary>
        public static IReadOnlyCollection<string> TracksAt(string station, IEnumerable<Spot> spots = null) =>
            new HashSet<string>((spots ?? All).Where(s => s.Station == station).SelectMany(s => s.Tracks), StringComparer.Ordinal);
    }
}
