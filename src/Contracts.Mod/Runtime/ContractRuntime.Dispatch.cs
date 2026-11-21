using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Contracts.Core.Industries;
using Contracts.Core.Sim;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using Random = System.Random;

namespace Contracts.Mod.Runtime
{
    internal sealed partial class ContractRuntime
    {
        // ------------------------------------------------------------ jobs

        /// <summary>A cut of cars, or cars already on the map, that share the next leg's job.</summary>
        internal sealed class Chunk
        {
            public List<TrainCar> Cars;
            public List<Waybill> Bills;
            public Track Track;
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

        internal static readonly FieldInfo StationSpawnedJobOverviewsField = AccessTools.Field(typeof(StationController), "spawnedJobOverviews");
        internal static readonly FieldInfo StationProcessedNewJobsField = AccessTools.Field(typeof(StationController), "processedNewJobs");

        /// <summary>Takes a job not yet taken off its station: its booklet, then the job itself (it expires; its chain goes with it).</summary>
        internal static void Withdraw(Job job)
        {
            var station = StationController.allStations?.FirstOrDefault(s => s != null && s.logicStation != null && s.logicStation.availableJobs.Contains(job));
            if (station != null)
            {
                var spawned = StationSpawnedJobOverviewsField?.GetValue(station) as List<JobOverview>;
                if (spawned != null)
                {
                    for (var i = spawned.Count - 1; i >= 0; i--)
                    {
                        var o = spawned[i];
                        if (o != null && o.job == job)
                        {
                            spawned.RemoveAt(i);
                            o.DestroyJobOverview();
                        }
                    }
                }
                (StationProcessedNewJobsField?.GetValue(station) as HashSet<Job>)?.Remove(job);
            }
            job.ExpireJob();
        }

        private static int MaxCarsPerJob() => Math.Max(3, SingletonBehaviour<LicenseManager>.Instance?.GetMaxNumberOfCarsPerJobWithAcquiredJobLicenses() ?? 5);

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
            var maxCars = MaxCarsPerJob();
            var groups = cars.GroupBy(c =>
            {
                var track = standingOn ?? c.Car.logicCar.FrontBogieTrack;
                return (Track: track, Key: JobKey(c.W, track), Loaded: c.Car.logicCar.CurrentCargoTypeInCar != CargoType.None);
            });
            foreach (var g in groups)
            {
                var list = g.ToList();
                for (var i = 0; i < list.Count; i += maxCars)
                {
                    var count = Math.Min(maxCars, list.Count - i);
                    var chunkCars = new List<TrainCar>(count);
                    var chunkBills = new List<Waybill>(count);
                    for (var j = 0; j < count; j++)
                    {
                        var item = list[i + j];
                        chunkCars.Add(item.Car);
                        chunkBills.Add(item.W);
                    }
                    chunks.Add(new Chunk { Cars = chunkCars, Bills = chunkBills, Track = g.Key.Track });
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
            if (jobRng == null)
                jobRng = new SplitMix64(unchecked((State?.Seed ?? 0UL) ^ ((ulong)(uint)(State?.Day ?? 0) * 0x9E3779B97F4A7C15UL) ^ 0x4A4F42524E475345UL));
            var random = new Random((int)(jobRng.NextULong() & int.MaxValue));
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
    }
}
