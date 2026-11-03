using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using DV;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The contract model's view of the stock map, built from the game once the stations exist:
    /// the industries from the stations' own cargo groups (Contracts.Core's catalog, design
    /// "Industries"), the interchanges from the interchange mod's job tracks, and for each cargo
    /// the car types that carry it.
    /// </summary>
    internal sealed class Economy
    {
        /// <summary>The bridge contract's id: traffic between the West and Northeast interchanges.</summary>
        public const string BridgeId = "BRIDGE";

        /// <summary>The bridge's carloads a day each way at level 5: twice an industry's main traffic (D25).</summary>
        public const int BridgeLevel5Carloads = 2 * IndustrySettings.DefaultLevel5Carloads;

        public Dictionary<string, Industry> Industries { get; private set; }

        public bool IsBridge(string id) => Data != null && Data.Bridges.Any(b => b.Id == id);
        public IndustryData Data { get; private set; }

        /// <summary>
        /// The job licenses an industry's traffic asks for (by id): each of its cargo's own, and
        /// those of the car types that carry it (LicenseRules: they gate taking its contract).
        /// </summary>
        public Dictionary<string, IReadOnlyList<string>> Licenses { get; private set; } = new Dictionary<string, IReadOnlyList<string>>();

        /// <summary>The job licenses each cargo asks for (by id): its own and those of the car types that carry it.</summary>
        public Dictionary<string, IReadOnlyList<string>> CargoLicenses { get; private set; } = new Dictionary<string, IReadOnlyList<string>>();

        /// <summary>The economy, or null when the stations or interchanges aren't there yet.</summary>
        public static Economy Build(List<string> notes)
        {
            if (StationController.allStations == null || StationController.allStations.Count == 0)
                return null;
            var groups = new List<StockCargoGroup>();
            var profile = IndustryProfile.Default();
            foreach (var station in StationController.allStations.Where(s => s != null && s.stationInfo != null))
            {
                var id = station.stationInfo.YardID;
                if (!profile.Names.ContainsKey(id))
                    profile.Names[id] = station.stationInfo.Name;
                foreach (var g in station.proceduralJobsRuleset?.outputCargoGroups ?? new List<CargoGroup>())
                    groups.Add(new StockCargoGroup(g.cargoTypes.Select(c => c.ToString()).ToList(), new[] { id },
                        g.stations.Where(s => s != null && s.stationInfo != null).Select(s => s.stationInfo.YardID).ToList()));
            }
            var industries = IndustryCatalog.Build(groups, profile).ToList();
            var interchanges = InterchangeYards.All().Keys.Select(n => new Interchange(n, InterchangeYards.StationOf(n))).ToList();
            if (interchanges.Count == 0)
            {
                notes.Add("economy: no interchange tracks (is the interchange mod installed?)");
                return new Economy { Industries = industries.ToDictionary(i => i.Id) };
            }
            var carTypes = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var cargo in industries.SelectMany(i => i.Lines).SelectMany(l => l.Cargo).Distinct())
            {
                if (!Enum.TryParse(cargo, out CargoType c) || !Globals.G.Types.CargoType_to_v2.TryGetValue(c, out var v2) || v2 == null)
                    continue;
                var types = v2.loadableCarTypes?.Select(l => l.carType).Where(t => t != null && t.liveries != null && t.liveries.Count > 0)
                    .Select(t => t.id).Distinct().ToList() ?? new List<string>();
                if (types.Count > 0)
                    carTypes[cargo] = types;
            }
            // An industry whose cargo no car carries can't be served: leave it out rather than fail the whole map.
            var served = industries.Where(i => i.Lines.SelectMany(l => l.Cargo).All(carTypes.ContainsKey)).ToList();
            foreach (var i in industries.Except(served))
                notes.Add($"economy: {i.Id} left out: no car carries some of its cargo");
            // The bridge (D25): foreign traffic between the two interchanges, both ways, in any cargo
            // the map's cars carry (only what the player may carry is billed: the planner's filter).
            // Twice an industry's main traffic: a high-volume, late-game contract (owner, 2026-11-03).
            var bridges = interchanges.Count < 2 ? new List<Bridge>()
                : new List<Bridge> { new Bridge(BridgeId, interchanges[0].Id, interchanges[1].Id, carTypes.Keys.OrderBy(c => c, StringComparer.Ordinal).ToList(),
                    BridgeLevel5Carloads) };
            var data = new IndustryData(served, interchanges, carTypes, bridges);
            notes.Add($"economy: {served.Count} industries, {interchanges.Count} interchanges ({string.Join(", ", interchanges.Select(x => $"{x.Id} at {x.Station}"))}), {carTypes.Count} cargos, {bridges.Count} bridge(s)");
            var byCargo = served.SelectMany(i => i.Lines).SelectMany(l => l.Cargo).Distinct().ToDictionary(c => c, LicensesOf);
            var licenses = served.ToDictionary(i => i.Id, i => (IReadOnlyList<string>)i.Lines.SelectMany(l => l.Cargo).SelectMany(c => byCargo[c])
                .Distinct().OrderBy(id => id, StringComparer.Ordinal).ToList());
            var gated = licenses.Where(l => l.Value.Count > 0).OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key} {string.Join("+", l.Value)}").ToList();
            notes.Add($"economy: licenses needed: {(gated.Count == 0 ? "none" : string.Join(", ", gated))}");
            return new Economy { Industries = served.ToDictionary(i => i.Id), Data = data, Licenses = licenses, CargoLicenses = byCargo };
        }

        private static IReadOnlyList<string> LicensesOf(string cargo)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (!Enum.TryParse(cargo, out CargoType c) || !Globals.G.Types.CargoType_to_v2.TryGetValue(c, out var v2) || v2 == null)
                return new List<string>();
            foreach (var l in v2.requiredJobLicenses ?? new JobLicenseType_v2[0])
                if (l != null && l.id != "Basic")
                    ids.Add(l.id);
            foreach (var type in v2.loadableCarTypes?.Select(t => t.carType).Where(t => t != null) ?? Enumerable.Empty<TrainCarType_v2>())
                foreach (var l in type.requiredJobLicenses ?? new JobLicenseType_v2[0])
                    if (l != null && l.id != "Basic")
                        ids.Add(l.id);
            return ids.OrderBy(i => i, StringComparer.Ordinal).ToList();
        }

        /// <summary>The station yard of an industry or an interchange id.</summary>
        public string StationOf(string place) =>
            place == null ? null
            : Industries.TryGetValue(place, out var i) ? i.Station
            : InterchangeYards.StationOf(place);

        public bool IsInterchange(string place) => Data != null && Data.IsInterchange(place);

        public static StationController Station(string yard) =>
            yard != null && SingletonBehaviour<LogicController>.Instance?.YardIdToStationController != null &&
            SingletonBehaviour<LogicController>.Instance.YardIdToStationController.TryGetValue(yard, out var sc) ? sc : null;
    }
}
