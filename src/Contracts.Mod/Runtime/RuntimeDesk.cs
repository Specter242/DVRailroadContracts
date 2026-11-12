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
        public const string WaitingId = "#waiting";
        /// <summary>The Contracts list's row for selling bought vehicles back (VehicleSales).</summary>
        public const string SellId = "#sell";
        /// <summary>The Contracts list's row on the day length, while days are shorter than 24 hours (Core DayLength).</summary>
        public const string DayLengthId = "#daylength";

        /// <summary>Whether a list entry is a contract (not a shop, the jobless cars' row or the day length's).</summary>
        public static bool IsContract(string id) => ShopOf(id) == null && id != JoblessId && id != WaitingId && id != OwedId && id != HoldId && id != SellId && id != DayLengthId;

        /// <summary>The shop a menu entry opens, or null for a contract.</summary>
        public static LocoShop.Kind? ShopOf(string id) =>
            id == ShopId ? LocoShop.Kind.Locomotives : id == CoachShopId ? LocoShop.Kind.Coaches : (LocoShop.Kind?)null;

        public const string OwedId = "#owed";
        public const string HoldId = "#hold";

        private static ContractRuntime Rt => ContractRuntime.Instance;

        /// <summary>Off only for the probe, whose own checks take contracts the old way (at midnight) and run their services themselves.</summary>
        internal static bool OpensAtOnce = true;

        /// <summary>
        /// Why a contract can't be taken, or null: the licenses its cargo asks for (D19), and for
        /// the bridge, a contract at level 3 first (Contracts.Core ContractTerms.BridgeBlocker;
        /// owner, 2026-11-10: "the bridge contract needs to be a later game contract"). A bridge
        /// already held runs on.
        /// </summary>
        public static string Blocker(string id) =>
            (Rt?.OnHold != null ? ContractTerms.HoldLine : null)
            ?? LicenseRules.Blocker(id)
            ?? (Rt?.Economy?.IsBridge(id) == true ? ContractTerms.BridgeBlocker(Rt.State.Contracts.Where(c => c.Id != id).Select(c => c.Level)) : null);

        /// <summary>
        /// A contract taken now starts at once, with its first cars: the career has none at work
        /// (owner, 2026-11-10: "Faster service is fine"; mikebackwards, Nexus: "need to wait 6
        /// real time hours ... until I can play the game?"). Any other starts at midnight (D16).
        /// </summary>
        public bool StartsAtOnce => OpensAtOnce && Rt != null && Rt.State.Contracts.All(c => c.Level < 1);

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
            var here = Contracts.Core.Net.DeskOrder.Here(StationOf(place), Adopted);
            var atInterchange = place != null && place.IsInterchange;
            // The order is the core's (DeskOrder), the same at a kiosk of a player who joined: the
            // bridge leads at an interchange office once it can be taken; until then it is with the locked.
            return AllOffers()
                .OrderBy(o => Contracts.Core.Net.DeskOrder.Rank(o.Station, o.View.Needs != null, here, atInterchange))
                .ThenBy(o => o.View.Id, StringComparer.Ordinal)
                .Select(o => o.View)
                .ToList();
        }

        /// <summary>The station a kiosk's place stands for: its own, or the one beside an interchange office.</summary>
        internal static string StationOf(KioskPlace place) =>
            place == null ? null : place.IsInterchange ? InterchangeYards.NeighborOf(place.Id) : place.Id;

        /// <summary>A station's industry that another's kiosk lists as its own.</summary>
        internal static IReadOnlyDictionary<string, string> AdoptedBy => Adopted;

        /// <summary>
        /// Every contract not held, each with its station (none for the bridge): an industry's, a
        /// city's passenger service (D13), the bridge (D25).
        /// </summary>
        internal static List<(string Station, ContractView View)> AllOffers()
        {
            if (Rt == null)
                return new List<(string, ContractView)>();
            var all = Industries.Values.Select(i => (Station: i.Station, Id: i.Id, Short: $"{i.Station} {i.Name}"))
                .Concat(Passengers.Cities().Select(c => (Station: c, Id: PassengerService.IdOf(c), Short: $"{c} Passengers")))
                .Concat((Rt.Economy?.Data?.Bridges ?? new List<Bridge>()).Select(b => (Station: (string)null, Id: b.Id, Short: "WI-NEI Bridge")));
            return all
                .Where(o => Rt.State.Contracts.All(m => m.Id != o.Id))
                .Select(o => (o.Station, new ContractView { Id = o.Id, Short = Clip(o.Short), IsOffer = true, Needs = Blocker(o.Id) }))
                .ToList();
        }

        private static string Clip(string s) => s.Length <= ShortLength ? s : s.Substring(0, ShortLength - 1).TrimEnd() + ".";

        /// <summary>
        /// The player's contracts, those with the most cars still to move first (Ace, Discord,
        /// 2026-11-08: "sort it by most remaining moves to least"); a passenger contract, or one
        /// not started, counts none. Ties keep the order they were taken in.
        /// </summary>
        public IReadOnlyList<ContractView> Mine()
        {
            if (Rt == null)
                return new List<ContractView>();
            var cars = Rt.CarsOfContracts();
            var mine = Rt.State.Contracts.Select(c => new ContractView
            {
                Id = c.Id, Short = c.Short ?? c.Id, IsOffer = false, Level = c.Level, RequestedLevel = c.Requested, Ending = c.Ending,
                Performance = (float?)ContractStanding.Average(c.History), ScoredDays = c.History.Count,
                OpenWaybills = PassengerService.CityOf(c.Id) != null ? c.Due.Count + c.Running.Count : 0,
                ToMove = cars.TryGetValue(c.Id, out var theirs) && (c.Level >= 1 || theirs.Count > 0) ? ContractCars.ToMove(theirs) : (int?)null,
                Needs = LicenseRules.Blocker(c.Id),
            });
            return ContractCars.MostToMoveFirst(mine, v => v.ToMove).ToList();
        }

        public ContractCarsView Cars(ContractView contract)
        {
            if (Rt == null || contract == null || contract.IsOffer || IsPassenger(contract.Id) || Rt.State.Contracts.All(c => c.Id != contract.Id))
                return null;
            var cars = Rt.CarsOfContracts();
            if (!cars.TryGetValue(contract.Id, out var theirs))
                return null;
            // The bridge's cars go by where they are billed: to NEI, then back to WI, as its legend says it.
            var bridge = Rt.Economy?.Data?.Bridges.FirstOrDefault(b => b.Id == contract.Id);
            var ends = bridge == null ? null
                : new[] { bridge.From, bridge.To }.Select(InterchangeYards.CodeOf).OrderBy(e => e == "NEI" ? 0 : 1).ToArray();
            return new ContractCarsView
            {
                ToMove = ContractCars.ToMove(theirs),
                Line = ContractCars.ToMoveLine(theirs, ends),
                Page = ContractCars.Page(theirs, ends),
            };
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
            var needs = Blocker(offer.Id);
            if (needs != null)
            {
                Main.Log($"contracts: {offer.Id} not taken: {needs}");
                return;
            }
            var atOnce = StartsAtOnce;
            Rt.State.Contracts.Add(new HeldContract { Id = offer.Id, Short = offer.Short, Level = atOnce ? 1 : 0, Requested = 1, SinceDay = Rt.State.Day });
            Main.Log($"contracts: took {offer.Id}; it starts at level 1 {(atOnce ? "now, with its first cars" : "at midnight")}");
            if (atOnce)
                Rt.OpenFirstContract();
        }

        public void RequestLevel(ContractView contract, int level)
        {
            var held = Rt?.State.Contracts.FirstOrDefault(c => c.Id == contract.Id);
            if (held == null)
                return;
            // Any level asked for takes back an end asked for: nothing is final before midnight.
            held.Ending = false;
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

        public string RaiseValue(ContractView contract)
        {
            var held = Rt?.State.Contracts.FirstOrDefault(c => c.Id == contract.Id);
            return held == null ? "locked" : ContractStanding.RaiseBlockedValue(held.Level, held.History);
        }

        /// <summary>
        /// Ending a contract is final at midnight, like every change, and its penalty is charged
        /// then (owner, 2026-11-10: "Penalties shouldn't apply until midnight when the contract is
        /// finalized"); until then it works as before and the end can be taken back. A contract
        /// not yet started has nothing to finalize: it ends at once, at no cost.
        /// </summary>
        public void End(ContractView contract)
        {
            var held = Rt?.State.Contracts.FirstOrDefault(c => c.Id == contract.Id);
            if (held == null)
                return;
            if (held.Level < 1)
            {
                Rt.State.Contracts.Remove(held);
                Main.Log($"contracts: {held.Id} given up before it started");
                return;
            }
            held.Ending = true;
            held.Requested = held.Level;
            Main.Log($"contracts: {held.Id} asked to end at midnight");
        }

        public (int Cars, string Page) Waiting()
        {
            if (Rt == null)
                return (0, null);
            var waiting = Rt.State.Waiting.Select(w => w.Order()).ToList();
            return (ServiceRetry.Cars(waiting), ServiceRetry.Page(waiting, InterchangeYards.CodeOf, Rt.State.RetryAt, ContractRuntime.Clock));
        }

        public double Penalty(ContractView contract, int target)
        {
            var held = Rt?.State.Contracts.FirstOrDefault(c => c.Id == contract.Id);
            if (held == null)
                return 0.0;
            // Given up before its first midnight, a contract costs nothing (ContractTerms.FreeToGiveUp).
            if (target < 1 && ContractTerms.FreeToGiveUp(held.SinceDay, Rt.State.Day))
                return 0.0;
            return ContractTerms.Penalty(held.Level, target, held.History.Count);
        }

        public string Hold => Rt?.OnHold;

        // The shop and the sales, through the desk: a player who joined asks the host's (Kiosk.RemoteDesk).
        public double Money => LocoShop.Money;

        public IReadOnlyList<ShopRow> Shop(bool coaches, KioskPlace place) => ShopRows(coaches, place);

        public string Buy(bool coaches, string key, KioskPlace place) => BuyRow(coaches, key, place);

        public IReadOnlyList<SaleRow> Selling() => SaleRows();

        public string Sell(string key) => SellRow(key);

        internal static List<ShopRow> ShopRows(bool coaches, KioskPlace place) =>
            LocoShop.Catalogue(coaches ? LocoShop.Kind.Coaches : LocoShop.Kind.Locomotives)
                .Select(i => new ShopRow { Key = i.Name, Name = i.Name, Price = i.Price, Ask = LocoShop.AskToBuy(i, place) }).ToList();

        internal static string BuyRow(bool coaches, string key, KioskPlace place)
        {
            var item = LocoShop.Catalogue(coaches ? LocoShop.Kind.Coaches : LocoShop.Kind.Locomotives).FirstOrDefault(i => i.Name == key);
            return item == null ? $"{key} is not sold here any more." : LocoShop.Buy(item, place);
        }

        internal static List<SaleRow> SaleRows() =>
            VehicleSales.Owned().Where(u => u.Cars.Count > 0 && u.Cars[0]?.logicCar != null)
                .Select(u => new SaleRow { Key = u.Cars[0].logicCar.carGuid, Label = u.Label, Offer = u.Offer, Blocker = u.Blocker, Ask = VehicleSales.Ask(u) }).ToList();

        internal static string SellRow(string key)
        {
            var unit = VehicleSales.Owned().FirstOrDefault(u => u.Cars.Any(c => c?.logicCar?.carGuid == key));
            return unit == null ? "That vehicle is gone." : VehicleSales.Sell(unit);
        }

        public double Owed => Rt?.State.Owed ?? 0.0;

        public (string Value, string Page)? DayLength => DayLengthOf(DV.Globals.G?.GameParams?.DayLengthInMinutes ?? 0f);

        /// <summary>A day length's row value and page, or null for 24-hour days (Core DayLength).</summary>
        internal static (string Value, string Page)? DayLengthOf(double minutes) =>
            Contracts.Core.Industries.DayLength.Page(minutes) is string page ? (Contracts.Core.Industries.DayLength.Value(minutes), page) : ((string, string)?)null;

        public string Notice
        {
            get
            {
                if (Rt == null)
                    return "Contracts aren't running in this game";
                if (Rt.OnHold != null)
                    return ContractTerms.HoldLine;
                var minutes = DV.Globals.G?.GameParams?.DayLengthInMinutes ?? 0f;
                return minutes > 0f && minutes < 24f * 60f - 1f ? $"Days last {minutes / 60f:0.#} h: set 24 h" : null;
            }
        }
    }
}
