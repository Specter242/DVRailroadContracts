using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Mod.Kiosk;
using DV;

namespace Contracts.Mod.Spike
{
    /// <summary>
    /// Spike S6's stand-in for the contract model: the industries from the game's own cargo
    /// groups (built by Contracts.Core's catalog, as the model will be), and the player's
    /// contracts held in memory (not saved). It shows what the kiosk menu will show; the
    /// model replaces it in phase 3.
    ///
    /// A station's kiosk offers the contracts of that station's industries (a large station
    /// holds several), and those of the military stations without an office of their own
    /// (HMB's at HB, MFMB's at MF). The bridge
    /// contract is offered at the interchange offices, or at MF and IME, the stand-ins, when
    /// the interchange mod is not installed.
    /// </summary>
    internal sealed class SpikeDesk : IContractDesk
    {
        private static readonly Dictionary<string, string> Adopted = new Dictionary<string, string> { ["HB"] = "HMB", ["MF"] = "MFMB" };
        private const string Bridge = "bridge:MF West:IME West";
        private const int ShortLength = 24;

        private readonly List<ContractView> mine = new List<ContractView>();
        private Dictionary<string, Industry> industries;

        /// <summary>The industries, built once the stations exist.</summary>
        internal IReadOnlyDictionary<string, Industry> Industries
        {
            get
            {
                if (industries != null || StationController.allStations == null || StationController.allStations.Count == 0)
                    return industries ?? new Dictionary<string, Industry>();
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
                industries = IndustryCatalog.Build(groups, profile).ToDictionary(i => i.Id);
                Main.Log($"spike desk: {industries.Count} industries from {groups.Count} stock cargo groups");
                return industries;
            }
        }

        public IReadOnlyList<ContractView> Offers(KioskPlace place)
        {
            var offers = new List<ContractView>();
            if (place == null)
                return offers;
            var bridgeHere = place.IsInterchange || !KioskPlaces.AnyInterchangeOffice() && (place.Id == "MF" || place.Id == "IME");
            if (bridgeHere)
                offers.Add(new ContractView { Id = Bridge, Short = "WI-NEI bridge", IsOffer = true });
            if (!place.IsInterchange)
            {
                var here = new List<string> { place.Id };
                if (Adopted.TryGetValue(place.Id, out var adopted))
                    here.Add(adopted);
                foreach (var station in here)
                    foreach (var industry in Industries.Values.Where(i => i.Station == station).OrderBy(i => i.Id, StringComparer.Ordinal))
                        offers.Add(new ContractView { Id = industry.Id, Short = Clip($"{station} {industry.Name}"), IsOffer = true });
            }
            offers.RemoveAll(o => mine.Any(m => m.Id == o.Id));
            return offers;
        }

        private static string Clip(string s) => s.Length <= ShortLength ? s : s.Substring(0, ShortLength - 1).TrimEnd() + ".";

        public IReadOnlyList<ContractView> Mine() => mine;

        public string Volume(ContractView contract, int level)
        {
            if (contract.Id == Bridge)
            {
                var each = IndustrySettings.DailyCarloads(IndustrySettings.DefaultLevel5Carloads, level);
                return $"{each} + {each}";
            }
            return Industries.TryGetValue(contract.Id, out var i)
                ? $"{i.Daily(LineRole.Product, level)} / {i.Daily(LineRole.Material, level)} / {i.Daily(LineRole.Waste, level)}"
                : "";
        }

        public string Legend(ContractView contract) =>
            contract.Id == Bridge ? "Cars a day: each way" : "Cars a day: out / in / waste";

        public void Take(ContractView offer, int level)
        {
            if (mine.Any(m => m.Id == offer.Id))
                return;
            // A new contract starts at midnight, like a level change.
            mine.Add(new ContractView
            {
                Id = offer.Id, Short = offer.Short, IsOffer = false,
                Level = 0, RequestedLevel = level, Performance = null, OpenWaybills = 0, OnOrder = 0,
            });
            Main.Log($"spike desk: took {offer.Id} at level {level}");
        }

        public void RequestLevel(ContractView contract, int level)
        {
            contract.RequestedLevel = level;
            Main.Log($"spike desk: {contract.Id} asks for level {level} from midnight");
        }

        public string RaiseBlocker(ContractView contract) => null;

        public void End(ContractView contract)
        {
            mine.Remove(contract);
            Main.Log($"spike desk: ended {contract.Id}");
        }

        public string Notice
        {
            get
            {
                var minutes = Globals.G?.GameParams?.DayLengthInMinutes ?? 0f;
                return minutes > 0f && minutes < 24f * 60f - 1f
                    ? $"Days last {minutes / 60f:0.#} h: set 24 h"
                    : null;
            }
        }

        /// <summary>An industry's lines, for the spike's log.</summary>
        internal string Describe(string id) =>
            Industries.TryGetValue(id, out var i)
                ? $"{i.Id} ({i.Name}, reckoned on {i.Primary}): " + string.Join("; ", i.Lines.Select(l =>
                    $"{l.Role} {string.Join("/", l.Cargo.Take(3))}{(l.Cargo.Count > 3 ? $" +{l.Cargo.Count - 3}" : "")} {(l.Ships ? "to" : "from")} {string.Join(",", l.Partners)}"))
                : $"{id}: no industry";
    }
}
