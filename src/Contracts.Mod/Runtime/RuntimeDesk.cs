using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Mod.Kiosk;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The kiosk's Contracts menu on the running model: offers from the economy, the player's
    /// contracts from the saved state. A contract taken starts at level 1 at midnight (owner,
    /// 2026-11-01: "industries should start at level 1"), and moves a level at a time; a raise
    /// needs consistent performance (owner, 2026-11-02; Contracts.Core ContractStanding). A
    /// contract can be taken only with the licenses its traffic needs (LicenseRules, D19).
    /// Ending a contract leaves its cars to finish their legs and drain through the interchanges.
    /// Lowering or ending one costs the penalty (D7, ContractTerms).
    /// </summary>
    internal sealed class RuntimeDesk : IContractDesk
    {
        private static readonly Dictionary<string, string> Adopted = new Dictionary<string, string> { ["HB"] = "HMB", ["MF"] = "MFMB" };
        private const int ShortLength = 24;
        public const string ShopId = "#locomotives";
        public const string CoachShopId = "#coaches";
        /// <summary>The list's "Cars without jobs" row (owner, 2026-11-03: cars on an interchange with no job number).</summary>
        public const string JoblessId = "#jobless";

        /// <summary>Whether a list entry is a contract (not a shop or the jobless cars' row).</summary>
        public static bool IsContract(string id) => ShopOf(id) == null && id != JoblessId;

        /// <summary>The shop a menu entry opens, or null for a contract.</summary>
        public static LocoShop.Kind? ShopOf(string id) =>
            id == ShopId ? LocoShop.Kind.Locomotives : id == CoachShopId ? LocoShop.Kind.Coaches : (LocoShop.Kind?)null;

        private static ContractRuntime Rt => ContractRuntime.Instance;

        public static bool IsPassenger(string id) => PassengerService.CityOf(id) != null;

        /// <summary>Whether a desk is the spike's stand-in (development builds only).</summary>
        public static bool IsStandIn(IContractDesk desk) =>
#if DEVTOOLS
            desk is Spike.SpikeDesk;
#else
            false;
#endif

        private static IReadOnlyDictionary<string, Industry> Industries =>
            Rt?.Economy?.Industries ?? (IReadOnlyDictionary<string, Industry>)new Dictionary<string, Industry>();

        /// <summary>
        /// Every industry's contract not held, at every kiosk (owner, 2026-11-03: "manage
        /// contracts without having to travel all over the map"): this place's own first (at an
        /// interchange office, the station's beside it), then those the player may take, then those locked
        /// for want of licenses.
        /// </summary>
        public IReadOnlyList<ContractView> Offers(KioskPlace place)
        {
            if (Rt == null)
                return new List<ContractView>();
            var here = new HashSet<string>(StringComparer.Ordinal);
            var station = place == null ? null : place.IsInterchange ? InterchangeYards.NeighborOf(place.Id) : place.Id;
            if (station != null)
            {
                here.Add(station);
                if (Adopted.TryGetValue(station, out var adopted))
                    here.Add(adopted);
            }
            // A city's passenger service is an offer of its own (D13), after its industries; the
            // bridge (D25) comes first at an interchange office.
            var all = Industries.Values.Select(i => (Station: i.Station, Id: i.Id, Short: $"{i.Station} {i.Name}"))
                .Concat(Passengers.Cities().Select(c => (Station: c, Id: PassengerService.IdOf(c), Short: $"{c} Passengers")))
                .Concat((Rt.Economy?.Data?.Bridges ?? new List<Bridge>()).Select(b => (Station: (string)null, Id: b.Id, Short: "WI-NEI Bridge")));
            var atInterchange = place != null && place.IsInterchange;
            return all
                .Where(o => Rt.State.Contracts.All(m => m.Id != o.Id))
                .Select(o => (o.Station, o.Id, View: new ContractView { Id = o.Id, Short = Clip(o.Short), IsOffer = true, Needs = LicenseRules.Blocker(o.Id) }))
                .OrderBy(o => (o.Station == null ? atInterchange : here.Contains(o.Station)) ? 0 : o.View.Needs == null ? 1 : 2)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .Select(o => o.View)
                .ToList();
        }

        private static string Clip(string s) => s.Length <= ShortLength ? s : s.Substring(0, ShortLength - 1).TrimEnd() + ".";

        public IReadOnlyList<ContractView> Mine()
        {
            if (Rt == null)
                return new List<ContractView>();
            return Rt.State.Contracts.Select(c => new ContractView
            {
                Id = c.Id, Short = c.Short ?? c.Id, IsOffer = false, Level = c.Level, RequestedLevel = c.Requested,
                Performance = (float?)ContractStanding.Average(c.History),
                OpenWaybills = PassengerService.CityOf(c.Id) != null ? c.Due.Count + c.Running.Count
                    : Rt.State.Waybills.Count(w => w.Contract == c.Id && w.Stage != Stage.Idle && w.Stage != Stage.OffMap),
                OnOrder = 0,
                Needs = LicenseRules.Blocker(c.Id),
            }).ToList();
        }

        public string Volume(ContractView contract, int level) =>
            PassengerService.CityOf(contract.Id) != null ? $"{PassengerService.Trains(level)} trains"
            : Rt?.Economy?.Data?.Bridges.FirstOrDefault(b => b.Id == contract.Id) is Bridge bridge
                ? $"{IndustrySettings.DailyCarloads(bridge.Level5Carloads, level)} + {IndustrySettings.DailyCarloads(bridge.Level5Carloads, level)}"
            : Industries.TryGetValue(contract.Id, out var i)
                ? $"{i.Daily(LineRole.Product, level)} / {i.Daily(LineRole.Material, level)} / {i.Daily(LineRole.Waste, level)}"
                : "";

        public (int Count, string Text) Jobless()
        {
            var cars = Rt?.Jobless() ?? new List<JoblessCar>();
            return (cars.Count, JoblessReport.Paragraph(cars, namesPerGroup: 6));
        }

        public string Legend(ContractView contract) =>
            PassengerService.CityOf(contract.Id) != null ? "Trains a day from the city"
            : Rt?.Economy?.IsBridge(contract.Id) == true ? "Cars a day: WI to NEI + back"
            : "Cars a day: out / in / waste";

        public void Take(ContractView offer, int level)
        {
            if (Rt == null || Rt.State.Contracts.Any(m => m.Id == offer.Id))
                return;
            // Licenses gate the contract, so none of its bills asks for one the player lacks (D19).
            var needs = LicenseRules.Blocker(offer.Id);
            if (needs != null)
            {
                Main.Log($"contracts: {offer.Id} not taken: {needs}");
                return;
            }
            Rt.State.Contracts.Add(new HeldContract { Id = offer.Id, Short = offer.Short, Level = 0, Requested = 1, SinceDay = Rt.State.Day });
            Main.Log($"contracts: took {offer.Id}; it starts at level 1 at midnight");
        }

        public void RequestLevel(ContractView contract, int level)
        {
            var held = Rt?.State.Contracts.FirstOrDefault(c => c.Id == contract.Id);
            if (held == null)
                return;
            level = Math.Max(1, Math.Min(5, level));
            if (level > held.Level && held.Level >= 1)
            {
                var blocker = ContractStanding.RaiseBlocker(held.Level, held.History);
                if (blocker != null)
                {
                    Main.Log($"contracts: {held.Id} can't be raised: {blocker}");
                    return;
                }
                level = held.Level + 1;
            }
            held.Requested = level;
            Main.Log($"contracts: {held.Id} asks for level {held.Requested} from midnight");
        }

        public string RaiseBlocker(ContractView contract)
        {
            var held = Rt?.State.Contracts.FirstOrDefault(c => c.Id == contract.Id);
            return held == null ? "Not your contract" : ContractStanding.RaiseBlocker(held.Level, held.History);
        }

        /// <summary>Ending a contract takes effect now, with its ending penalty (none before it starts).</summary>
        public void End(ContractView contract)
        {
            var held = Rt?.State.Contracts.FirstOrDefault(c => c.Id == contract.Id);
            if (held == null)
                return;
            var penalty = ContractTerms.Penalty(held.Level, 0, held.History.Count);
            Rt.State.Contracts.Remove(held);
            Main.Log($"contracts: ended {contract.Id}; its cars finish their legs and drain");
            if (penalty > 0.0)
                ContractPay.Charge(Rt.State, penalty, $"ending {held.Id} at level {held.Level}");
        }

        public double Penalty(ContractView contract, int target)
        {
            var held = Rt?.State.Contracts.FirstOrDefault(c => c.Id == contract.Id);
            return held == null ? 0.0 : ContractTerms.Penalty(held.Level, target, held.History.Count);
        }

        public double Owed => Rt?.State.Owed ?? 0.0;

        public string Notice
        {
            get
            {
                if (Rt == null)
                    return "Contracts are not running in this game";
                var minutes = DV.Globals.G?.GameParams?.DayLengthInMinutes ?? 0f;
                return minutes > 0f && minutes < 24f * 60f - 1f ? $"Days last {minutes / 60f:0.#} h: set 24 h" : null;
            }
        }
    }
}
