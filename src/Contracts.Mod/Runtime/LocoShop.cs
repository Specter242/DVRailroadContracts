using System;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.InventorySystem;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// Locomotives and coaches are bought (owner, 2026-11-01: "Trains will need to be
    /// purchasable with no free spawns"; 2026-11-02: modded locomotives too, and coaches), at a
    /// kiosk's Contracts menu, and are the player's own: owned vehicles in the game's own sense,
    /// as a garage's are (listed on Owned Vehicles, never deleted by the game, repaired by the
    /// player).
    ///
    /// - **Locomotives:** the stock ones (with the handcar and the caboose), then every other
    ///   locomotive or caboose type the game knows, the custom car loader's included. A
    ///   locomotive comes with its tender: the S282's, or a custom one's trainset.
    /// - **Coaches:** the stock passenger coaches, and any other car type that carries passengers,
    ///   sold as a train of 2, 4 or 6 (a custom set as it comes). A departure forms from them
    ///   wherever they stand (PassengerRuns), and the player brings them to its platform.
    ///
    /// Everything bought arrives through an interchange, as every other car does (owner,
    /// 2026-11-03: "new coaches should arrive at the interchange not magically at the station",
    /// then "yes" to locomotives too): at once, on a free track of the interchange whose office
    /// sold it, else of the one nearest the kiosk's station, else the other. With none free,
    /// nothing is sold.
    ///
    /// A vehicle bought comes with its license (LicenseRules.LicenseOwned), so its cab is never
    /// locked. Only a new career's starter DE2 (CareerRules) is delivered at a station: on its
    /// locomotive track (where the game used to spawn free ones) when that is clear, else on the
    /// nearest free storage track (<see cref="Deliver"/>).
    ///
    /// The price is the game's own value of the vehicle: what it charges to rebuild its body,
    /// wheels and powertrain, summed over the cars. A custom vehicle without those prices costs
    /// what the custom car loader asks to unlock it, if it says.
    /// </summary>
    internal static class LocoShop
    {
        internal enum Kind { Locomotives, Coaches }

        internal sealed class Item
        {
            public string Name;
            public List<TrainCarLivery> Cars;
            public float Price;
            public Kind Kind;   // the shop it is sold in: coaches arrive at an interchange
        }

        private const int NameLength = 24;

        private static readonly (TrainCarType Type, string Name, TrainCarType? Tender)[] StockLocomotives =
        {
            (TrainCarType.LocoShunter, "DE2", null),
            (TrainCarType.LocoDM3, "DM3", null),
            (TrainCarType.LocoDH4, "DH4", null),
            (TrainCarType.LocoDiesel, "DE6", null),
            (TrainCarType.LocoS060, "S060", null),
            (TrainCarType.LocoSteamHeavy, "S282", TrainCarType.Tender),
            (TrainCarType.LocoMicroshunter, "BE2", null),
            (TrainCarType.LocoDM1U, "DM1U", null),
            (TrainCarType.HandCar, "Handcar", null),
            (TrainCarType.CabooseRed, "Caboose", null),
        };

        private static readonly (TrainCarType Type, string Name, TrainCarType? Tender)[] StockCoaches =
        {
            (TrainCarType.PassengerRed, "Coach, red", null),
            (TrainCarType.PassengerGreen, "Coach, green", null),
            (TrainCarType.PassengerBlue, "Coach, blue", null),
        };

        public static List<Item> Catalogue(Kind kind)
        {
            var items = new List<Item>();
            var listed = new HashSet<TrainCarLivery>();
            foreach (var (type, name, tender) in kind == Kind.Coaches ? StockCoaches : StockLocomotives)
            {
                if (!Globals.G.Types.TrainCarType_to_v2.TryGetValue(type, out var livery) || livery == null)
                    continue;
                var cars = new List<TrainCarLivery> { livery };
                if (tender.HasValue && Globals.G.Types.TrainCarType_to_v2.TryGetValue(tender.Value, out var t) && t != null)
                    cars.Add(t);
                listed.UnionWith(cars);
                items.AddRange(Sized(kind, name, cars));
            }
            var others = new List<Item>();
            var trailing = new HashSet<TrainCarLivery>();
            foreach (var livery in Globals.G.Types.carTypes.Where(t => t != null && t.liveries != null).SelectMany(t => t.liveries))
            {
                if (livery == null || livery.isHidden || livery.prefab == null || listed.Contains(livery) || !Sells(kind, livery))
                    continue;
                var set = Trainset(livery);
                var cars = set.Length > 1 && set[0] == livery ? set.ToList() : new List<TrainCarLivery> { livery };
                trailing.UnionWith(cars.Skip(1));
                others.AddRange(Sized(kind, NameOf(livery), cars));
            }
            // A unit that comes behind another in its trainset (a B unit) is sold with it.
            items.AddRange(others.Where(i => !trailing.Contains(i.Cars[0])).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase));
            return items;
        }

        private static readonly int[] TrainSizes = { 2, 4, 6 };

        /// <summary>A shop's rows for a vehicle: one; for a single coach, a train of each size.</summary>
        private static IEnumerable<Item> Sized(Kind kind, string name, List<TrainCarLivery> cars)
        {
            if (kind != Kind.Coaches || cars.Count != 1)
                return new[] { new Item { Name = name, Cars = cars, Price = PriceOf(cars), Kind = kind } };
            var stem = name.Length <= NameLength - 3 ? name : name.Substring(0, NameLength - 4).TrimEnd() + ".";
            return TrainSizes.Select(n => Enumerable.Repeat(cars[0], n).ToList())
                .Select(train => new Item { Name = $"{stem} x{train.Count}", Cars = train, Price = PriceOf(train), Kind = kind });
        }

        /// <summary>Whether a livery is a coach: it carries passengers and is no locomotive, tender or caboose.</summary>
        internal static bool IsCoach(TrainCarLivery livery) => livery != null && Sells(Kind.Coaches, livery);

        private static bool Sells(Kind kind, TrainCarLivery livery)
        {
            if (kind == Kind.Locomotives)
                return CarTypes.IsLocomotive(livery) || CarTypes.IsCaboose(livery);
            if (CarTypes.IsLocomotive(livery) || CarTypes.IsTender(livery) || CarTypes.IsCaboose(livery))
                return false;
            return Globals.G.Types.CarTypeToLoadableCargo.TryGetValue(livery.parentType, out var cargos) &&
                   cargos.Any(c => c?.id != null && c.id.IndexOf("passenger", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>The custom car loader's trainset for a livery (the livery first), or none.</summary>
        private static TrainCarLivery[] Trainset(TrainCarLivery livery)
        {
            try
            {
                return CclTrainset.Value?.Invoke(livery) ?? Array.Empty<TrainCarLivery>();
            }
            catch (Exception e)
            {
                Main.Warn($"shop: the custom car loader's trainset for {livery.id} failed: {e.Message}");
                return Array.Empty<TrainCarLivery>();
            }
        }

        private static readonly Lazy<Func<TrainCarLivery, TrainCarLivery[]>> CclTrainset = new Lazy<Func<TrainCarLivery, TrainCarLivery[]>>(() =>
        {
            var method = AccessTools.TypeByName("CCL.Importer.CarManager")?.GetMethod("GetTrainsetForLivery",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(TrainCarLivery) }, null);
            return method == null ? null : (Func<TrainCarLivery, TrainCarLivery[]>)Delegate.CreateDelegate(typeof(Func<TrainCarLivery, TrainCarLivery[]>), method);
        });

        /// <summary>The livery's name in the game's language, else its type's, else its id; short enough for a kiosk row.</summary>
        private static string NameOf(TrainCarLivery livery)
        {
            var name = livery.id;
            foreach (var key in new[] { livery.localizationKey, livery.parentType?.localizationKey })
            {
                if (string.IsNullOrEmpty(key))
                    continue;
                var text = DV.Localization.LocalizationAPI.L(key);
                if (!string.IsNullOrWhiteSpace(text) && text != key && !text.StartsWith("[", StringComparison.Ordinal))
                {
                    name = text.Trim();
                    break;
                }
            }
            return name.Length <= NameLength ? name : name.Substring(0, NameLength - 1).TrimEnd() + ".";
        }

        /// <summary>The game's value of the cars: rebuilding body, wheels and powertrain, to the nearest $500.</summary>
        public static float PriceOf(IEnumerable<TrainCarLivery> cars)
        {
            float Part(float p) => p > 0f ? p : 0f;
            var list = cars.ToList();
            var sum = list.Sum(c => c?.parentType?.damage == null ? 0f
                : Part(c.parentType.damage.bodyPrice) + Part(c.parentType.damage.wheelsPrice)
                  + Part(c.parentType.damage.mechanicalPowertrainPrice) + Part(c.parentType.damage.electricalPowertrainPrice));
            if (sum < 1000f && list.Count > 0 && AccessTools.Field(list[0].GetType(), "UnlockPrice")?.GetValue(list[0]) is float unlock && unlock > sum)
                sum = unlock;
            return Mathf.Max(500f, Mathf.Round(sum / 500f) * 500f);
        }

        public static double Money => SingletonBehaviour<Inventory>.Instance?.PlayerMoney ?? 0.0;

        /// <summary>Why an item can't be bought now, for the kiosk's info screen: the money falls short, by how much.</summary>
        public static string ShortOf(Item item) =>
            $"Not enough money for {item.Name}.\n\nIt costs ${item.Price:N0}. You have ${Money:N0}, ${Math.Max(0.0, item.Price - Money):N0} short.";

        /// <summary>The kiosk's question before a purchase: the price, and where the vehicle arrives.</summary>
        public static string AskToBuy(Item item, Kiosk.KioskPlace place)
        {
            // It arrives on a free track of an interchange: this office's, else the one nearest here (DeliverThroughInterchange).
            var first = InterchangesFor(place).FirstOrDefault();
            var at = first == null ? "an interchange" : $"the {InterchangeYards.CodeOf(first)} interchange";
            var why = place != null && place.IsInterchange && first == place.Id ? "here" : "the nearest";
            return $"Buy {item.Name} for ${item.Price:N0}? It arrives at once on a free track at {at} ({why}), else at the other one." +
                   (item.Kind == Kind.Coaches ? " Bring them to a platform to board." : "");
        }

        /// <summary>Buys an item for delivery at a kiosk's place; the message for the kiosk.</summary>
        public static string Buy(Item item, Kiosk.KioskPlace place)
        {
            var inventory = SingletonBehaviour<Inventory>.Instance;
            if (inventory == null)
                return "The cash register is not working.";
            if (inventory.PlayerMoney < item.Price)
                return ShortOf(item);
            var spawned = DeliverThroughInterchange(item.Cars, place, out var where);
            if (spawned == null)
                return $"No free interchange track for {item.Name}.\n\nClear one at WI or NEI and try again. Nothing was paid.";
            inventory.RemoveMoney(item.Price);
            LicenseRules.LicenseOwned(spawned, null);
            Main.Log($"shop: sold {item.Name} for ${item.Price:N0}: {string.Join(", ", spawned.Select(c => c.ID))} on {where}");
            return $"{item.Name} delivered on {where}: {string.Join(", ", spawned.Select(c => c.ID))}.\n\n${item.Price:N0} paid. You have ${inventory.PlayerMoney:N0} left.";
        }

        /// <summary>Owned cars delivered at a station: its locomotive track, else a free storage track.</summary>
        public static List<TrainCar> Deliver(IEnumerable<TrainCarLivery> cars, StationController station, out string where)
        {
            var list = cars.ToList();
            var length = Length(list);
            foreach (var spawner in UnityEngine.Object.FindObjectsOfType<StationLocoSpawner>()
                         .Where(s => s != null && s.locoSpawnTrack != null)
                         .OrderBy(s => (s.transform.position - station.transform.position).sqrMagnitude).Take(1))
            {
                var track = spawner.locoSpawnTrack;
                if ((spawner.transform.position - station.transform.position).magnitude > 1500f || track.LogicTrack().GetCarsFullyOnTrack().Count > 0)
                    continue;
                var anchor = Traverse.Create(spawner).Field("spawnTrackMiddleAnchor").GetValue<GameObject>();
                var at = anchor != null ? anchor.transform.position : Middle(track);
                var spawned = Spawn(list, at);
                if (spawned != null)
                {
                    where = $"the locomotive track {track.LogicTrack().ID.FullDisplayID}";
                    return spawned;
                }
            }
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            foreach (var track in station.logicStation?.yard?.StorageTracks ?? new List<Track>())
            {
                if (organizer == null || organizer.GetFreeSpaceOnTrack(track) < length + 10.0 || track.GetCarsFullyOnTrack().Count > 0)
                    continue;
                var rail = RailTrackRegistry.LogicToRailTrack.TryGetValue(track, out var r) ? r : null;
                var spawned = rail == null ? null : Spawn(list, Middle(rail));
                if (spawned != null)
                {
                    where = $"track {track.ID.FullDisplayID}";
                    return spawned;
                }
            }
            where = null;
            return null;
        }

        /// <summary>
        /// A purchase delivered at an interchange, as every car comes onto the map: on a free track
        /// of the interchange whose office sold it, else of the one nearest the station, else the other.
        /// </summary>
        private static List<TrainCar> DeliverThroughInterchange(IEnumerable<TrainCarLivery> cars, Kiosk.KioskPlace place, out string where)
        {
            var list = cars.ToList();
            foreach (var interchange in InterchangesFor(place))
            {
                var spawned = DeliverAtInterchange(list, interchange, out where);
                if (spawned != null)
                {
                    where = $"{where} at {InterchangeYards.CodeOf(interchange)}";
                    return spawned;
                }
            }
            where = null;
            return null;
        }

        /// <summary>The interchanges in the order a purchase tries them: the office's own, else nearest the kiosk's station first.</summary>
        internal static List<string> InterchangesFor(Kiosk.KioskPlace place)
        {
            var all = InterchangeYards.All();
            if (place != null && place.IsInterchange && all.ContainsKey(place.Id))
                return new[] { place.Id }.Concat(all.Keys.Where(k => k != place.Id)).ToList();
            var station = place == null ? null : Economy.Station(place.Id);
            float Distance(List<Track> tracks)
            {
                if (station == null)
                    return 0f;
                return tracks.Select(t => RailTrackRegistry.LogicToRailTrack.TryGetValue(t, out var r) ? (Middle(r) - station.transform.position).sqrMagnitude : float.MaxValue)
                    .DefaultIfEmpty(float.MaxValue).Min();
            }
            return all.OrderBy(kv => Distance(kv.Value)).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList();
        }

        private static List<TrainCar> DeliverAtInterchange(IEnumerable<TrainCarLivery> cars, string interchange, out string where)
        {
            var list = cars.ToList();
            foreach (var track in InterchangeYards.Tracks(interchange).Where(t => t.GetCarsFullyOnTrack().Count == 0))
            {
                var rail = RailTrackRegistry.LogicToRailTrack.TryGetValue(track, out var r) ? r : null;
                var spawned = rail == null ? null : Spawn(list, Middle(rail));
                if (spawned != null)
                {
                    where = $"track {track.ID.FullDisplayID}";
                    return spawned;
                }
            }
            where = null;
            return null;
        }

        private static List<TrainCar> Spawn(List<TrainCarLivery> cars, Vector3 at) =>
            SingletonBehaviour<CarSpawner>.Instance?.SpawnCarTypesOnClosestTrack(cars, at, null, preventAutoCoupleOnLastCars: true,
                applyHandbrakeOnLastCars: true, startSpan: 0.0, flipTrainConsist: false, playerSpawnedCars: true, uniqueSpawnedCars: true);

        private static float Length(List<TrainCarLivery> cars)
        {
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            return spawner == null ? 30f * cars.Count : spawner.GetTotalCarLiveriesLength(cars, true);
        }

        /// <summary>The middle of a track, in world space now.</summary>
        internal static Vector3 Middle(RailTrack track)
        {
            var points = track.GetKinkedPointSet().points;
            return (Vector3)points[points.Length / 2].position + DV.OriginShift.OriginShift.currentMove;
        }
    }

    /// <summary>
    /// The interchanges' tracks, found by the job IDs the interchange mod gives them (type X,
    /// under the station whose sub-yard the interchange is: MF-WI-01-X). An interchange is named
    /// as its office is: "MF West" at MF, "IME West" at IME.
    /// </summary>
    internal static class InterchangeYards
    {
        public const string TrackType = "X";

        public static string NameOf(string stationYard) => stationYard + " West";

        /// <summary>
        /// An interchange's name as the player reads it, its code on the maps and in the books
        /// (the interchange mod's): WI for MF West, NEI for IME West; else the name itself.
        /// </summary>
        public static string CodeOf(string interchange) =>
            interchange == "MF West" ? "WI" : interchange == "IME West" ? "NEI" : interchange;

        public static string StationOf(string interchange) =>
            interchange != null && interchange.EndsWith(" West", StringComparison.Ordinal) ? interchange.Substring(0, interchange.Length - 5) : null;

        /// <summary>Every interchange track, by interchange name.</summary>
        public static Dictionary<string, List<Track>> All()
        {
            var result = new Dictionary<string, List<Track>>();
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            if (organizer == null)
                return result;
            foreach (var kv in organizer.yardTrackIdToTrack.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var parts = kv.Key.Split('-');
                if (parts.Length != 4 || parts[3] != TrackType)
                    continue;
                var name = NameOf(parts[0]);
                if (!result.TryGetValue(name, out var list))
                    result[name] = list = new List<Track>();
                list.Add(kv.Value);
            }
            return result;
        }

        public static List<Track> Tracks(string interchange) =>
            All().TryGetValue(interchange ?? "", out var list) ? list : new List<Track>();

        public static bool IsInterchangeTrack(Track t) => t != null && t.ID != null && t.ID.FullID.EndsWith("-" + TrackType, StringComparison.Ordinal);

        public static string InterchangeOf(Track t) => IsInterchangeTrack(t) ? NameOf(t.ID.yardId) : null;
    }
}
