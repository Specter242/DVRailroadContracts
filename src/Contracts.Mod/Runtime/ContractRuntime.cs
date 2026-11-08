using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
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
    ///   empty or loaded. The placement and the jobs go a step a frame (BeginService); a save
    ///   finishes them first.
    /// - **Work is stock jobs** (design, "Waybills and work"), built with Persistent Jobs'
    ///   generators for the cars' next leg: an empty haul to a shipper, a transport to the
    ///   receiver's station or to an interchange track. When a chain of contract cars completes,
    ///   the mod writes the next leg at once and takes the cars out of the finished chain, so
    ///   neither the game nor Persistent Jobs sees them jobless.
    /// - **Industry work** (design, "Industry work"): at its industry's station a car gets a short
    ///   job to spot it on the industry's track (StationMoves); the industry then loads or unloads
    ///   it over time, a car at a time per line at its contract's rate (Contracts.Core
    ///   IndustryWork), and it goes out at once, joining an outbound job not yet taken.
    /// - **Standing.** A contract car's bill is open from when it is billed for a move until the
    ///   move is done (an empty until spotted at its shipper, a load until spotted at its receiver
    ///   or gone at an interchange); the time an industry works a car counts for no bill.
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
        private float timer, passengerTimer, spotTimer;
        private readonly HashSet<string> removing = new HashSet<string>(StringComparer.Ordinal);
        // Cars waiting for room on their industry track, said once in the log.
        private readonly HashSet<string> waitingSaid = new HashSet<string>(StringComparer.Ordinal);

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
            // Persistent Jobs suspending it (FarCarGuard keeps contract cars out; this is in case
            // one gets through): it comes back, so its bill stays.
            if (PersistentJobs.Suspending(car))
            {
                Main.Warn($"contracts: contract car {car.ID} suspended by Persistent Jobs; its bill kept for when it comes back");
                return;
            }
            if (!removing.Remove(guid))
                Main.Warn($"contracts: contract car {car.ID} was deleted by the game");
            State.Remove(guid);
        }

        private void Update()
        {
            if (service != null)
                StepService();
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
                LogJobless("after load");
            }
            // The day moves on once the service under way is done (a second or so).
            if (service != null)
                return;
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
                BeginService(day);
            }
            ContractPay.Collect(State);
            WorkIndustries(Clock ?? 0.0);
            spotTimer -= 2f;
            if (spotTimer <= 0f)
            {
                spotTimer = 15f;
                SpotWaiting();
            }
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
            // The kiosk says what dropped, the next time its Contracts list opens, on its one line:
            // by name if that fits, else how many.
            if (changes.Count > 0)
            {
                var cost = charged > 0.0 ? $": -${charged:N0}" : "";
                var named = string.Join(", ", changes) + cost;
                Kiosk.KioskMenu.Tell(named.Length <= Kiosk.KioskMenu.InfoLineChars ? named : $"{changes.Count} contracts dropped{cost}");
            }
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

        /// <summary>A cut of cars, or cars already on the map, that share the next leg's job.</summary>
        internal sealed class Chunk
        {
            public List<TrainCar> Cars;
            public List<Waybill> Bills;
            public Track Track;
        }

        /// <summary>One interchange's new cars (one entry per car, with its order) and how far they are placed.</summary>
        internal sealed class Fill
        {
            public string Interchange;
            public List<(int Order, TrainCarLivery Livery, CargoType Cargo)> Wanted;
            public TrackFill<Track> Cuts;
        }

        /// <summary>A service under way: its steps, what it has placed, and its counts.</summary>
        internal sealed class ServiceRun
        {
            public int Day, Contracts, Taken, Gone, Asleep, Idled, Recalled, Withdrawn, Bills, SetOut, Placed, Made;
            public double? Opened;
            public readonly StagedWork Work = new StagedWork();
            public double WorkMs, LongestStepMs;
            public string Summary;
            /// <summary>The cuts placed, in order ("track: from+count"), and each step's time with what it did.</summary>
            public readonly List<string> Cuts = new List<string>();
            public readonly List<(string What, double Ms)> Steps = new List<(string, double)>();
            internal string Doing;
            internal List<Chunk> OnTheMap;
            internal List<(Order Order, List<TrainCar> Cars)> Orders;
            internal readonly List<Fill> Fills = new List<Fill>();
            internal int Filling;
        }

        private ServiceRun service;

        /// <summary>A service is under way.</summary>
        public bool Servicing => service != null;

        /// <summary>The last service done, with its work's time (for the probe).</summary>
        public ServiceRun LastService { get; private set; }

        /// <summary>Runs the interchange service now, to the end (tests, the probe); returns a summary.</summary>
        public string Service(int day)
        {
            if (!BeginService(day))
                return "no economy";
            FinishService();
            return LastService?.Summary;
        }

        /// <summary>
        /// Starts the interchange service. Placing a day's cars at once held up the game (146
        /// cars: 178 ms, then a 262 ms frame), so it goes a step a frame (owner, 2026-11-03,
        /// "spread the 06:00 service's car placement over several frames"):
        ///
        /// 1. Now: the passenger timetable, the sweep of the contract cars, the day's plan, and the
        ///    bills for cars already on the map.
        /// 2. A cut of new cars on an interchange track a step, each car billed as it appears (so it
        ///    is a contract car at once), cuts as the service has always made them (TrackFill).
        ///    After each cut, a frame of nothing: the game sets up new cars in the frame after
        ///    they spawn, and that frame then holds no other cut (82 ms frames before it).
        /// 3. Then a job a step: those of the cars on the map first, then the new cuts', grouped as
        ///    before, after every new car is in place (a job onto an interchange track takes a track
        ///    the new cars left free).
        /// 4. The summary.
        ///
        /// The day's cars, tracks and jobs are those of the service at once, unless something else
        /// takes an interchange track in the second or so it lasts. A save first does what is left
        /// (<see cref="Saving"/>): the cars and jobs it saves are the whole service's. The day waits
        /// for it (Tick). Returns false if there is no economy to plan.
        /// </summary>
        public bool BeginService(int day)
        {
            FinishService();
            var passengers = PassengerRuns.Service(State, day);
            if (passengers != null)
                Main.Log("contracts: " + passengers);
            if (Economy?.Data == null)
            {
                Economy = Economy.Build(Notes);
                if (Economy?.Data == null)
                    return false;
            }
            var watch = Stopwatch.StartNew();
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var carrying = LicenseRules.Carrying();
            var run = new ServiceRun { Day = day, Opened = Clock, Withdrawn = WithdrawUncarried(carrying) };
            foreach (var w in State.Waybills.ToList())
            {
                var car = registry?.GetTrainCarByCarGuid(w.Guid);
                // Held by Persistent Jobs' far-car suspension: out of the world for now, not gone.
                if (car == null && PersistentJobs.IsSuspended(w.Guid))
                {
                    run.Asleep++;
                    continue;
                }
                if (car == null)
                {
                    State.Remove(w.Guid);
                    run.Gone++;
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
                        run.Taken++;
                        continue;
                    }
                }
                // Set out for pickup, then moved off the interchange (the player took it): nothing
                // collects it there, so it goes back to idle and the planner bills it again (owner,
                // 2026-11-03: "fix it"). Before, it waited off the map's edge for good.
                if (w.Stage == Stage.OffMap && jobs?.GetJobOfCar(car.logicCar) == null)
                {
                    Main.Log($"contracts: {car.ID} was set out for pickup but stands on {track?.ID?.FullDisplayID ?? "no track"}: back to idle");
                    w.Stage = Stage.Idle;
                    w.At = PlaceOf(car, w);
                    run.Recalled++;
                }
                // At an industry: worked on its track, or waiting for room there. Moved off before it
                // was done, the industry can't work it: back to idle, and the planner bills it again.
                if (Worked(w) && !OnIndustryTrack(car, w))
                    Main.Log($"contracts: {car.ID} was moved off its industry track at {w.Next} before it was done: back to idle");
                var waits = Worked(w) ? OnIndustryTrack(car, w) : WaitsToSpot(car, w);
                if (w.Stage != Stage.Idle && w.Stage != Stage.OffMap && jobs?.GetJobOfCar(car.logicCar) == null && !waits)
                {
                    w.Stage = Stage.Idle;
                    w.Spotted = w.Ready = null;
                    w.At = PlaceOf(car, w);
                    run.Idled++;
                }
                if (w.Stage == Stage.Idle)
                {
                    w.Cargo = car.logicCar.CurrentCargoTypeInCar == CargoType.None ? null : car.logicCar.CurrentCargoTypeInCar.ToString();
                    w.At = PlaceOf(car, w);
                }
            }

            var levels = State.Contracts.Where(c => c.Level >= 1 && (Economy.Industries.ContainsKey(c.Id) || Economy.IsBridge(c.Id)))
                .ToDictionary(c => c.Id, c => c.Level);
            run.Contracts = levels.Count;
            var fleet = State.Waybills.Select(w => new FleetCar
            {
                Guid = w.Guid,
                CarType = registry?.GetTrainCarByCarGuid(w.Guid)?.carLivery?.parentType?.id,
                Location = w.At,
                Cargo = w.Cargo,
                Busy = w.Stage != Stage.Idle,
            }).Where(f => f.CarType != null).ToList();
            var plan = IndustryPlanner.PlanDay(Economy.Data, levels, fleet, new SplitMix64(State.Seed + (ulong)day), carrying);
            run.Bills = plan.Assignments.Count;

            // Bills for cars already on the map; their jobs come after the new cars are placed.
            var onTheMap = new List<(TrainCar Car, Waybill W)>();
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
                            // Already on an interchange track: set out for the next service's pickup, no job.
                            Close(w);
                            w.Stage = Stage.OffMap;
                            run.SetOut++;
                            continue;
                        }
                        w.Stage = Stage.ToInterchange; w.Next = a.Destination; w.Then = null;
                        break;
                }
                Open(w);
                onTheMap.Add((car, w));
            }
            run.OnTheMap = Chunks(onTheMap);

            // The orders, each interchange's cars in order, to fill its empty tracks one after another.
            run.Orders = plan.Orders.Select(o => (Order: o, Cars: new List<TrainCar>())).ToList();
            foreach (var at in run.Orders.GroupBy(r => r.Order.Interchange))
            {
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
                        wanted.Add((run.Orders.FindIndex(x => ReferenceEquals(x.Order, o)), type.liveries[0], cargo));
                }
                run.Fills.Add(new Fill { Interchange = at.Key, Wanted = wanted, Cuts = new TrackFill<Track>(wanted.Count, InterchangeYards.Tracks(at.Key)) });
            }
            service = run;
            run.Work.Then(PlaceNext);
            run.WorkMs += watch.Elapsed.TotalMilliseconds;
            run.LongestStepMs = run.WorkMs;
            return true;
        }

        /// <summary>The service's next step, this frame (Update; the probe).</summary>
        internal void StepService()
        {
            var run = service;
            if (run == null)
                return;
            var watch = Stopwatch.StartNew();
            run.Doing = null;
            run.Work.Step(StepFailed);
            var ms = watch.Elapsed.TotalMilliseconds;
            run.WorkMs += ms;
            run.LongestStepMs = Math.Max(run.LongestStepMs, ms);
            run.Steps.Add((run.Doing, ms));
            if (!run.Work.Pending && service == run)
                service = null;
        }

        /// <summary>Does what is left of the service under way at once: before a save, or a service run to the end.</summary>
        public void FinishService()
        {
            var run = service;
            if (run == null)
                return;
            var watch = Stopwatch.StartNew();
            var steps = run.Work.Finish(StepFailed);
            var ms = watch.Elapsed.TotalMilliseconds;
            run.WorkMs += ms;
            run.LongestStepMs = Math.Max(run.LongestStepMs, ms);
            if (service == run)
                service = null;
            if (steps > 1)
                Main.Log($"contracts: the service's last {steps} step(s) done at once ({ms:F0} ms)");
        }

        private static void StepFailed(Exception e) => Main.Error($"contracts: a service step failed; the service goes on: {e}");

        /// <summary>
        /// A step: the next cut of new cars on its interchange track, then this step again; or,
        /// every cut placed, the jobs a step each and the summary.
        /// </summary>
        private void PlaceNext()
        {
            var run = service;
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            while (run.Filling < run.Fills.Count)
            {
                var fill = run.Fills[run.Filling];
                var cut = fill.Cuts.Next(FreeForCut, t => t.length, (from, count) => CutLength(spawner, fill, from, count));
                if (cut == null)
                {
                    if (fill.Cuts.Unplaced > 0)
                        Main.Warn($"contracts: interchange {fill.Interchange} had no room for {fill.Cuts.Unplaced} of {fill.Cuts.Cars} car(s)");
                    run.Filling++;
                    continue;
                }
                // A cut placed: a frame for the game to set its cars up, with no other cut in it.
                if (Spawn(run, fill, cut, spawner))
                    run.Work.Then(() => run.Doing = "a frame for the cut's cars to settle");
                run.Work.Then(PlaceNext);
                return;
            }

            // Every cut placed: the new cars' bills in the orders' order, as the service has always
            // listed them, then the jobs, a step each.
            var fresh = run.Orders.SelectMany(r => r.Cars).Where(c => c != null).Select(c => State.Of(c.logicCar.carGuid)).Where(w => w != null).ToList();
            var set = new HashSet<Waybill>(fresh);
            State.Waybills.RemoveAll(set.Contains);
            foreach (var w in fresh)
                State.Add(w);
            var placed = run.Orders.SelectMany(r => r.Cars).Where(c => c != null && State.Of(c.logicCar.carGuid) != null)
                .Select(c => (Car: c, W: State.Of(c.logicCar.carGuid))).ToList();
            foreach (var chunk in run.OnTheMap.Concat(Chunks(placed)))
                run.Work.Then(() =>
                {
                    run.Doing = $"a job for {chunk.Cars.Count} car(s)";
                    if (Write(chunk))
                        run.Made++;
                });
            run.Work.Then(EndService);
            run.Doing = "the bills reordered, the jobs grouped";
        }

        /// <summary>An interchange track a cut can go on: no car on it, even in part.</summary>
        private static bool FreeForCut(Track t) =>
            t.GetCarsFullyOnTrack().Count == 0 && t.GetCarsPartiallyOnTrack().Count == 0 && RailTrackRegistry.LogicToRailTrack.ContainsKey(t);

        private static double CutLength(CarSpawner spawner, Fill fill, int from, int count) =>
            spawner.GetTotalCarLiveriesLength(fill.Wanted.Skip(from).Take(count).Select(w => w.Livery).ToList(), true);

#if DEVTOOLS
        /// <summary>
        /// The cuts the service just begun would make placing them all now, as it did before it
        /// went a step a frame (the probe checks the steps make the same).
        /// </summary>
        internal List<string> CutsAtOnce()
        {
            var run = service;
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            var cuts = new List<string>();
            if (run == null || run.Cuts.Count > 0)
                return null;
            foreach (var fill in run.Fills)
            {
                var dry = new TrackFill<Track>(fill.Wanted.Count, InterchangeYards.Tracks(fill.Interchange));
                while (dry.Next(FreeForCut, t => t.length, (from, count) => CutLength(spawner, fill, from, count)) is TrackCut<Track> cut)
                {
                    dry.Spawned(cut);
                    cuts.Add($"{cut.Track.ID.FullDisplayID}: {cut.From}+{cut.Count}");
                }
            }
            return cuts;
        }
#endif

        /// <summary>Spawns a cut, loaded if its orders say so, and bills its cars at once; false if it failed (its cars go to the next track).</summary>
        private bool Spawn(ServiceRun run, Fill fill, TrackCut<Track> cut, CarSpawner spawner)
        {
            var wanted = fill.Wanted.Skip(cut.From).Take(cut.Count).ToList();
            var cars = spawner.SpawnCarTypesOnTrack(wanted.Select(w => w.Livery).ToList(), null, RailTrackRegistry.LogicToRailTrack[cut.Track],
                preventAutoCoupleOnLastCars: true, applyHandbrakeOnLastCars: true);
            if (cars == null || cars.Count != wanted.Count)
            {
                Main.Warn($"contracts: spawning {wanted.Count} car(s) on {cut.Track.ID.FullDisplayID} failed");
                return false;
            }
            fill.Cuts.Spawned(cut);
            run.Cuts.Add($"{cut.Track.ID.FullDisplayID}: {cut.From}+{cut.Count}");
            run.Doing = $"a cut of {cars.Count} car(s) on {cut.Track.ID.FullDisplayID}";
            for (var i = 0; i < cars.Count; i++)
            {
                if (wanted[i].Cargo != CargoType.None)
                    cars[i].logicCar.LoadCargo(cars[i].logicCar.capacity, wanted[i].Cargo);
                var (order, list) = run.Orders[wanted[i].Order];
                list.Add(cars[i]);
                State.Add(new Waybill
                {
                    Guid = cars[i].logicCar.carGuid, Contract = order.Contract, Cargo = order.Cargo,
                    Next = order.BilledTo, Then = order.Then, At = order.Interchange, Opened = run.Opened,
                    Stage = order.Loaded ? (Economy.IsInterchange(order.BilledTo) ? Stage.ToInterchange : Stage.ToReceiver) : Stage.ToShipper,
                });
            }
            run.Placed += cars.Count;
            return true;
        }

        /// <summary>The last step: the service's summary.</summary>
        private void EndService()
        {
            var run = service;
            var short_ = run.Orders.Sum(r => r.Order.Count - r.Cars.Count);
            run.Summary = $"service day {run.Day}: {run.Contracts} contract(s) at work; {run.Taken} car(s) taken off the map, {run.Gone} gone, " +
                          (run.Asleep > 0 ? $"{run.Asleep} suspended by Persistent Jobs (kept), " : "") + $"{run.Idled} back to idle, " +
                          (run.Recalled > 0 ? $"{run.Recalled} moved off an interchange back to idle, " : "") + $"{run.Withdrawn} withdrawn; " +
                          $"{run.Bills} bill(s) ({run.SetOut} set out for pickup), {run.Placed} car(s) placed ({short_} short), {run.Made} job(s)";
            Main.Log($"contracts: {run.Summary} (in {run.Work.Run} steps after the plan, {run.WorkMs:F0} ms of work so far, the longest step {run.LongestStepMs:F0} ms)");
            run.Doing = "the summary";
            Notes.Add(run.Summary);
            LastServiceDay = run.Day;
            LastService = run;
            LogJobless("after the service");
        }

        /// <summary>
        /// The contract cars without a job now, and why (Core JoblessReport): set out on an
        /// interchange track for the pickup, moved off one after, at an industry being loaded or
        /// unloaded (with when it will be done), waiting for room on an industry track, idle, or
        /// their job ended unfinished. For the kiosk's "Cars without jobs" and the log.
        /// </summary>
        internal List<JoblessCar> Jobless()
        {
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var result = new List<JoblessCar>();
            if (registry == null || jobs == null)
                return result;
            foreach (var w in State.Waybills)
            {
                var car = registry.GetTrainCarByCarGuid(w.Guid);
                if (car == null || car.logicCar == null || jobs.GetJobOfCar(car.logicCar) != null)
                    continue;
                var track = car.logicCar.FrontBogieTrack;
                var interchange = InterchangeYards.InterchangeOf(track);
                // A car with no track (derailed, or its bogies not yet on one) says so, not "?".
                var place = interchange != null ? InterchangeYards.CodeOf(interchange) : track == null ? "no track" : track.ID?.yardId;
                var reason = w.Stage == Stage.OffMap ? (interchange != null ? JoblessReason.WaitingForPickup : JoblessReason.MovedOffInterchange)
                    : w.Stage == Stage.Idle ? JoblessReason.Idle
                    : w.Stage == Stage.BeingLoaded ? JoblessReason.BeingLoaded
                    : w.Stage == Stage.BeingUnloaded ? JoblessReason.BeingUnloaded
                    : WaitsToSpot(car, w) ? JoblessReason.WaitingToSpot : JoblessReason.JobEnded;
                // At an industry: when it will be done (once its work has begun).
                var note = Worked(w) && w.Ready is double ready && Clock is double now ? IndustryWork.When(ready, now) : null;
                result.Add(new JoblessCar(car.ID, reason, place, note));
            }
            return result;
        }

        /// <summary>
        /// Each held industry or bridge contract's cars now (Core ContractCars), for the kiosk's
        /// counts and Cars page: those billed to it and those bound for its industry, with what
        /// each needs next, its job and where. A passenger contract has none. Read from the
        /// waybills, the cars and their jobs as they are: nothing is saved.
        /// </summary>
        internal Dictionary<string, List<ContractCar>> CarsOfContracts()
        {
            var result = new Dictionary<string, List<ContractCar>>(StringComparer.Ordinal);
            var held = new List<(string Id, Industry Industry, Bridge Bridge)>();
            foreach (var c in State.Contracts)
            {
                var industry = Economy?.Industries != null && Economy.Industries.TryGetValue(c.Id, out var i) ? i : null;
                var bridge = Economy?.Data?.Bridges.FirstOrDefault(b => b.Id == c.Id);
                if (industry == null && bridge == null)
                    continue;
                result[c.Id] = new List<ContractCar>();
                held.Add((c.Id, industry, bridge));
            }
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            if (held.Count == 0 || registry == null || jobs == null)
                return result;
            var now = Clock;
            foreach (var w in State.Waybills)
            {
                var theirs = held.Where(h => h.Id == w.Contract || (h.Industry != null && h.Id == w.Next)).ToList();
                if (theirs.Count == 0)
                    continue;
                var car = registry.GetTrainCarByCarGuid(w.Guid);
                if (car == null || car.logicCar == null)
                    continue;
                var job = jobs.GetJobOfCar(car.logicCar);
                var track = car.logicCar.FrontBogieTrack;
                var interchange = InterchangeYards.InterchangeOf(track);
                var billed = new BilledCar
                {
                    Id = car.ID, Job = job?.ID, Contract = w.Contract, Next = w.Next, Cargo = w.Cargo,
                    CarType = car.carLivery?.parentType?.id, Stage = StageOf(w.Stage),
                    WaitsToSpot = job == null && WaitsToSpot(car, w), OnInterchange = interchange != null,
                    // As "Cars without jobs" says places: an interchange's code, a station's yard.
                    At = interchange != null ? InterchangeYards.CodeOf(interchange) : track == null ? "no track" : track.ID?.yardId,
                    To = w.Next == null ? null : Economy.IsInterchange(w.Next) ? InterchangeYards.CodeOf(w.Next) : Economy.StationOf(w.Next),
                    Note = Worked(w) && w.Ready is double ready && now is double t ? IndustryWork.When(ready, t) : null,
                };
                foreach (var h in theirs)
                {
                    var seen = h.Bridge != null ? Contracts.Core.Industries.ContractCars.Of(billed, h.Bridge)
                        : Contracts.Core.Industries.ContractCars.Of(billed, h.Industry, Economy.Data);
                    if (seen != null)
                        result[h.Id].Add(seen);
                }
            }
            return result;
        }

        /// <summary>A waybill's stage as the core names it.</summary>
        internal static CarStage StageOf(Stage stage)
        {
            switch (stage)
            {
                case Stage.ToShipper: return CarStage.ToShipper;
                case Stage.Loading: return CarStage.Loading;
                case Stage.ToReceiver: return CarStage.ToReceiver;
                case Stage.Unloading: return CarStage.Unloading;
                case Stage.ToInterchange: return CarStage.ToInterchange;
                case Stage.OffMap: return CarStage.OffMap;
                case Stage.BeingLoaded: return CarStage.BeingLoaded;
                case Stage.BeingUnloaded: return CarStage.BeingUnloaded;
                default: return CarStage.Idle;
            }
        }

        private void LogJobless(string when)
        {
            var line = JoblessReport.LogLine(Jobless());
            Main.Log($"contracts: {when}, " + (line ?? "every contract car has a job"));
        }

        /// <summary>
        /// A save gathers the cars and jobs before the mod's own data (SaveGameManager calls
        /// OnInternalDataUpdate last): the service under way is finished first, so the save holds
        /// all of it.
        /// </summary>
        [HarmonyPatch(typeof(SaveGameManager), "UpdateInternalData")]
        private static class Saving
        {
            private static void Prefix()
            {
                try
                {
                    Instance?.FinishService();
                }
                catch (Exception e)
                {
                    Main.Error($"contracts: finishing the service before a save failed: {e}");
                }
            }
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
                // A car at its industry is the industry's to load or unload; a load is checked when it is ready.
                if (w.Cargo == null || w.Stage == Stage.Idle || w.Stage == Stage.OffMap || Worked(w) || carries(w.Cargo))
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
            foreach (var chunk in Chunks(cars, standingOn))
                if (Write(chunk))
                    made++;
            return made;
        }

        /// <summary>Cars grouped for their next leg's jobs, by where they stand and where they go, each group cut to the licences' train length.</summary>
        private List<Chunk> Chunks(List<(TrainCar Car, Waybill W)> cars, Track standingOn = null)
        {
            var chunks = new List<Chunk>();
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
                    chunks.Add(new Chunk { Cars = chunk.Select(c => c.Car).ToList(), Bills = chunk.Select(c => c.W).ToList(), Track = g.Key.Track });
                }
            }
            return chunks;
        }

        /// <summary>
        /// A group's job, or null if none could be made (its cars go idle). A car gone since the
        /// group was made (the service writes its jobs a step a frame) idles the group; the next
        /// service plans its cars again.
        /// </summary>
        private bool Write(Chunk chunk)
        {
            var gone = chunk.Cars.Any(c => c == null || State.Of(c.logicCar?.carGuid) == null);
            if (!gone && SpotLeg(chunk.Bills[0], chunk.Track))
                return WriteSpot(chunk);
            var chain = gone ? null : NextLeg(chunk.Cars, chunk.Bills, chunk.Track);
            if (chain == null)
            {
                var first = chunk.Bills[0];
                Main.Warn($"contracts: no job for {chunk.Cars.Count} car(s) ({string.Join(", ", chunk.Cars.Where(c => c != null).Select(c => c.ID))}) " +
                          $"on {chunk.Track?.ID?.FullDisplayID ?? "no track"}, {first.Stage} to {first.Next ?? "?"}" +
                          (gone ? " (a car of theirs is gone)" : "") + "; they go idle until the next service");
                foreach (var w in chunk.Bills)
                    if (w.Stage != Stage.OffMap)
                        w.Stage = Stage.Idle;
                return false;
            }
            chain.FinalizeSetupAndGenerateFirstJob(false);
            return true;
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
            // At its industry's station: spotted next, with the cars for the same industry tracks.
            if (SpotLeg(w, track))
            {
                var cargo = w.Cargo != null && Enum.TryParse(w.Cargo, out CargoType c) ? c : CargoType.None;
                var tracks = IndustryTracks(Economy.Station(here), cargo).Select(t => t.ID.FullID).OrderBy(id => id, StringComparer.Ordinal);
                return $"spot>{w.Next}>{(Loads(w) ? "load" : "unload")}>{string.Join("+", tracks)}";
            }
            switch (w.Stage)
            {
                case Stage.ToShipper:
                case Stage.ToReceiver:
                    return $"{w.Stage}>{nextStation}";
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
                    // A freed empty for a shipper at the station it was freed at: off the industry track, into storage.
                    if (next == here)
                        return Pull(cars, here, track);
                    return PersistentJobs.EmptyHaul(here, next, track, cars, random, null);
                case Stage.ToReceiver:
                    if (next == null)
                        return null;
                    return PersistentJobs.Transport(here, track, next, cars, Cargo(cars), random, false, null);
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

        // ------------------------------------------------------------ industry work

        /// <summary>Whether a car is at its industry, being loaded or unloaded.</summary>
        internal static bool Worked(Waybill w) => w.Stage == Stage.BeingLoaded || w.Stage == Stage.BeingUnloaded;

        /// <summary>Whether a car's industry loads it (an empty for its shipper) rather than unloads it.</summary>
        private static bool Loads(Waybill w) => w.Stage == Stage.ToShipper || w.Stage == Stage.Loading || w.Stage == Stage.BeingLoaded;

        /// <summary>
        /// Whether a car standing on a track is at its industry's station to be spotted there: an
        /// empty with a load planned at its shipper, or a load at its receiver.
        /// </summary>
        private bool SpotLeg(Waybill w, Track track)
        {
            var here = track?.ID == null || track.ID.IsGeneric() ? null : track.ID.yardId;
            if (here == null || Economy.StationOf(w.Next) != here || !Economy.Industries.ContainsKey(w.Next))
                return false;
            return w.Stage == Stage.Loading || w.Stage == Stage.Unloading || w.Stage == Stage.ToReceiver
                   || w.Stage == Stage.ToShipper && w.Cargo != null;
        }

        /// <summary>The cargo a car's industry track handles: the load to be, or the load it carries.</summary>
        private static CargoType TrackCargo(TrainCar car, Waybill w)
        {
            if (!Loads(w))
                return car.logicCar.CurrentCargoTypeInCar;
            return w.Cargo != null && Enum.TryParse(w.Cargo, out CargoType cargo) ? cargo : CargoType.None;
        }

        /// <summary>
        /// The tracks at a station where its industry works a cargo (design, "Industry spots"):
        /// the spot tracks that name it (Contracts.Core IndustrySpots: the Steel Mill's steel under
        /// its portal crane), else those of the warehouse machines that handle it; for a cargo none
        /// handles there (the scrap the mod adds to the mines), all of the machines' tracks, as the
        /// industry loads and unloads its cars itself.
        /// </summary>
        internal static List<Track> IndustryTracks(StationController station, CargoType cargo)
        {
            if (station == null || cargo == CargoType.None)
                return new List<Track>();
            var yardId = station.stationInfo.YardID;
            var spots = IndustrySpots.TracksFor(yardId, cargo.ToString());
            if (spots.Count > 0)
            {
                var tracks = spots.Select(part => StationTrack(yardId, part)).Where(t => t != null).ToList();
                if (tracks.Count > 0)
                    return tracks;
                if (missingSpots.Add($"{yardId} {cargo}"))
                    Main.Warn($"contracts: none of {yardId}'s spot tracks for {cargo} ({string.Join(", ", spots)}) is on the map; its warehouse track serves");
            }
            return MachineTracks(station, cargo);
        }

        /// <summary>
        /// The tracks of a station's warehouse machines that handle a cargo; for a cargo none
        /// handles there, all of the machines' tracks. Up to 0.1.7 every cargo was worked there.
        /// </summary>
        private static List<Track> MachineTracks(StationController station, CargoType cargo)
        {
            var yard = station?.logicStation?.yard;
            if (yard == null || cargo == CargoType.None)
                return new List<Track>();
            var handling = yard.GetWarehouseMachinesThatSupportCargoTypes(new List<CargoType> { cargo }) ?? new List<WarehouseMachine>();
            var machines = handling.Count > 0 ? handling : yard.WarehouseMachines?.Where(m => m?.WarehouseTrack != null).ToList() ?? new List<WarehouseMachine>();
            return machines.Select(m => m.WarehouseTrack).Where(t => t != null).Distinct().ToList();
        }

        private static readonly HashSet<string> missingSpots = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A station's track by its name within the station ("B7S"), or null.</summary>
        private static Track StationTrack(string yardId, string part) =>
            RailTrackRegistry.LogicToRailTrack.Keys.FirstOrDefault(t => t?.ID != null && t.ID.yardId == yardId && t.ID.TrackPartOnly == part);

        /// <summary>
        /// The track of its industry a car stands on, where its cargo is worked; null if none. A car
        /// already being worked on one of its industry's warehouse machine tracks for its cargo, as
        /// every car was spotted up to 0.1.7, is worked there to the end: new cars go to the spots
        /// (RF audit, 2026-11-06: the owner's career had cars being loaded on the Steel Mill's A7L
        /// and the Farm's A1L, which would otherwise start over at the next service).
        /// </summary>
        private Track SpotUnder(TrainCar car, Waybill w)
        {
            var track = car.logicCar.FrontBogieTrack;
            if (track == null)
                return null;
            var station = Economy.Station(Economy.StationOf(w.Next));
            var cargo = TrackCargo(car, w);
            var spot = IndustryTracks(station, cargo).FirstOrDefault(t => t == track);
            if (spot != null || !Worked(w))
                return spot;
            return MachineTracks(station, cargo).FirstOrDefault(t => t == track);
        }

        /// <summary>Whether a car stands on a track of its industry where its cargo is worked.</summary>
        internal bool OnIndustryTrack(TrainCar car, Waybill w) => SpotUnder(car, w) != null;

        /// <summary>Whether a car with no job waits at its industry's station for room on the industry's track (SpotWaiting).</summary>
        internal bool WaitsToSpot(TrainCar car, Waybill w) =>
            (w.Stage == Stage.Loading || w.Stage == Stage.Unloading) && SpotLeg(w, car.logicCar.FrontBogieTrack);

        /// <summary>
        /// Spots cars at their industry (design, "Industry work"): a job to move them onto the
        /// industry's track for their cargo, the one with the most room, taking as many as fit.
        /// Those that don't fit wait at the station, without a job, until there is room
        /// (SpotWaiting). Cars already on it are spotted where they stand. A load the player may
        /// not carry is not loaded (D19): its empty goes idle, and the service plans it again.
        /// Returns whether a job was made.
        /// </summary>
        private bool WriteSpot(Chunk chunk)
        {
            var cars = chunk.Cars;
            var bills = chunk.Bills;
            var track = chunk.Track;
            var w = bills[0];
            var loading = Loads(w);
            foreach (var b in bills)
                b.Stage = loading ? Stage.Loading : Stage.Unloading;
            var here = Economy.Station(track.ID.yardId);
            void GoIdle(string why)
            {
                Main.Warn($"contracts: {cars.Count} car(s) ({string.Join(", ", cars.Select(c => c.ID))}) not spotted at {w.Next}: {why}; they go idle until the next service");
                foreach (var b in bills)
                    b.Stage = Stage.Idle;
            }
            if (loading && !LicenseRules.Carrying()(w.Cargo))
            {
                GoIdle($"no license for {w.Cargo}");
                return false;
            }
            var cargo = TrackCargo(cars[0], w);
            var spots = IndustryTracks(here, cargo);
            if (spots.Count == 0)
            {
                GoIdle($"no track there {(loading ? "loads" : "unloads")} {cargo}");
                return false;
            }
            if (spots.Contains(track))
            {
                foreach (var b in bills)
                    BeginWork(b);
                Main.Log($"contracts: {cars.Count} car(s) ({string.Join(", ", cars.Select(c => c.ID))}) spotted where they stand, on {track.ID.FullDisplayID}");
                return false;
            }
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            var best = spots.OrderByDescending(Room).First();
            var room = Room(best);
            var fit = 0;
            while (fit < cars.Count && spawner.GetTotalTrainCarsLength(cars.Take(fit + 1).Select(c => c.logicCar).ToList(), true) <= room)
                fit++;
            foreach (var c in cars.Skip(fit))
                if (waitingSaid.Add(c.logicCar.carGuid))
                    Main.Log($"contracts: {c.ID} waits at {here.stationInfo.YardID} for room on {best.ID.FullDisplayID} ({room:F0} m free)");
            if (fit == 0)
                return false;
            var spotted = cars.Take(fit).ToList();
            var chain = StationMoves.Move(here, track, best, spotted, loading);
            if (chain == null)
            {
                GoIdle(loading ? "loaded or mixed cars to be loaded" : "mixed loaded and empty cars");
                return false;
            }
            foreach (var c in spotted)
                waitingSaid.Remove(c.logicCar.carGuid);
            chain.FinalizeSetupAndGenerateFirstJob(false);
            Main.Log($"contracts: {spotted.Count} car(s) ({string.Join(", ", spotted.Select(c => c.ID))}) to be spotted on {best.ID.FullDisplayID} at {w.Next} " +
                     $"({spawner.GetTotalTrainCarsLength(spotted.Select(c => c.logicCar).ToList(), true):F0} m of {room:F0} m free)");
            return true;
        }

        /// <summary>
        /// The room left on an industry track: its free length, less the cars of spot jobs not yet
        /// taken that head for it (Persistent Jobs reserves a job's track only when it is taken).
        /// </summary>
        private double Room(Track track)
        {
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var heading = new List<Car>();
            foreach (var w in State.Waybills.Where(w => w.Stage == Stage.Loading || w.Stage == Stage.Unloading))
            {
                var car = registry?.GetTrainCarByCarGuid(w.Guid);
                var job = car == null ? null : jobs?.GetJobOfCar(car.logicCar);
                if (job != null && job.State == JobState.Available && car.logicCar.FrontBogieTrack != track && JobDestination(job) == track)
                    heading.Add(car.logicCar);
            }
            var pending = heading.Count == 0 ? 0f : SingletonBehaviour<CarSpawner>.Instance.GetTotalCarsLength(heading, true);
            return organizer.GetFreeSpaceOnTrack(track) - pending;
        }

        /// <summary>
        /// A freed empty off its industry's track into the station's storage, the track with the
        /// most room that holds it: one that is no industry's spot, else (at a station whose
        /// storage tracks are all spots, as the Sawmill's) another spot; null if none holds it.
        /// </summary>
        private static JobChainController Pull(List<TrainCar> cars, StationController station, Track track)
        {
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            var length = SingletonBehaviour<CarSpawner>.Instance.GetTotalTrainCarsLength(cars.Select(c => c.logicCar).ToList(), true);
            var spots = IndustrySpots.TracksAt(station.stationInfo.YardID);
            var into = station.logicStation.yard.StorageTracks.Where(t => t != track && organizer.GetFreeSpaceOnTrack(t) >= length)
                .OrderBy(t => spots.Contains(t.ID.TrackPartOnly)).ThenByDescending(t => organizer.GetFreeSpaceOnTrack(t)).FirstOrDefault();
            if (into == null)
            {
                Main.Warn($"contracts: no storage track at {station.stationInfo.YardID} holds {cars.Count} empty car(s) off its industry track");
                return null;
            }
            return StationMoves.Move(station, track, into, cars, forLoading: false);
        }

        /// <summary>
        /// A car spotted at its industry: the move there is done, so its bill closes (the
        /// railroad's part; the industry's time counts for no bill), and the industry's work begins.
        /// </summary>
        private void BeginWork(Waybill w)
        {
            Close(w);
            w.Stage = Loads(w) ? Stage.BeingLoaded : Stage.BeingUnloaded;
            w.At = w.Next;
            w.Spotted = Clock;
            w.Ready = null;
        }

        /// <summary>The level a contract works at: its own, or 1 if it is not held or not yet started.</summary>
        private int LevelOf(string id) => Math.Max(1, State.Contracts.FirstOrDefault(c => c.Id == id)?.Level ?? 1);

        /// <summary>
        /// The industries' work brought up to <paramref name="now"/> (Tick; the probe, with a
        /// later clock): each line's queue of spotted cars advances at its contract's rate
        /// (Contracts.Core IndustryWork), and the cars done go out at once (SendOut). A car done
        /// but no longer on its industry track waits until it is back on it (the service sends it
        /// back to idle). Returns the cars that went out.
        /// </summary>
        internal int WorkIndustries(double now)
        {
            if (Economy?.Data == null || service != null)
                return 0;
            var working = State.Waybills.Where(Worked).ToList();
            if (working.Count == 0)
                return 0;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var done = new List<(TrainCar Car, Waybill W, double Ready)>();
            foreach (var line in working.GroupBy(w =>
                     {
                         Economy.Industries.TryGetValue(w.Next ?? "", out var industry);
                         var cargoLine = IndustryWork.LineFor(industry, w.Cargo, w.Stage == Stage.BeingLoaded);
                         return (w.Next, w.Stage, Line: cargoLine?.ToString() ?? w.Cargo);
                     }))
            {
                var first = line.First();
                Economy.Industries.TryGetValue(first.Next ?? "", out var industry);
                var cargoLine = IndustryWork.LineFor(industry, first.Cargo, first.Stage == Stage.BeingLoaded);
                var daysPerCar = industry == null || cargoLine == null ? 1.0
                    : IndustryWork.DaysPerCar(IndustryWork.CarsPerDay(industry, cargoLine, LevelOf(industry.Id)));
                var queue = line.Select(w => (W: w, Car: new WorkingCar(w.Guid, w.Spotted ?? now, w.Ready))).ToList();
                var finished = IndustryWork.Advance(queue.Select(q => q.Car), daysPerCar, now);
                foreach (var (w, car) in queue)
                {
                    w.Spotted = car.Spotted;
                    w.Ready = car.Ready;
                }
                foreach (var f in finished)
                {
                    var w = queue.First(q => q.Car == f).W;
                    var train = registry?.GetTrainCarByCarGuid(w.Guid);
                    if (train != null && OnIndustryTrack(train, w))
                        done.Add((train, w, f.Ready.Value));
                }
            }
            if (done.Count == 0)
                return 0;
            var levels = State.Contracts.Where(c => c.Level >= 1 && Economy.Industries.ContainsKey(c.Id)).ToDictionary(c => c.Id, c => c.Level);
            var carrying = LicenseRules.Carrying();
            var outbound = new List<(TrainCar, Waybill)>();
            foreach (var (car, w, ready) in done)
            {
                try
                {
                    if (Finish(car, w, ready, levels, carrying))
                        outbound.Add((car, w));
                }
                catch (Exception e)
                {
                    Main.Error($"contracts: {car.ID} at {w.Next}: finishing its industry work failed; idle until the next service: {e}");
                    w.Stage = Stage.Idle;
                    w.Spotted = w.Ready = null;
                }
            }
            var made = SendOut(outbound);
            Main.Log($"contracts: industries done with {done.Count} car(s) ({string.Join(", ", done.Select(d => $"{d.Car.ID} {(d.W.Cargo == null ? "emptied" : "loaded")} at {d.W.At}"))}); {made} outbound job(s)");
            return done.Count;
        }

        /// <summary>The machine to name as a load's origin or an unload's destination: one of the station's that handles the cargo, wherever the car is worked.</summary>
        private static WarehouseMachine Handling(StationController station, CargoType cargo) =>
            station?.logicStation?.yard?.WarehouseMachines?.FirstOrDefault(m => m != null && m.IsCargoSupported(cargo));

        /// <summary>
        /// The industry is done with a car: loaded, it is billed on to its load's destination;
        /// emptied, it goes where an empty is wanted (Contracts.Core IndustryPlanner.RouteFreedEmpty).
        /// Its bill opens when it was ready. False if it goes idle instead (no license for its load).
        /// </summary>
        private bool Finish(TrainCar car, Waybill w, double ready, IReadOnlyDictionary<string, int> levels, Func<string, bool> carrying)
        {
            var station = Economy.Station(Economy.StationOf(w.Next));
            var logic = car.logicCar;
            w.Spotted = w.Ready = null;
            if (w.Stage == Stage.BeingLoaded)
            {
                w.At = w.Next;
                if (w.Cargo == null || !Enum.TryParse(w.Cargo, out CargoType cargo) || !carrying(w.Cargo) || logic.CurrentCargoTypeInCar != CargoType.None)
                {
                    Main.Log($"contracts: {car.ID} not loaded with {w.Cargo ?? "nothing"} at {w.Next}: {(logic.CurrentCargoTypeInCar != CargoType.None ? "already loaded" : "no license")}; idle");
                    w.Stage = Stage.Idle;
                    return false;
                }
                logic.LoadCargo(logic.capacity, cargo, Handling(station, cargo));
                w.Stage = Economy.IsInterchange(w.Then) ? Stage.ToInterchange : Stage.ToReceiver;
                w.Next = w.Then;
                w.Then = null;
            }
            else
            {
                if (logic.CurrentCargoTypeInCar != CargoType.None)
                    logic.UnloadCargo(logic.LoadedCargoAmount, logic.CurrentCargoTypeInCar, Handling(station, logic.CurrentCargoTypeInCar));
                w.At = w.Next;
                w.Cargo = null;
                w.Then = null;
                var a = IndustryPlanner.RouteFreedEmpty(Economy.Data, levels, w.Guid, car.carLivery?.parentType?.id, w.At,
                    new SplitMix64(State.Seed + (ulong)Math.Max(0.0, Math.Floor(ready * 1440))), carrying);
                if (a.Kind == AssignmentKind.EmptyToShipper)
                {
                    w.Stage = Stage.ToShipper;
                    w.Contract = a.Shipper;
                }
                else
                    w.Stage = Stage.ToInterchange;
                w.Next = a.Destination;
            }
            w.Opened = ready;
            return true;
        }

        /// <summary>
        /// Cars an industry is done with go out at once (owner, 2026-11-04: "add it to the outbound
        /// as soon as it's ready"): each joins an outbound job not yet taken from its track to the
        /// same place, which is written again with it in it (up to the licenses' train length);
        /// with none, it gets a job of its own. Returns the jobs made.
        /// </summary>
        private int SendOut(List<(TrainCar Car, Waybill W)> cars)
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var all = new List<(TrainCar Car, Waybill W)>(cars);
            var mine = new HashSet<Car>(cars.Select(c => c.Car.logicCar));
            var withdrawn = new HashSet<Job>();
            foreach (var g in cars.GroupBy(c => (Track: c.Car.logicCar.FrontBogieTrack, Key: JobKey(c.W, c.Car.logicCar.FrontBogieTrack))))
            {
                if (g.Key.Track == null || jobs == null || registry == null)
                    continue;
                foreach (var other in g.Key.Track.GetCarsFullyOnTrack().Where(c => !mine.Contains(c)))
                {
                    var job = jobs.GetJobOfCar(other);
                    if (job == null || job.State != JobState.Available || withdrawn.Contains(job))
                        continue;
                    // Joined only if all its cars are contract cars on this track, going the same way.
                    var theirs = new List<(TrainCar Car, Waybill W)>();
                    foreach (var c in JobCars(job))
                    {
                        var w = State.Of(c?.carGuid);
                        if (w == null || c.FrontBogieTrack != g.Key.Track || JobKey(w, g.Key.Track) != g.Key.Key
                            || !registry.logicCarToTrainCar.TryGetValue(c, out var train) || train == null)
                        {
                            theirs = null;
                            break;
                        }
                        theirs.Add((train, w));
                    }
                    if (theirs == null || theirs.Count == 0)
                        continue;
                    withdrawn.Add(job);
                    Withdraw(job);
                    Main.Log($"contracts: job {job.ID} ({theirs.Count} car(s)) written again with the cars just done on {g.Key.Track.ID.FullDisplayID}");
                    foreach (var t in theirs)
                        if (mine.Add(t.Car.logicCar))
                            all.Add(t);
                }
            }
            return Dispatch(all);
        }

        /// <summary>
        /// Cars waiting at their industry's station for room on its track (WriteSpot), tried
        /// again: those that fit now are spotted (Tick, every 15 s).
        /// </summary>
        internal void SpotWaiting()
        {
            if (Economy?.Data == null || service != null)
                return;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            if (registry == null || jobs == null)
                return;
            var waiting = new List<(TrainCar, Waybill)>();
            foreach (var w in State.Waybills.Where(w => w.Stage == Stage.Loading || w.Stage == Stage.Unloading))
            {
                var car = registry.GetTrainCarByCarGuid(w.Guid);
                if (car != null && jobs.GetJobOfCar(car.logicCar) == null && WaitsToSpot(car, w))
                    waiting.Add((car, w));
            }
            if (waiting.Count > 0)
                Dispatch(waiting);
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
                    // A freed empty at the shipper it was sent to: it waits there, idle, for the next service.
                    case JobType.EmptyHaul when w.Stage == Stage.ToShipper && w.Cargo == null:
                        Close(w);
                        w.Stage = Stage.Idle;
                        w.At = w.Next;
                        w.Next = null;
                        continue;
                    case JobType.EmptyHaul when w.Stage == Stage.ToShipper:
                        w.Stage = Stage.Loading;
                        w.At = w.Next;
                        break;
                    // Spotted on its industry's track (StationMoves): the industry's work begins.
                    case JobType.EmptyHaul when w.Stage == Stage.Loading:
                    case JobType.Transport when w.Stage == Stage.Unloading:
                        BeginWork(w);
                        continue;
                    // A stock shunting load or unload, written before the industries worked cars themselves.
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
