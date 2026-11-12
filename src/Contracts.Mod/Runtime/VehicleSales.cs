using System;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.Garages;
using DV.InventorySystem;
using DV.Logic.Job;
using DV.ServicePenalty;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// Selling the vehicles the player bought back at the kiosk (owner, 2026-11-07: "you
    /// shouldn't be clearing trains that you bought. You should be re-railing them or selling
    /// them. Clearing is essentially deleting the train"). The comms radio's Clear no longer
    /// takes one of them (owner, 2026-11-10; <see cref="Protect"/>): it deleted a bought
    /// vehicle for good, with nothing back.
    ///
    /// - **What is sold:** a vehicle bought at the kiosk, with what came with it (a locomotive's
    ///   tender, a train of coaches), or a new career's starter, wherever it stands, a wreck
    ///   too. Not one the player is on, one coupled to other cars, or one on a job.
    /// - **The offer** (Contracts.Core VehicleSale): half what was paid, less what its repairs
    ///   and refills cost now (the career manager's Owned Vehicles screen), never below a tenth
    ///   of what was paid. What was paid is recorded at the purchase (ContractState.Bought); for
    ///   one bought before the record, today's shop price stands in.
    /// - **The sale** pays at once and takes the vehicle away, leaving nothing behind: the game
    ///   keeps a deleted owned vehicle's bill on its Owned Vehicles screen, and its number,
    ///   paint and state for the next vehicle of its model spawned (a garage's car coming back);
    ///   both are dropped.
    ///
    /// And purchases are fresh: before the shop spawns a vehicle, the game's kept state for its
    /// model (a cleared vehicle's) is dropped with that vehicle's bill, as buying the model
    /// again dropped the bill before, so a new vehicle never takes a cleared one's number or
    /// paint.
    /// </summary>
    internal static class VehicleSales
    {
        /// <summary>Vehicles sold together: one purchase's cars still on the map.</summary>
        internal sealed class Unit
        {
            public List<TrainCar> Cars;
            public string Label;          // the kiosk row: the first car's number and its model
            public double Paid;
            public bool PaidRecorded;     // false: today's shop price stands in
            public double Owed;           // repairs and refills, as the Owned Vehicles screen has them
            public double Offer;
            public string Blocker;        // why it can't be sold now, or null
            public bool Coaches;
        }

        /// <summary>Off only for the probe, to see the game's own carry-over of a cleared vehicle's number.</summary>
        internal static bool FreshPurchases = true;

        private const int LabelLength = 26;

        private static ContractState State => ContractRuntime.Instance?.State;

        /// <summary>Records vehicles got at the kiosk: their price shared out (a train of coaches evenly, a locomotive's on itself).</summary>
        public static void Record(IReadOnlyList<TrainCar> cars, double price, bool coaches)
        {
            var state = State;
            if (state == null || cars == null || cars.Count == 0)
                return;
            var set = cars[0].logicCar?.carGuid;
            for (var i = 0; i < cars.Count; i++)
            {
                var guid = cars[i]?.logicCar?.carGuid;
                if (guid == null)
                    continue;
                state.Bought.RemoveAll(p => p.Guid == guid);
                state.Bought.Add(new Purchase { Guid = guid, Set = set, Price = coaches ? price / cars.Count : i == 0 ? price : 0.0 });
                cars[i].preventDelete = true;
            }
        }

        /// <summary>
        /// Keeps the vehicles the kiosk sells from the comms radio's Clear, which passes over a car
        /// with the game's own preventDelete flag (as it does the player's car). The flag is not
        /// saved, so it is set at each load, and on each purchase (<see cref="Record"/>). Selling
        /// at the kiosk is how they go. Returns how many are kept.
        /// </summary>
        public static int Protect()
        {
            var recorded = new HashSet<string>((State?.Bought ?? new List<Purchase>()).Where(p => p.Guid != null).Select(p => p.Guid), StringComparer.Ordinal);
            var n = 0;
            foreach (var car in SingletonBehaviour<CarSpawner>.Instance?.AllCars?.Where(c => Sellable(c, recorded)).ToList() ?? new List<TrainCar>())
            {
                car.preventDelete = true;
                n++;
            }
            return n;
        }

        private static bool IsLocomotive(Unit u) => u.Cars.Any(c => c != null && CarTypes.IsLocomotive(c.carLivery));

        /// <summary>
        /// Whether a car is one the player owns that the kiosk sells, not a garage's: one recorded
        /// as bought, or a locomotive, tender, caboose or coach (a stock coach is one without
        /// Passenger Jobs too, which gives coaches their passengers).
        /// </summary>
        private static bool Sellable(TrainCar car, HashSet<string> recorded) =>
            car != null && car.uniqueCar && car.playerSpawnedCar && car.logicCar != null && car.GetComponent<HomeGarageReference>() == null
            && (recorded.Contains(car.logicCar.carGuid) || CarTypes.IsLocomotive(car.carLivery) || CarTypes.IsTender(car.carLivery)
                || CarTypes.IsCaboose(car.carLivery) || LocoShop.IsStockCoach(car.carLivery) || LocoShop.IsCoach(car.carLivery));

        /// <summary>The vehicles the player can sell, as the kiosk lists them.</summary>
        public static List<Unit> Owned()
        {
            var bought = (State?.Bought ?? new List<Purchase>()).Where(p => p.Guid != null).GroupBy(p => p.Guid).ToDictionary(g => g.Key, g => g.First());
            var recorded = new HashSet<string>(bought.Keys, StringComparer.Ordinal);
            var cars = SingletonBehaviour<CarSpawner>.Instance?.AllCars?.Where(c => Sellable(c, recorded)).ToList() ?? new List<TrainCar>();
            var units = new List<List<TrainCar>>();
            // One purchase's cars go together.
            foreach (var g in cars.Where(c => bought.ContainsKey(c.logicCar.carGuid)).GroupBy(c => bought[c.logicCar.carGuid].Set))
                units.Add(g.ToList());
            // Before the record: a locomotive with the tenders coupled to it, anything else alone.
            var loose = cars.Where(c => !bought.ContainsKey(c.logicCar.carGuid)).ToList();
            var taken = new HashSet<TrainCar>();
            foreach (var loco in loose.Where(c => !CarTypes.IsTender(c.carLivery)))
            {
                var unit = new List<TrainCar> { loco };
                foreach (var next in Neighbours(loco).Where(n => loose.Contains(n) && CarTypes.IsTender(n.carLivery) && !taken.Contains(n)))
                    unit.Add(next);
                taken.UnionWith(unit);
                units.Add(unit);
            }
            foreach (var tender in loose.Where(c => !taken.Contains(c)))
                units.Add(new List<TrainCar> { tender });
            var priced = new Prices();
            return units.Select(u => Describe(u, bought, priced)).OrderBy(u => u.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<TrainCar> Neighbours(TrainCar car) =>
            new[] { car.frontCoupler?.coupledTo?.train, car.rearCoupler?.coupledTo?.train }.Where(t => t != null);

        private static Unit Describe(List<TrainCar> cars, Dictionary<string, Purchase> bought, Prices prices)
        {
            var head = cars.FirstOrDefault(c => !CarTypes.IsTender(c.carLivery)) ?? cars[0];
            var coaches = cars.All(c => LocoShop.IsStockCoach(c.carLivery) || LocoShop.IsCoach(c.carLivery));
            var recorded = cars.All(c => bought.ContainsKey(c.logicCar.carGuid));
            var paid = recorded ? cars.Sum(c => bought[c.logicCar.carGuid].Price) : prices.Today(cars, coaches);
            var owed = Owed(cars);
            var name = LocoShop.NameOf(head.carLivery) + (coaches && cars.Count > 1 ? $" x{cars.Count}" : "");
            var label = $"{head.ID} {name}";
            return new Unit
            {
                Cars = cars, Coaches = coaches, Paid = paid, PaidRecorded = recorded, Owed = owed,
                Offer = Core.Shop.VehicleSale.Offer(paid, owed), Blocker = Blocker(cars),
                Label = label.Length <= LabelLength ? label : label.Substring(0, LabelLength - 1).TrimEnd() + ".",
            };
        }

        /// <summary>Today's shop prices for vehicles bought before the record: a locomotive's row, a coach's share of a train of two.</summary>
        private sealed class Prices
        {
            private List<LocoShop.Item> locos, coaches;

            public double Today(List<TrainCar> cars, bool coach)
            {
                var head = cars.FirstOrDefault(c => !CarTypes.IsTender(c.carLivery)) ?? cars[0];
                if (coach)
                {
                    coaches = coaches ?? LocoShop.Catalogue(LocoShop.Kind.Coaches);
                    var two = coaches.FirstOrDefault(i => i.Cars.Count == 2 && i.Cars[0] == head.carLivery);
                    return two != null ? two.Price / 2.0 * cars.Count : LocoShop.ValueOf(cars.Select(c => c.carLivery));
                }
                locos = locos ?? LocoShop.Catalogue(LocoShop.Kind.Locomotives);
                var row = locos.FirstOrDefault(i => i.Cars.Count > 0 && i.Cars[0] == head.carLivery);
                return row != null ? row.Price : LocoShop.ValueOf(cars.Select(c => c.carLivery));
            }
        }

        /// <summary>What the cars' repairs and refills cost now: the game's own figure for its Owned Vehicles screen.</summary>
        private static double Owed(List<TrainCar> cars)
        {
            var states = SingletonBehaviour<OwnedCarsStateController>.Instance?.existingOwnedCarStates;
            if (states == null)
                return 0.0;
            var total = 0.0;
            foreach (var state in states.Where(s => s?.car != null && cars.Contains(s.car)))
            {
                state.UpdateDebtState();
                total += Math.Max(0f, state.GetTotalPrice());
            }
            return total;
        }

        /// <summary>Why the cars can't be sold now: the player is on them, they're coupled to others, or one is on a job.</summary>
        private static string Blocker(List<TrainCar> cars)
        {
            if (PlayerManager.Car != null && cars.Contains(PlayerManager.Car))
                return "You're on it. Step off first.";
            // In Multiplayer, any player's (Kiosk.DeskNet; the add-on gives the cars they stand on).
            if (Kiosk.DeskNet.CarsWithPlayers?.Invoke().Any(c => c != null && cars.Contains(c)) == true)
                return "A player is on it. They step off first.";
            if (cars.SelectMany(Neighbours).Any(n => !cars.Contains(n)))
                return "It's coupled to other cars. Uncouple it first.";
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            if (jobs != null && cars.Any(c => jobs.GetJobOfCar(c.logicCar) != null))
                return "It's on a job. Finish or abandon the job first.";
            return null;
        }

        /// <summary>The kiosk's question before a sale: the offer and how it is made up.</summary>
        public static string Ask(Unit u)
        {
            var paid = u.PaidRecorded ? $"the ${u.Paid:N0} you paid" : $"its ${u.Paid:N0} price today";
            string why;
            if (u.Paid < 1.0)
                why = $"Sell {u.Label} for nothing? It came with the career.";
            else if (u.Offer > Core.Shop.VehicleSale.Scrap * u.Paid + 0.5)
                why = $"Sell {u.Label} for ${u.Offer:N0}? That's half {paid}, less ${u.Owed:N0} for its repairs and refills.";
            else
                why = $"Sell {u.Label} for ${u.Offer:N0}, its scrap value? Its repairs and refills (${u.Owed:N0}) cost more than half {paid}.";
            var owned = Owned();
            var coachesLeft = !u.Coaches ? -1 : owned.Where(o => o.Coaches).Sum(o => o.Cars.Count) - u.Cars.Count;
            var warn = coachesLeft == 0 && (State?.Contracts.Any(c => RuntimeDesk.IsPassenger(c.Id)) ?? false)
                ? " Your passenger trains wait until you have coaches again." : "";
            // The only locomotive: said before it goes, with whether the cash buys another.
            if (IsLocomotive(u) && owned.Count(IsLocomotive) <= 1)
            {
                var cheapest = LocoShop.Catalogue(LocoShop.Kind.Locomotives).OrderBy(i => i.Price).FirstOrDefault();
                if (cheapest != null)
                    warn += Core.Shop.VehicleSale.LastLocomotive(LocoShop.Money + u.Offer, cheapest.Price, cheapest.Name);
            }
            return why + " It's taken away at once, wherever it stands." + warn;
        }

        /// <summary>Sells vehicles: pays the offer and takes them away. The message for the kiosk.</summary>
        public static string Sell(Unit u)
        {
            var inventory = SingletonBehaviour<Inventory>.Instance;
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            if (inventory == null || spawner == null)
                return "The cash register is not working.";
            var cars = u.Cars.Where(c => c != null).ToList();
            if (cars.Count == 0)
                return $"{u.Label} is gone.";
            var blocker = Blocker(cars);
            if (blocker != null)
                return $"{u.Label} can't be sold now. {blocker}";
            // The offer as it stands now.
            var offer = Core.Shop.VehicleSale.Offer(u.Paid, Owed(cars));
            var sold = cars.Select(c => (Id: c.ID, Guid: c.logicCar.carGuid, Livery: c.carLivery)).ToList();
            foreach (var car in cars)
            {
                // The game files a deleted owned vehicle's bill as the car is destroyed, at the end
                // of the frame unless the car goes back to its pool: dropped then, after it.
                var id = car.ID;
                Action<TrainCar> drop = null;
                drop = destroyed =>
                {
                    destroyed.OnDestroyCar -= drop;
                    DropBills(new[] { id });
                };
                car.OnDestroyCar += drop;
                spawner.DeleteCar(car);
                if (car != null)
                {
                    car.gameObject.SetActive(false);
                    if (car.interior != null)
                        car.interior.gameObject.SetActive(false);
                }
            }
            SingletonBehaviour<UnusedTrainCarDeleter>.Instance?.ClearInvalidCarReferencesAfterManualDelete();
            foreach (var (id, _, livery) in sold)
                Forget(livery, id);
            Unreserve(sold.Select(s => s.Id));
            DropBills(sold.Select(s => s.Id));
            State?.Bought.RemoveAll(p => sold.Any(s => s.Guid == p.Guid));
            inventory.AddMoney(offer);
            Main.Log($"shop: bought back {u.Label} ({string.Join(", ", sold.Select(s => s.Id))}) for ${offer:N0} (paid ${u.Paid:N0}{(u.PaidRecorded ? "" : ", today's price")})");
            return $"{u.Label} sold for ${offer:N0}.\n\nYou have ${inventory.PlayerMoney:N0}.";
        }

        // ------------------------------------------------------------ the game's kept state

        private static Dictionary<TrainCarLivery, JObject> Kept(CarSpawner spawner) =>
            spawner == null ? null : AccessTools.Field(typeof(CarSpawner), "deletedUniqueCarLiveryToLastCarState")?.GetValue(spawner) as Dictionary<TrainCarLivery, JObject>;

        /// <summary>
        /// Before the shop spawns vehicles: the game's kept state for their models (a cleared
        /// vehicle's number, paint and state) dropped, with that vehicle's bill.
        /// </summary>
        public static void Freshen(IEnumerable<TrainCarLivery> liveries)
        {
            if (!FreshPurchases)
                return;
            foreach (var livery in liveries.Where(l => l != null).Distinct())
            {
                var id = Forget(livery, null);
                if (id == null)
                    continue;
                Unreserve(new[] { id });
                DropBills(new[] { id });
                Main.Log($"shop: a new {livery.id} won't take {id}'s number: its kept state and bill dropped");
            }
        }

        /// <summary>Drops the game's kept state for a model (only if it is that car's, given its number); the number it held, or null.</summary>
        private static string Forget(TrainCarLivery livery, string onlyId)
        {
            try
            {
                var kept = Kept(SingletonBehaviour<CarSpawner>.Instance);
                if (kept == null || livery == null || !kept.TryGetValue(livery, out var data))
                    return null;
                var id = CarsSaveManager.UniqueCarDataToLoad.GetIdFromCarData(data);
                if (onlyId != null && id != onlyId)
                    return null;
                kept.Remove(livery);
                return id;
            }
            catch (Exception e)
            {
                Main.Error($"shop: dropping the game's kept state for {livery?.id} failed: {e.Message}");
                return null;
            }
        }

        /// <summary>Frees numbers the game reserved for deleted owned vehicles.</summary>
        private static void Unreserve(IEnumerable<string> ids)
        {
            var generator = SingletonBehaviour<IdGenerator>.Instance;
            var reserved = generator == null ? null : AccessTools.Field(typeof(IdGenerator), "reservedCarIds")?.GetValue(generator) as HashSet<string>;
            foreach (var id in ids.Where(i => i != null))
                if (reserved == null || reserved.Contains(id))
                    generator?.UnReserveCarId(id);
        }

        /// <summary>Drops deleted owned vehicles' bills from the Owned Vehicles screen.</summary>
        private static void DropBills(IEnumerable<string> ids)
        {
            var owned = SingletonBehaviour<OwnedCarsStateController>.Instance;
            if (owned == null)
                return;
            var set = new HashSet<string>(ids.Where(i => i != null), StringComparer.Ordinal);
            if (owned.currentlyDestroyedOwnedCarStates.RemoveAll(s => s != null && set.Contains(s.ID)) > 0)
                owned.UpdateSortedList();
        }

        /// <summary>The game's kept state's number for a model, if any (for the probe).</summary>
        internal static string KeptId(TrainCarLivery livery)
        {
            var kept = Kept(SingletonBehaviour<CarSpawner>.Instance);
            return kept != null && livery != null && kept.TryGetValue(livery, out var data) ? CarsSaveManager.UniqueCarDataToLoad.GetIdFromCarData(data) : null;
        }

        /// <summary>Whether the Owned Vehicles screen has a deleted vehicle's bill with this number (for the probe).</summary>
        internal static bool HasBill(string id) =>
            SingletonBehaviour<OwnedCarsStateController>.Instance?.currentlyDestroyedOwnedCarStates.Any(s => s?.ID == id) ?? false;
    }
}
