using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Contracts.Core.Industries;
using DV.Logic.Job;
using DV.Utils;
using HarmonyLib;

namespace Contracts.Mod.Runtime
{
    internal sealed partial class ContractRuntime
    {
        // ------------------------------------------------------------ queries

        /// <summary>The contracts' cars still in work now, from their waybills (Contracts.Core WorkInHand).</summary>
        internal WorkInHand InHand() =>
            WorkInHand.From(State.Waybills.Select(w => new BilledCar { Id = w.Guid, Contract = w.Contract, Next = w.Next, Cargo = w.Cargo, Stage = StageOf(w.Stage) }),
                Economy?.Data);

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
        /// Whether a job is contract work: it moves cars with a waybill, or the player's own coaches
        /// (a passenger contract's departure, or a ferry for them).
        /// </summary>
        internal bool IsContractJob(Job job)
        {
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            return JobCars(job).Any(c => c != null && (State.Of(c.carGuid) != null
                || registry != null && registry.logicCarToTrainCar.TryGetValue(c, out var t) && Passengers.IsCoach(t)));
        }

        private static readonly FieldInfo JobsManagerJobToJobCarsField = AccessTools.Field(typeof(JobsManager), "jobToJobCars");

        /// <summary>A job's cars, as the jobs manager lists them, else from its tasks.</summary>
        internal static IEnumerable<Car> JobCars(Job job)
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var raw = jobs == null || job == null ? null : JobsManagerJobToJobCarsField?.GetValue(jobs);
            if (raw is Dictionary<Job, HashSet<Car>> dict)
            {
                if (dict.TryGetValue(job, out var set) && set != null)
                    return set;
            }
            else if (raw is System.Collections.IDictionary map && map.Contains(job) && map[job] is IEnumerable<Car> cars)
            {
                return cars;
            }
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
    }
}
