using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Core.Sim;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using DV.WeatherSystem;
using HarmonyLib;
using UnityEngine;
using Random = System.Random;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The contracts at work in the game (design phase 3), on the host.
    ///
    /// - **Days.** A contract day is a game day on the world clock (D6). At midnight, levels
    ///   asked for take effect, and a contract taken that day starts. At 06:00 the interchange
    ///   service runs (design, "Interchange service").
    /// - **The service.** Cars left on an interchange track to go off the map are taken away;
    ///   contract cars whose job ended without finishing (abandoned or expired) go back to
    ///   idle. The core's planner plans the day for the contracts held (Industries/IndustryPlanner):
    ///   bills for idle contract cars, and orders of cars placed now on free interchange tracks,
    ///   empty or loaded.
    /// - **Work is stock jobs** (design, "Waybills and work"), built with Persistent Jobs'
    ///   generators for the cars' next leg: an empty haul to a shipper, a shunting load there, a
    ///   transport to the receiver's station or to an interchange track, a shunting unload. When a
    ///   chain of contract cars completes, the mod writes the next leg at once and takes the cars
    ///   out of the finished chain, so neither the game nor Persistent Jobs sees them jobless.
    /// - **Standing.** A contract car's bill is open from when it is billed for a move until the
    ///   move is done (an empty until loaded, a load until unloaded or gone at an interchange).
    ///   Each midnight scores every contract's day from its bills' ages, and a raise needs
    ///   consistent good days (Contracts.Core ContractStanding).
    /// - **Pay on top** (D7, ContractPay): quick delivery earns a bonus at levels 2–5; lowering
    ///   or ending a contract, or three poor days, costs a penalty.
    /// - **Saves.** The state goes under its own key on every save.
    /// </summary>
    internal sealed class ContractRuntime : MonoBehaviour
    {
        public static ContractRuntime Instance { get; private set; }

        public ContractState State;
        public Economy Economy;
        public readonly List<string> Notes = new List<string>();
        public int LastServiceDay = -1;

        private bool newCareer, setUp;
        private float timer, passengerTimer;
        private readonly HashSet<string> removing = new HashSet<string>(StringComparer.Ordinal);

        public static bool Active => Instance != null && Instance.setUp;

        // ------------------------------------------------------------ life

        public static void OnWorldLoaded()
        {
            try
            {
                if (Kiosk.Multiplayer.IsClient)
                {
                    Main.Log("contracts: a Multiplayer client; the host runs the contracts");
                    return;
                }
                var state = ContractState.Read(ContractState.CurrentSave, out var unreadable);
                if (unreadable)
                    return;
                var go = new GameObject("[contracts runtime]");
                var rt = go.AddComponent<ContractRuntime>();
                rt.State = state ?? new ContractState();
                rt.newCareer = state == null && IsNewCareer();
                Instance = rt;
                CareerRules.IsContractCar = g => g != null && Instance != null && Instance.State.Of(g) != null;
                var save = SingletonBehaviour<SaveGameManager>.Instance;
                if (save != null)
                    save.OnInternalDataUpdate += rt.OnSaving;
                var spawner = SingletonBehaviour<CarSpawner>.Instance;
                if (spawner != null)
                    spawner.CarAboutToBeDeleted += rt.OnCarDeleted;
                Main.Log($"contracts: runtime started ({(state == null ? "new state" : $"{state.Contracts.Count} contracts, {state.Waybills.Count} cars")}, new career {rt.newCareer})");
            }
            catch (Exception e)
            {
                Main.Error($"contracts: starting the runtime failed; contracts stay off: {e}");
            }
        }

        private static bool IsNewCareer()
        {
            var start = FindObjectOfType<AStartGameData>();
            return start is StartGameData_NewCareer;
        }

        private void OnDestroy()
        {
            var save = SingletonBehaviour<SaveGameManager>.Instance;
            if (save != null)
                save.OnInternalDataUpdate -= OnSaving;
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            if (spawner != null)
                spawner.CarAboutToBeDeleted -= OnCarDeleted;
            if (Instance == this)
            {
                Instance = null;
                CareerRules.IsContractCar = g => false;
            }
        }

        private void OnSaving(SaveGameData data)
        {
            try
            {
                State.Write(data);
            }
            catch (Exception e)
            {
                Main.Error($"contracts: saving failed; the save goes on without them: {e.Message}");
            }
        }

        private void OnCarDeleted(TrainCar car)
        {
            // The world unloading deletes every car; the saved state keeps them.
            if (UnloadWatcher.isUnloading)
                return;
            var guid = car != null && car.logicCar != null ? car.logicCar.carGuid : null;
            if (guid == null || State.Of(guid) == null)
                return;
            if (!removing.Remove(guid))
                Main.Warn($"contracts: contract car {car.ID} was deleted by the game");
            State.Remove(guid);
        }

        private void Update()
        {
            timer -= Time.unscaledDeltaTime;
            if (timer > 0f)
                return;
            timer = 2f;
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Main.Error($"contracts: {e}");
                timer = 30f;
            }
        }

        public static DateTime? Now => SingletonBehaviour<WeatherDriver>.Instance?.manager?.DateTime;

        public static int DayOf(DateTime t) => (int)(t.Date - new DateTime(2000, 1, 1)).TotalDays;

        /// <summary>The world clock in contract days: the day number plus the time of day (midnight of day d is d).</summary>
        public static double? Clock => Now is DateTime t ? DayOf(t) + t.TimeOfDay.TotalDays : (double?)null;

        private void Tick()
        {
            if (!AStartGameData.carsAndJobsLoadingFinished)
                return;
            if (!setUp)
            {
                var flagged = CareerRules.FlagAllStations();
                Notes.Add($"setup: {flagged} station(s) flagged as spawned");
                Economy = Economy.Build(Notes);
                if (newCareer && !State.StarterGiven)
                    State.StarterGiven = CareerRules.GiveStarter(Notes);
                LicenseRules.GiveJobTypes(Notes);
                LicenseRules.LicenseOwned(SingletonBehaviour<TrainCarRegistry>.Instance?.logicCarToTrainCar.Values, Notes);
                var withdrawn = WithdrawUncarried(LicenseRules.Carrying());
                if (withdrawn > 0)
                    Notes.Add($"licenses: {withdrawn} bill(s) for cargo without its licenses withdrawn");
                setUp = true;
                foreach (var n in Notes)
                    Main.Log("contracts: " + n);
            }
            var now = Now;
            if (now == null)
                return;
            var day = DayOf(now.Value);
            if (State.Day < 0)
                State.Day = day;
            while (State.Day < day)
            {
                State.Day++;
                Midnight();
            }
            if (now.Value.Hour >= 6 && State.ServiceDay < day)
            {
                State.ServiceDay = day;
                Service(day);
            }
            ContractPay.Collect(State);
            passengerTimer -= 2f;
            if (passengerTimer <= 0f)
            {
                passengerTimer = 10f;
                PassengerRuns.Run(State);
            }
        }

        /// <summary>
        /// The day just ended is scored for each running contract; then asked-for levels take
        /// effect (a raise only with the standing for it), and contracts taken today start. Three
        /// poor days force a contract down two levels; a drop costs the penalty, and a contract
        /// dropped to nothing ends (D7, ContractTerms).
        /// </summary>
        internal void Midnight()
        {
            var midnight = (double)State.Day;
            foreach (var c in State.Contracts.Where(c => c.Level >= 1))
            {
                var open = PassengerService.CityOf(c.Id) != null ? PassengerRuns.OpenAges(c, midnight)
                    : State.Waybills.Where(w => w.Opened.HasValue && w.Opened.Value < midnight && Involves(w, c))
                        .Select(w => midnight - w.Opened.Value).ToList();
                var score = ContractStanding.DayScore(open, c.Closed);
                var closed = c.Closed.Count;
                c.Closed.Clear();
                if (score == null)
                    continue;
                ContractStanding.Record(c.History, score.Value);
                Main.Log($"contracts: {c.Id} scored {score.Value * 100:0}% on day {State.Day - 1} ({open.Count} open, {closed} closed); " +
                         $"{ContractStanding.Average(c.History) * 100:0}% over {c.History.Count} day(s)");
            }
            var changes = new List<string>();
            var charged = 0.0;
            foreach (var c in State.Contracts.ToList())
            {
                var target = c.Requested >= 1 ? c.Requested : c.Level;
                // Three poor days take a contract down two levels (D7, Contracts.Core ContractTerms).
                var reason = "";
                var forced = ContractTerms.ForcedLevel(c.Level, c.History);
                if (forced.HasValue && target > forced.Value)
                {
                    target = forced.Value;
                    reason = " (low performance)";
                }
                if (target == c.Level)
                {
                    c.Requested = c.Level;
                    continue;
                }
                if (c.Level >= 1 && target > c.Level)
                {
                    var blocker = ContractStanding.RaiseBlocker(c.Level, c.History);
                    if (blocker != null)
                    {
                        Main.Log($"contracts: {c.Id} stays at level {c.Level}, not raised: {blocker}");
                        c.Requested = c.Level;
                        continue;
                    }
                    target = c.Level + 1;
                }
                var penalty = ContractTerms.Penalty(c.Level, target, c.History.Count);
                if (penalty > 0.0)
                {
                    ContractPay.Charge(State, penalty, $"{c.Id} from level {c.Level} to {target}{reason}");
                    charged += penalty;
                }
                if (target < 1)
                {
                    State.Contracts.Remove(c);
                    Main.Log($"contracts: {c.Id} ended at midnight{reason}; its cars finish their legs and drain");
                    changes.Add($"{c.Id} ended");
                    continue;
                }
                Main.Log($"contracts: {c.Id} from level {c.Level} to {target} at midnight{reason}");
                if (target < c.Level)
                    changes.Add($"{c.Id} L{c.Level}>{target}");
                c.Level = c.Requested = target;
                c.History.Clear();
                c.Closed.Clear();
            }
            // The kiosk says what dropped, the next time its Contracts list opens.
            if (changes.Count > 0)
                Kiosk.KioskMenu.Tell(charged > 0.0 ? $"{string.Join(", ", changes)}: -${charged:N0}" : string.Join(", ", changes));
        }

        /// <summary>Whether a car's bill counts for a contract: the one it is billed to, or the industry it goes to.</summary>
        private static bool Involves(Waybill w, HeldContract c) => c.Id == w.Contract || c.Id == w.Next;

        /// <summary>A bill opens when a car is billed for a move, unless one is still open for it.</summary>
        private static void Open(Waybill w)
        {
            if (w.Opened == null)
                w.Opened = Clock;
        }

        /// <summary>The move is done: the bill's final age counts on the next midnight's score of the contracts it involves.</summary>
        private void Close(Waybill w)
        {
            var now = Clock;
            if (w.Opened.HasValue && now.HasValue)
            {
                var age = Math.Round(Math.Max(0.0, now.Value - w.Opened.Value), 4);
                foreach (var c in State.Contracts.Where(c => Involves(w, c)))
                    c.Closed.Add(age);
            }
            w.Opened = null;
        }

        // ------------------------------------------------------------ service

        /// <summary>Runs the interchange service now (also for tests); returns a summary.</summary>
        public string Service(int day)
        {
            var passengers = PassengerRuns.Service(State, day);
            if (passengers != null)
                Main.Log("contracts: " + passengers);
            if (Economy?.Data == null)
            {
                Economy = Economy.Build(Notes);
                if (Economy?.Data == null)
                    return "no economy";
            }
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var carrying = LicenseRules.Carrying();
            var withdrawn = WithdrawUncarried(carrying);
            int gone = 0, idled = 0, taken = 0;
            foreach (var w in State.Waybills.ToList())
            {
                var car = registry?.GetTrainCarByCarGuid(w.Guid);
                if (car == null)
                {
                    State.Remove(w.Guid);
                    gone++;
                    continue;
                }
                var track = car.logicCar.FrontBogieTrack;
                if (w.Stage == Stage.OffMap || w.Stage == Stage.ToInterchange && jobs?.GetJobOfCar(car.logicCar) == null && InterchangeYards.IsInterchangeTrack(track))
                {
                    if (InterchangeYards.IsInterchangeTrack(track))
                    {
                        removing.Add(w.Guid);
                        SingletonBehaviour<CarSpawner>.Instance.DeleteCar(car);
                        State.Remove(w.Guid);
                        taken++;
                        continue;
                    }
                }
                if (w.Stage != Stage.Idle && w.Stage != Stage.OffMap && jobs?.GetJobOfCar(car.logicCar) == null)
                {
                    w.Stage = Stage.Idle;
                    w.At = PlaceOf(car, w);
                    idled++;
                }
                if (w.Stage == Stage.Idle)
                {
                    w.Cargo = car.logicCar.CurrentCargoTypeInCar == CargoType.None ? null : car.logicCar.CurrentCargoTypeInCar.ToString();
                    w.At = PlaceOf(car, w);
                }
            }

            var levels = State.Contracts.Where(c => c.Level >= 1 && (Economy.Industries.ContainsKey(c.Id) || Economy.IsBridge(c.Id)))
                .ToDictionary(c => c.Id, c => c.Level);
            var fleet = State.Waybills.Select(w => new FleetCar
            {
                Guid = w.Guid,
                CarType = registry?.GetTrainCarByCarGuid(w.Guid)?.carLivery?.parentType?.id,
                Location = w.At,
                Cargo = w.Cargo,
                Busy = w.Stage != Stage.Idle,
            }).Where(f => f.CarType != null).ToList();
            var plan = IndustryPlanner.PlanDay(Economy.Data, levels, fleet, new SplitMix64(State.Seed + (ulong)day), carrying);

            // Bills for cars already on the map.
            var dispatch = new List<(TrainCar Car, Waybill W)>();
            foreach (var a in plan.Assignments)
            {
                var w = State.Of(a.Guid);
                var car = registry?.GetTrainCarByCarGuid(a.Guid);
                if (w == null || car == null)
                    continue;
                switch (a.Kind)
                {
                    case AssignmentKind.EmptyToShipper:
                        w.Stage = Stage.ToShipper; w.Next = a.Destination; w.Then = a.Then; w.Cargo = a.Cargo; w.Contract = a.Shipper;
                        break;
                    case AssignmentKind.LoadToReceiver:
                        w.Stage = Stage.ToReceiver; w.Next = a.Destination; w.Then = null; w.Contract = a.Receiver;
                        break;
                    case AssignmentKind.ReturnEmpty:
                    case AssignmentKind.ExportLoad:
                        if (InterchangeYards.IsInterchangeTrack(car.logicCar.FrontBogieTrack))
                        {
                            Close(w);
                            w.Stage = Stage.OffMap;
                            continue;
                        }
                        w.Stage = Stage.ToInterchange; w.Next = a.Destination; w.Then = null;
                        break;
                }
                Open(w);
                dispatch.Add((car, w));
            }

            // Orders placed at the interchanges now: each interchange's cars arrive as cuts filling its empty tracks.
            int placed = 0, short_ = 0;
            foreach (var (order, cars) in PlaceAll(plan.Orders))
            {
                placed += cars.Count;
                short_ += order.Count - cars.Count;
                foreach (var car in cars)
                {
                    var w = new Waybill
                    {
                        Guid = car.logicCar.carGuid, Contract = order.Contract, Cargo = order.Cargo,
                        Next = order.BilledTo, Then = order.Then, At = order.Interchange, Opened = Clock,
                        Stage = order.Loaded ? (Economy.IsInterchange(order.BilledTo) ? Stage.ToInterchange : Stage.ToReceiver) : Stage.ToShipper,
                    };
                    State.Add(w);
                    dispatch.Add((car, w));
                }
            }
            var made = Dispatch(dispatch);
            var summary = $"service day {day}: {levels.Count} contract(s) at work; {taken} car(s) taken off the map, {gone} gone, {idled} back to idle, {withdrawn} withdrawn; " +
                          $"{plan.Assignments.Count} bill(s), {placed} car(s) placed ({short_} short), {made} job(s)";
            Main.Log("contracts: " + summary);
            Notes.Add(summary);
            LastServiceDay = day;
            return summary;
        }

        /// <summary>
        /// Bills for cargo the player may not carry, from a contract held since before the license
        /// gate (D19; owner, 2026-11-03: fix the save rather than start over). A job not yet taken
        /// is withdrawn: its booklet goes, the job expires, and its cars go idle, unscored. A load
        /// on an interchange track goes off the map at the next service; the service re-plans the
        /// rest, for cargo the player may carry. Cars of a withdrawn job whose own bills are
        /// carried get their next leg again at once. A job already taken is left to finish.
        /// Returns the bills withdrawn.
        /// </summary>
        internal int WithdrawUncarried(Func<string, bool> carries)
        {
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            if (registry == null || jobs == null)
                return 0;
            var withdrawn = new HashSet<Job>();
            var n = 0;
            foreach (var w in State.Waybills)
            {
                if (w.Cargo == null || w.Stage == Stage.Idle || w.Stage == Stage.OffMap || carries(w.Cargo))
                    continue;
                var car = registry.GetTrainCarByCarGuid(w.Guid);
                var job = car == null ? null : jobs.GetJobOfCar(car.logicCar);
                if (job != null && job.State != JobState.Available)
                    continue;
                if (job != null && withdrawn.Add(job))
                    Withdraw(job);
                var loaded = car != null && car.logicCar.CurrentCargoTypeInCar != CargoType.None;
                w.Opened = null;
                w.Stage = loaded && InterchangeYards.IsInterchangeTrack(car.logicCar.FrontBogieTrack) ? Stage.OffMap : Stage.Idle;
                if (car != null)
                    w.At = PlaceOf(car, w);
                n++;
                Main.Log($"licenses: {car?.ID ?? w.Guid} ({w.Cargo}, {w.Contract}) withdrawn: {ContractLicenses.Blocker(Economy.CargoLicenses.TryGetValue(w.Cargo, out var ids) ? ids : null)}; {w.Stage}");
            }
            // The rest of a withdrawn job's cars: their next leg again.
            var again = new List<(TrainCar Car, Waybill W)>();
            foreach (var job in withdrawn)
                foreach (var c in CarsOf(job.tasks))
                {
                    var w = State.Of(c?.carGuid);
                    if (w == null || w.Stage == Stage.Idle || w.Stage == Stage.OffMap)
                        continue;
                    if (registry.logicCarToTrainCar.TryGetValue(c, out var train) && train != null)
                        again.Add((train, w));
                }
            if (again.Count > 0)
                Main.Log($"licenses: {again.Count} other car(s) of withdrawn jobs: {Dispatch(again)} new job(s)");
            return n;
        }

        /// <summary>Takes a job not yet taken off its station: its booklet, then the job itself (it expires; its chain goes with it).</summary>
        private static void Withdraw(Job job)
        {
            var station = StationController.allStations?.FirstOrDefault(s => s != null && s.logicStation != null && s.logicStation.availableJobs.Contains(job));
            if (station != null)
            {
                var spawned = Traverse.Create(station).Field("spawnedJobOverviews").GetValue<List<JobOverview>>();
                foreach (var o in spawned?.Where(o => o != null && o.job == job).ToList() ?? new List<JobOverview>())
                {
                    spawned.Remove(o);
                    o.DestroyJobOverview();
                }
                Traverse.Create(station).Field("processedNewJobs").GetValue<HashSet<Job>>()?.Remove(job);
            }
            job.ExpireJob();
        }

        /// <summary>Where an idle car is, as the planner knows places: an interchange, the industry it was last billed to at this station, or the station's industry.</summary>
        private string PlaceOf(TrainCar car, Waybill w)
        {
            var track = car.logicCar.FrontBogieTrack;
            var interchange = InterchangeYards.InterchangeOf(track);
            if (interchange != null)
                return interchange;
            var yard = track?.ID?.yardId;
            if (yard == null || track.ID.IsGeneric())
                return w.At;
            foreach (var candidate in new[] { w.Next, w.At })
                if (candidate != null && Economy.StationOf(candidate) == yard)
                    return candidate;
            return Economy.Industries.Values.FirstOrDefault(i => i.Station == yard)?.Id ?? w.At;
        }

        /// <summary>
        /// Spawns the orders' cars at their interchanges, loaded if an order says so: each
        /// interchange's cars in order, as cuts that fill its empty tracks one after another
        /// (a track with cars on it is left alone).
        /// </summary>
        private List<(Order Order, List<TrainCar> Cars)> PlaceAll(List<Order> orders)
        {
            var result = orders.Select(o => (Order: o, Cars: new List<TrainCar>())).ToList();
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            foreach (var at in result.GroupBy(r => r.Order.Interchange))
            {
                // The cars wanted here, one entry per car, each with its order.
                var wanted = new List<(int Order, TrainCarLivery Livery, CargoType Cargo)>();
                foreach (var r in at)
                {
                    var o = r.Order;
                    if (!Globals.G.Types.TryGetCarType(o.CarType, out var type) || type.liveries == null || type.liveries.Count == 0)
                    {
                        Main.Warn($"contracts: no car type {o.CarType} for an order of {o.Count}");
                        continue;
                    }
                    var cargo = CargoType.None;
                    if (o.Loaded && (o.Cargo == null || !Enum.TryParse(o.Cargo, out cargo)))
                        continue;
                    for (var i = 0; i < o.Count; i++)
                        wanted.Add((result.FindIndex(x => ReferenceEquals(x.Order, o)), type.liveries[0], cargo));
                }
                var next = 0;
                foreach (var track in InterchangeYards.Tracks(at.Key).Where(t => t.GetCarsFullyOnTrack().Count == 0 && t.GetCarsPartiallyOnTrack().Count == 0))
                {
                    if (next >= wanted.Count)
                        break;
                    var rail = RailTrackRegistry.LogicToRailTrack.TryGetValue(track, out var r) ? r : null;
                    if (rail == null)
                        continue;
                    // As many as fit, with room to spare at the ends.
                    var take = 0;
                    while (next + take < wanted.Count &&
                           spawner.GetTotalCarLiveriesLength(wanted.Skip(next).Take(take + 1).Select(w => w.Livery).ToList(), true) < track.length - 30.0)
                        take++;
                    if (take == 0)
                        continue;
                    var cut = wanted.Skip(next).Take(take).ToList();
                    var cars = spawner.SpawnCarTypesOnTrack(cut.Select(w => w.Livery).ToList(), null, rail, preventAutoCoupleOnLastCars: true, applyHandbrakeOnLastCars: true);
                    if (cars == null || cars.Count != cut.Count)
                    {
                        Main.Warn($"contracts: spawning {cut.Count} car(s) on {track.ID.FullDisplayID} failed");
                        continue;
                    }
                    for (var i = 0; i < cars.Count; i++)
                    {
                        if (cut[i].Cargo != CargoType.None)
                            cars[i].logicCar.LoadCargo(cars[i].logicCar.capacity, cut[i].Cargo);
                        result[cut[i].Order].Cars.Add(cars[i]);
                    }
                    next += take;
                }
                if (next < wanted.Count)
                    Main.Warn($"contracts: interchange {at.Key} had no room for {wanted.Count - next} of {wanted.Count} car(s)");
            }
            return result;
        }

        private static int MaxCarsPerJob() => Math.Max(3, SingletonBehaviour<LicenseManager>.Instance?.GetMaxNumberOfCarsPerJobWithAcquiredJobLicenses() ?? 5);

        // ------------------------------------------------------------ jobs

        /// <summary>
        /// Gives cars their next leg's jobs, grouped by where they stand and where they go. Returns
        /// the jobs made. <paramref name="standingOn"/>: the track the cars stand on, when known
        /// better than their bogies say (a job just completed there; the bogies' tracks can lag).
        /// </summary>
        private int Dispatch(List<(TrainCar Car, Waybill W)> cars, Track standingOn = null)
        {
            var made = 0;
            var groups = cars.GroupBy(c =>
            {
                var track = standingOn ?? c.Car.logicCar.FrontBogieTrack;
                return (Track: track, Key: JobKey(c.W, track), Loaded: c.Car.logicCar.CurrentCargoTypeInCar != CargoType.None);
            });
            foreach (var g in groups)
            {
                var list = g.ToList();
                for (var i = 0; i < list.Count; i += MaxCarsPerJob())
                {
                    var chunk = list.Skip(i).Take(MaxCarsPerJob()).ToList();
                    var chain = NextLeg(chunk.Select(c => c.Car).ToList(), chunk.Select(c => c.W).ToList(), g.Key.Track);
                    if (chain == null)
                    {
                        foreach (var c in chunk)
                            if (c.W.Stage != Stage.OffMap)
                                c.W.Stage = Stage.Idle;
                        continue;
                    }
                    chain.FinalizeSetupAndGenerateFirstJob(false);
                    made++;
                }
            }
            return made;
        }

        /// <summary>
        /// What cars share a job by: hauls (an empty to its shipper's station, a load to its
        /// receiver's) go together to the same station, whatever each carries; a load at its
        /// shipper goes with the cars of the same cargo for the same place; an unload with the
        /// cars unloading there; cars leaving with those for the same interchange.
        /// </summary>
        private string JobKey(Waybill w, Track track)
        {
            var nextStation = Economy.StationOf(w.Next) ?? w.Next;
            var here = track?.ID == null || track.ID.IsGeneric() ? null : track.ID.yardId;
            switch (w.Stage)
            {
                // Already at the shipper's station: loading next (NextLeg), so grouped as a load.
                case Stage.ToShipper when nextStation == here:
                    return $"{Stage.Loading}>{w.Next}>{w.Then}>{w.Cargo}";
                case Stage.ToShipper:
                case Stage.ToReceiver:
                    return $"{w.Stage}>{nextStation}";
                case Stage.Loading:
                    return $"{w.Stage}>{w.Next}>{w.Then}>{w.Cargo}";
                case Stage.Unloading:
                    return $"{w.Stage}";
                default:
                    return $"{w.Stage}>{w.Next}";
            }
        }

        /// <summary>The job chain for cars' next leg, by their waybills' stage (all alike); null if none could be made (they go idle).</summary>
        private JobChainController NextLeg(List<TrainCar> cars, List<Waybill> bills, Track track)
        {
            var w = bills[0];
            var random = new Random(Environment.TickCount);
            var hereYard = track?.ID == null || track.ID.IsGeneric() ? null : track.ID.yardId;
            var here = Economy.Station(hereYard);
            if (here == null)
            {
                Main.Warn($"contracts: {cars.Count} car(s) ({cars[0].ID}) stand on {track?.ID?.FullID ?? "no track"}, not at a station; they wait");
                return null;
            }
            var next = Economy.Station(Economy.StationOf(w.Next));
            switch (w.Stage)
            {
                case Stage.ToShipper:
                    if (next == null)
                        return null;
                    if (next == here)
                        return Load(cars, bills, here, track);
                    return PersistentJobs.EmptyHaul(here, next, track, cars, random, null);
                case Stage.Loading:
                    return Load(cars, bills, here, track);
                case Stage.ToReceiver:
                    if (next == null)
                        return null;
                    if (next == here)
                    {
                        foreach (var b in bills)
                            b.Stage = Stage.Unloading;
                        return PersistentJobs.ShuntingUnload(here, track, cars, random);
                    }
                    return PersistentJobs.Transport(here, track, next, cars, Cargo(cars), random, false, null);
                case Stage.Unloading:
                    return PersistentJobs.ShuntingUnload(here, track, cars, random);
                case Stage.ToInterchange:
                {
                    var to = Economy.Station(InterchangeYards.StationOf(w.Next));
                    var length = SingletonBehaviour<CarSpawner>.Instance.GetTotalTrainCarsLength(cars.Select(c => c.logicCar).ToList(), true);
                    var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
                    var into = InterchangeYards.Tracks(w.Next).Where(t => t != track)
                        .OrderByDescending(t => organizer.GetFreeSpaceOnTrack(t)).FirstOrDefault(t => organizer.GetFreeSpaceOnTrack(t) >= length);
                    if (to == null || into == null)
                    {
                        Main.Warn($"contracts: no room at interchange {w.Next} for {cars.Count} car(s)");
                        return null;
                    }
                    return cars.Any(c => c.logicCar.CurrentCargoTypeInCar != CargoType.None)
                        ? PersistentJobs.Transport(here, track, to, cars, Cargo(cars), random, false, into)
                        : PersistentJobs.EmptyHaul(here, to, track, cars, random, into);
                }
                default:
                    return null;
            }
        }

        private static List<CargoType> Cargo(List<TrainCar> cars) => cars.Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();

        /// <summary>A shunting load at the shipper, for the load's next stop: a receiver's station or an interchange's.</summary>
        private JobChainController Load(List<TrainCar> cars, List<Waybill> bills, StationController shipper, Track track)
        {
            var w = bills[0];
            if (w.Cargo == null || !Enum.TryParse(w.Cargo, out CargoType cargo))
                return null;
            // A load the player may not carry is not written (D19); the service re-plans the car.
            if (!LicenseRules.Carrying()(w.Cargo))
            {
                Main.Log($"licenses: {cars.Count} car(s) not loaded with {w.Cargo} at {shipper.stationInfo.YardID}: no license; they go idle");
                return null;
            }
            var machine = shipper.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(new List<CargoType> { cargo }).FirstOrDefault();
            var destination = Economy.Station(Economy.StationOf(w.Then));
            if (machine == null || destination == null)
            {
                Main.Warn($"contracts: {shipper.stationInfo.YardID} can't load {w.Cargo} for {w.Then}");
                return null;
            }
            foreach (var b in bills)
                b.Stage = Stage.Loading;
            // Cars standing on the given track (the group's) are listed there, whatever their bogies say.
            var perTrack = cars.GroupBy(c => c.logicCar.FrontBogieTrack == null || cars.All(x => x.logicCar.FrontBogieTrack != track) ? track : c.logicCar.FrontBogieTrack)
                .Select(g => new CarsPerTrack(g.Key, g.Select(c => c.logicCar).ToList())).ToList();
            return PersistentJobs.ShuntingLoad(shipper, perTrack, machine, destination, cars, cars.Select(_ => cargo).ToList(), false);
        }

        /// <summary>
        /// A chain of contract cars completed: write their next leg now and take them out of the
        /// chain, so the game doesn't register them jobless and Persistent Jobs doesn't reassign them.
        /// </summary>
        internal void OnChainCompleted(JobChainController chain, Job last)
        {
            ReleaseCoaches(chain, last);
            var ours = chain.carsForJobChain.Where(c => c != null && State.Of(c.carGuid) != null).ToList();
            if (ours.Count == 0)
                return;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var next = new List<(TrainCar, Waybill)>();
            foreach (var car in ours)
            {
                chain.carsForJobChain.Remove(car);
                var w = State.Of(car.carGuid);
                var train = registry.logicCarToTrainCar.TryGetValue(car, out var t) ? t : null;
                if (train == null)
                    continue;
                switch (last.jobType)
                {
                    case JobType.EmptyHaul when w.Stage == Stage.ToShipper:
                        w.Stage = Stage.Loading;
                        w.At = w.Next;
                        break;
                    case JobType.ShuntingLoad:
                        // The empty's bill is done; the load's begins.
                        Close(w);
                        w.Stage = Economy.IsInterchange(w.Then) ? Stage.ToInterchange : Stage.ToReceiver;
                        w.At = w.Next;
                        w.Next = w.Then;
                        w.Then = null;
                        Open(w);
                        break;
                    case JobType.Transport when w.Stage == Stage.ToReceiver:
                        w.Stage = Stage.Unloading;
                        w.At = w.Next;
                        break;
                    case JobType.ShuntingUnload:
                        Close(w);
                        w.Stage = Stage.Idle;
                        w.At = w.Next;
                        w.Cargo = null;
                        w.Next = null;
                        continue;
                    case JobType.Transport when w.Stage == Stage.ToInterchange:
                    case JobType.EmptyHaul when w.Stage == Stage.ToInterchange:
                        Close(w);
                        w.Stage = Stage.OffMap;
                        w.At = w.Next;
                        continue;
                    default:
                        w.Stage = Stage.Idle;
                        continue;
                }
                next.Add((train, w));
            }
            // The job is done, so its cars stand on its destination track.
            var made = Dispatch(next, JobDestination(last));
            Main.Log($"contracts: job {last.ID} ({last.jobType}) done for {ours.Count} contract car(s); {made} next job(s)");
        }

        /// <summary>
        /// Whether a job is contract work: it moves cars with a waybill, or the player's own coaches
        /// (a passenger contract's departure, or a ferry for them).
        /// </summary>
        internal bool IsContractJob(Job job)
        {
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            return JobCars(job).Any(c => c != null && (State.Of(c.carGuid) != null
                || registry != null && registry.logicCarToTrainCar.TryGetValue(c, out var t) && Passengers.IsCoach(t)));
        }

        /// <summary>A job's cars, as the jobs manager lists them, else from its tasks.</summary>
        internal static IEnumerable<Car> JobCars(Job job)
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var map = jobs == null || job == null ? null : Traverse.Create(jobs).Field("jobToJobCars").GetValue() as System.Collections.IDictionary;
            if (map != null && map.Contains(job) && map[job] is IEnumerable<Car> cars)
                return cars;
            return CarsOf(job?.tasks);
        }

        private static IEnumerable<Car> CarsOf(IEnumerable<Task> tasks)
        {
            foreach (var t in tasks ?? Enumerable.Empty<Task>())
            {
                var data = t.GetTaskData();
                if (data.nestedTasks != null && data.nestedTasks.Count > 0)
                    foreach (var c in CarsOf(data.nestedTasks))
                        yield return c;
                else if (data.cars != null)
                    foreach (var c in data.cars)
                        yield return c;
            }
        }

        /// <summary>
        /// A chain with the player's coaches is done (a passenger train, or a ferry): a departure's
        /// bill closes, and the coaches leave the chain, so neither Persistent Jobs (which would
        /// make them its own, not owned) nor Passenger Jobs gives them work: the contracts do.
        /// </summary>
        internal void ReleaseCoaches(JobChainController chain, Job last)
        {
            PassengerRuns.Done(State, last, Clock);
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var coaches = chain.carsForJobChain.Where(c => c != null && registry != null && registry.logicCarToTrainCar.TryGetValue(c, out var t) && Passengers.IsCoach(t)).ToList();
            foreach (var c in coaches)
                chain.carsForJobChain.Remove(c);
            if (coaches.Count > 0)
                Main.Log($"passengers: job {last.ID} done; its {coaches.Count} coach(es) wait for a departure");
        }

        /// <summary>A job's last destination track, from its tasks (nested tasks included).</summary>
        internal static Track JobDestination(Job job) => Destination(job?.tasks);

        private static Track Destination(IEnumerable<Task> tasks)
        {
            Track last = null;
            foreach (var t in tasks ?? Enumerable.Empty<Task>())
            {
                var data = t.GetTaskData();
                if (data.nestedTasks != null && data.nestedTasks.Count > 0)
                    last = Destination(data.nestedTasks) ?? last;
                else if (data.destinationTrack != null)
                    last = data.destinationTrack;
            }
            return last;
        }

        [HarmonyPatch(typeof(JobChainController), "OnLastJobInChainCompleted")]
        private static class ChainCompleted
        {
            // First: Persistent Jobs' own prefix then finds the contract cars gone and skips.
            [HarmonyPriority(Priority.First)]
            private static void Prefix(JobChainController __instance, Job lastJobInChain)
            {
                try
                {
                    if (Instance != null && Instance.setUp && __instance?.carsForJobChain != null)
                        Instance.OnChainCompleted(__instance, lastJobInChain);
                }
                catch (Exception e)
                {
                    Main.Error($"contracts: writing the next job after {lastJobInChain?.ID} failed: {e}");
                }
            }
        }
    }
}
