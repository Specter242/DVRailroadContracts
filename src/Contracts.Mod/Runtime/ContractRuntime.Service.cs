using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Core.Sim;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using static System.FormattableString;

namespace Contracts.Mod.Runtime
{
    internal sealed partial class ContractRuntime
    {
        // ------------------------------------------------------------ service

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
            /// <summary>Cars, cuts or jobs the service could not deal with (each said in the log); the rest went on.</summary>
            public int Failed;
            /// <summary>The service come again for the cars it had no room for, not a day's own.</summary>
            public bool Retry;
            /// <summary>What each contract asked for this day against its level's full day, in carloads (a line the player may not carry asks none).</summary>
            public readonly Dictionary<string, (int Asked, int Daily)> Requests = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
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
            // The day is planned afresh: the cars the last service still had no room for are dropped.
            var dropped = ServiceRetry.Cars(State.Waiting.Select(w => w.Order()));
            if (dropped > 0)
                Main.Log($"contracts: {dropped} car(s) of the last service never found room on an interchange; dropped for the new day's orders");
            State.Waiting.Clear();
            State.RetryAt = null;
            var run = new ServiceRun { Day = day, Opened = Clock, Withdrawn = WithdrawUncarried(carrying) };
            foreach (var w in State.Waybills.ToList())
            {
                // A car at a time: one whose record or car throws is left for the next service, and the rest go on.
                try
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
                    if (LeavesTheMap(w, car, jobs))
                    {
                        TakeAway(w, car);
                        run.Taken++;
                        continue;
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
                catch (Exception e)
                {
                    run.Failed++;
                    Main.Error($"contracts: the service could not deal with car {w.Guid}; it is left as it is: {e}");
                }
            }

            var levels = State.Contracts.Where(c => c != null && !string.IsNullOrEmpty(c.Id) && c.Level >= 1 && (Economy.Industries.ContainsKey(c.Id) || Economy.IsBridge(c.Id)))
                .GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Level, StringComparer.Ordinal);
            run.Contracts = levels.Count;
            var fleet = State.Waybills.Select(w => new FleetCar
            {
                Guid = w.Guid,
                CarType = registry?.GetTrainCarByCarGuid(w.Guid)?.carLivery?.parentType?.id,
                Location = w.At,
                Cargo = w.Cargo,
                Busy = w.Stage != Stage.Idle,
            }).Where(f => f.CarType != null).ToList();
            // Cars come on each industry's request: its level's carloads less its cars still in work
            // (owner, 2026-11-10; Contracts.Core WorkInHand, IndustryPlanner.Requests).
            var inHand = InHand();
            var plan = IndustryPlanner.PlanDay(Economy.Data, levels, fleet, new SplitMix64(State.Seed + (ulong)day), carrying, inHand, day);
            run.Bills = plan.Assignments.Count;
            foreach (var kv in levels)
            {
                var industry = Economy.Industries.TryGetValue(kv.Key, out var found) ? found : null;
                var bridge = industry == null ? Economy.Data.Bridges.FirstOrDefault(b => b.Id == kv.Key) : null;
                var daily = industry != null ? Enum.GetValues(typeof(LineRole)).Cast<LineRole>().Sum(r => industry.Daily(r, kv.Value))
                    : bridge != null ? 2 * IndustrySettings.DailyCarloads(bridge.Level5Carloads, kv.Value) : 0;
                var asked = industry != null ? (plan.Industries.TryGetValue(kv.Key, out var its) ? its.Due.Values.Sum() : 0)
                    : plan.Bridges.TryGetValue(kv.Key, out var loads) ? loads : 0;
                run.Requests[kv.Key] = (asked, daily);
            }
            var held = run.Requests.Where(r => r.Value.Asked < r.Value.Daily).ToList();
            if (held.Count > 0)
                Main.Log("contracts: asking for less than a full day, with cars still in work: " +
                         string.Join(", ", held.Select(r => $"{r.Key} {r.Value.Asked} of {r.Value.Daily}")));

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

            run.Orders = plan.Orders.Select(o => (Order: o, Cars: new List<TrainCar>())).ToList();
            AddFills(run);
            service = run;
            run.Work.Then(PlaceNext);
            run.WorkMs += watch.Elapsed.TotalMilliseconds;
            run.LongestStepMs = run.WorkMs;
            return true;
        }

        /// <summary>A car the service takes off the map: on an interchange track, set out for pickup or brought there with its job done.</summary>
        private static bool LeavesTheMap(Waybill w, TrainCar car, JobsManager jobs) =>
            InterchangeYards.IsInterchangeTrack(car.logicCar.FrontBogieTrack)
            && (w.Stage == Stage.OffMap || w.Stage == Stage.ToInterchange && jobs?.GetJobOfCar(car.logicCar) == null);

        private void TakeAway(Waybill w, TrainCar car)
        {
            removing.Add(w.Guid);
            SingletonBehaviour<CarSpawner>.Instance.DeleteCar(car);
            State.Remove(w.Guid);
        }

        /// <summary>
        /// The service again, for the cars it had no room for (owner, 2026-11-10: "if an
        /// interchange is full it needs to attempt serving again later"; Contracts.Core
        /// ServiceRetry). It takes away the cars that leave the map, as a day's service does,
        /// then places the waiting cars that now fit and writes their jobs, a step a frame like
        /// the day's. It plans nothing: the cars on the map keep their bills. What still waits
        /// after it waits for the next. Returns false with nothing left to place.
        /// </summary>
        public bool BeginRetry()
        {
            FinishService();
            var held = new HashSet<string>(State.Contracts.Select(c => c.Id), StringComparer.Ordinal);
            var orders = ServiceRetry.Held(State.Waiting.Select(w => w.Order()), held.Contains);
            if (orders.Count == 0)
            {
                State.Waiting.Clear();
                State.RetryAt = null;
                return false;
            }
            var watch = Stopwatch.StartNew();
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var run = new ServiceRun { Day = State.ServiceDay, Opened = Clock, Retry = true, Contracts = orders.Select(o => o.Contract).Distinct().Count() };
            foreach (var w in State.Waybills.ToList())
            {
                var car = registry?.GetTrainCarByCarGuid(w.Guid);
                if (car == null || car.logicCar == null || !LeavesTheMap(w, car, jobs))
                    continue;
                TakeAway(w, car);
                run.Taken++;
            }
            run.OnTheMap = new List<Chunk>();
            run.Orders = orders.Select(o => (Order: o, Cars: new List<TrainCar>())).ToList();
            AddFills(run);
            service = run;
            run.Work.Then(PlaceNext);
            run.WorkMs += watch.Elapsed.TotalMilliseconds;
            run.LongestStepMs = run.WorkMs;
            return true;
        }

        /// <summary>Runs a retry now, to the end (the probe); returns its summary, or null with nothing waiting.</summary>
        public string Retry()
        {
            if (!BeginRetry())
                return null;
            FinishService();
            return LastService?.Summary;
        }

        /// <summary>Whether an order's cars can be made at all: the game has its car type, and its cargo if it comes loaded.</summary>
        private static bool Placeable(Order o, out TrainCarLivery livery, out CargoType cargo)
        {
            livery = null;
            cargo = CargoType.None;
            if (!Globals.G.Types.TryGetCarType(o.CarType, out var type) || type.liveries == null || type.liveries.Count == 0)
                return false;
            livery = type.liveries[0];
            return !o.Loaded || o.Cargo != null && Enum.TryParse(o.Cargo, out cargo);
        }

        /// <summary>The run's orders as fills: each interchange's cars in order, to fill its empty tracks one after another.</summary>
        private static void AddFills(ServiceRun run)
        {
            foreach (var at in run.Orders.GroupBy(r => r.Order.Interchange))
            {
                var wanted = new List<(int Order, TrainCarLivery Livery, CargoType Cargo)>();
                foreach (var r in at)
                {
                    var o = r.Order;
                    if (!Placeable(o, out var livery, out var cargo))
                    {
                        Main.Warn($"contracts: no car type {o.CarType}{(o.Loaded ? $" or cargo {o.Cargo}" : "")} for an order of {o.Count}");
                        continue;
                    }
                    for (var i = 0; i < o.Count; i++)
                        wanted.Add((run.Orders.FindIndex(x => ReferenceEquals(x.Order, o)), livery, cargo));
                }
                run.Fills.Add(new Fill { Interchange = at.Key, Wanted = wanted, Cuts = new TrackFill<Track>(wanted.Count, InterchangeYards.Tracks(at.Key)) });
            }
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
                Main.Log(Invariant($"contracts: the service's last {steps} step(s) done at once ({ms:F0} ms)"));
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
                try
                {
                    var cut = fill.Cuts.Next(FreeForCut, t => t.length, (from, count) => CutLength(spawner, fill, from, count));
                    if (cut == null)
                    {
                        if (fill.Cuts.Unplaced > 0)
                            Main.Warn($"contracts: interchange {fill.Interchange} had no room for {fill.Cuts.Unplaced} of {fill.Cuts.Cars} car(s); they wait for the service to come again");
                        run.Filling++;
                        continue;
                    }
                    // A cut placed: a frame for the game to set its cars up, with no other cut in it.
                    if (Spawn(run, fill, cut, spawner))
                        run.Work.Then(() => run.Doing = "a frame for the cut's cars to settle");
                }
                catch (Exception e)
                {
                    // The rest of this interchange's cars wait for the service to come again; the
                    // other interchange, the jobs and the summary still follow. (A cut that threw
                    // used to end the day's service there, with no jobs for the cars on the map.)
                    run.Failed++;
                    run.Filling++;
                    Main.Error($"contracts: placing new cars at {fill.Interchange} failed; its {fill.Cuts.Unplaced} car(s) left wait for the service to come again: {e}");
                }
                run.Work.Then(PlaceNext);
                return;
            }

            // Every cut placed: the new cars' bills in the orders' order, as the service has always
            // listed them, then the jobs, a step each. The summary follows whatever happens here.
            try
            {
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
                        try
                        {
                            run.Made += Write(chunk);
                        }
                        catch (Exception e)
                        {
                            run.Failed++;
                            Main.Error($"contracts: writing a job for {chunk.Cars.Count} car(s) failed; the next service plans them again: {e}");
                        }
                    });
                run.Doing = "the bills reordered, the jobs grouped";
            }
            catch (Exception e)
            {
                run.Failed++;
                Main.Error($"contracts: grouping the service's jobs failed; the next service plans the cars again: {e}");
            }
            finally
            {
                run.Work.Then(EndService);
            }
        }

        /// <summary>An interchange track a cut can go on: no car on it, even in part.</summary>
        private static bool FreeForCut(Track t) =>
            t.GetCarsFullyOnTrack().Count == 0 && t.GetCarsPartiallyOnTrack().Count == 0 && RailTrackRegistry.LogicToRailTrack.ContainsKey(t);

        private static double CutLength(CarSpawner spawner, Fill fill, int from, int count) =>
            spawner.GetTotalCarLiveriesLength(fill.Wanted.Skip(from).Take(count).Select(w => w.Livery).ToList(), true);

#if DEVTOOLS
        /// <summary>The probe's fault: the next cut placed throws, to see the service go on past it.</summary>
        internal static bool FailNextCut;

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
#if DEVTOOLS
            if (FailNextCut)
            {
                FailNextCut = false;
                throw new InvalidOperationException("the probe's fault: a cut that throws");
            }
#endif
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
            // What found no room waits off the map, and the service comes again for it (Contracts.Core
            // ServiceRetry). An order the game can't make cars for is not waited for.
            var left = ServiceRetry.Left(run.Orders.Select(r => (r.Order, r.Cars.Count))).Where(o => Placeable(o, out _, out _)).ToList();
            var waiting = ServiceRetry.Cars(left);
            State.Waiting = left.Select(WaitingOrder.Of).ToList();
            State.RetryAt = waiting > 0 && Clock is double ended ? ServiceRetry.NextAt(ended) : (double?)null;
            var again = waiting > 0 && State.RetryAt is double at && Clock is double clock ? $"; {waiting} wait for a free interchange track, the service comes again {ServiceRetry.At(at, clock)}" : "";
            run.Summary = run.Retry
                ? $"service day {run.Day}, again for the cars it had no room for: {run.Taken} car(s) taken off the map; " +
                  $"{run.Placed} car(s) placed ({short_} short), {run.Made} job(s){again}"
                : $"service day {run.Day}: {run.Contracts} contract(s) at work; {run.Taken} car(s) taken off the map, {run.Gone} gone, " +
                  (run.Asleep > 0 ? $"{run.Asleep} suspended by Persistent Jobs (kept), " : "") + $"{run.Idled} back to idle, " +
                  (run.Recalled > 0 ? $"{run.Recalled} moved off an interchange back to idle, " : "") + $"{run.Withdrawn} withdrawn; " +
                  $"{run.Bills} bill(s) ({run.SetOut} set out for pickup), {run.Placed} car(s) placed ({short_} short), {run.Made} job(s){again}";
            if (run.Failed > 0)
                run.Summary += $"; {run.Failed} part(s) failed and were left for the next service (see the errors above)";
            Main.Log(Invariant($"contracts: {run.Summary} (in {run.Work.Run} steps after the plan, {run.WorkMs:F0} ms of work so far, the longest step {run.LongestStepMs:F0} ms)"));
            run.Doing = "the summary";
            if (!run.Retry)
                Notes.Add(run.Summary);
            LastServiceDay = run.Day;
            LastService = run;
            LogJobless("after the service");
        }
    }
}
