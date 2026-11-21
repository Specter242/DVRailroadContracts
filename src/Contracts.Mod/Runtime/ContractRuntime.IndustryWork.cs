using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Core.Sim;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using static System.FormattableString;

namespace Contracts.Mod.Runtime
{
    internal sealed partial class ContractRuntime
    {
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

        private readonly struct TrackKey : IEquatable<TrackKey>
        {
            public readonly string YardId;
            public readonly string Part;

            public TrackKey(string yardId, string part)
            {
                YardId = yardId;
                Part = part;
            }

            public bool Equals(TrackKey other) =>
                string.Equals(YardId, other.YardId, StringComparison.Ordinal)
                && string.Equals(Part, other.Part, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is TrackKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var h1 = YardId == null ? 0 : StringComparer.Ordinal.GetHashCode(YardId);
                    var h2 = Part == null ? 0 : StringComparer.Ordinal.GetHashCode(Part);
                    return (h1 * 397) ^ h2;
                }
            }
        }

        private static RailTrackRegistryBase stationTrackRegistry;
        private static Dictionary<Track, RailTrack> stationTrackSource;
        private static int stationTrackCount = -1;
        private static Dictionary<TrackKey, Track> stationTracks;

        private static void ClearStationTrackCache()
        {
            stationTrackRegistry = null;
            stationTrackSource = null;
            stationTrackCount = -1;
            stationTracks = null;
        }

        private static Dictionary<TrackKey, Track> BuildStationTrackIndex(Dictionary<Track, RailTrack> map)
        {
            var index = new Dictionary<TrackKey, Track>(map.Count);
            foreach (var t in map.Keys)
            {
                if (t?.ID == null)
                    continue;
                var key = new TrackKey(t.ID.yardId, t.ID.TrackPartOnly);
                if (!index.ContainsKey(key))
                    index.Add(key, t);
            }
            return index;
        }

        /// <summary>A station's track by its name within the station ("B7S"), or null.</summary>
        private static Track StationTrack(string yardId, string part)
        {
            var registry = RailTrackRegistry.Instance;
            if (registry == null)
            {
                if (stationTracks != null)
                    ClearStationTrackCache();
                return null;
            }
            var map = RailTrackRegistry.LogicToRailTrack;
            if (map == null)
            {
                if (stationTracks != null)
                    ClearStationTrackCache();
                return null;
            }
            var rebuilt = false;
            if (stationTracks == null
                || !ReferenceEquals(stationTrackRegistry, registry)
                || !ReferenceEquals(stationTrackSource, map)
                || stationTrackCount != map.Count)
            {
                stationTracks = BuildStationTrackIndex(map);
                stationTrackRegistry = registry;
                stationTrackSource = map;
                stationTrackCount = map.Count;
                rebuilt = true;
            }
            var key = new TrackKey(yardId, part);
            if (stationTracks.TryGetValue(key, out var found))
            {
                if (rebuilt
                    || (map.ContainsKey(found)
                        && found.ID != null
                        && found.ID.yardId == yardId
                        && found.ID.TrackPartOnly == part))
                    return found;
                // Same-count in-place replacement or TrackID override in LogicToRailTrack: rebuild so stale tracks are never returned.
                stationTracks = BuildStationTrackIndex(map);
                return stationTracks.TryGetValue(key, out found) ? found : null;
            }
            if (!rebuilt)
            {
                // Verify a miss wasn't caused by a same-count in-place swap introducing a new key.
                foreach (var t in map.Keys)
                {
                    if (t?.ID != null && t.ID.yardId == yardId && t.ID.TrackPartOnly == part)
                    {
                        stationTracks = BuildStationTrackIndex(map);
                        return stationTracks.TryGetValue(key, out found) ? found : null;
                    }
                }
            }
            return null;
        }

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
            var (best, room) = BestSpot(spots);
            var fit = 0;
            var prefix = new List<Car>(cars.Count);
            while (fit < cars.Count)
            {
                prefix.Add(cars[fit].logicCar);
                if (!(spawner.GetTotalTrainCarsLength(prefix, true) <= room))
                {
                    prefix.RemoveAt(prefix.Count - 1);
                    break;
                }
                fit++;
            }
            for (var i = fit; i < cars.Count; i++)
                if (waitingSaid.Add(cars[i].logicCar.carGuid))
                    Main.Log(Invariant($"contracts: {cars[i].ID} waits at {here.stationInfo.YardID} for room on {best.ID.FullDisplayID} ({room:F0} m free)"));
            if (fit == 0)
                return false;
            var spotted = cars.GetRange(0, fit);
            var chain = StationMoves.Move(here, track, best, spotted, loading);
            if (chain == null)
            {
                GoIdle(loading ? "loaded or mixed cars to be loaded" : "mixed loaded and empty cars");
                return false;
            }
            foreach (var c in spotted)
                waitingSaid.Remove(c.logicCar.carGuid);
            chain.FinalizeSetupAndGenerateFirstJob(false);
            Main.Log(Invariant($"contracts: {spotted.Count} car(s) ({string.Join(", ", spotted.Select(c => c.ID))}) to be spotted on {best.ID.FullDisplayID} at {w.Next} ") +
                     Invariant($"({spawner.GetTotalTrainCarsLength(prefix, true):F0} m of {room:F0} m free)"));
            return true;
        }

        /// <summary>
        /// Picks the industry track with the most room left: its free length, less the cars of spot
        /// jobs not yet taken that head for it (Persistent Jobs reserves a job's track only when it
        /// is taken). Scans open spot waybills once across all candidate tracks.
        /// </summary>
        private (Track Best, double Room) BestSpot(List<Track> spots)
        {
            if (spots == null || spots.Count == 0)
                return (null, 0.0);
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var spawner = SingletonBehaviour<CarSpawner>.Instance;

            Dictionary<Track, List<Car>> headingByTrack = null;
            if (registry != null && jobs != null)
            {
                var candidates = spots.Count > 1 ? new HashSet<Track>(spots) : null;
                var only = spots.Count == 1 ? spots[0] : null;
                Dictionary<Job, Track> destinations = null;
                foreach (var w in State.Waybills)
                {
                    if (w.Stage != Stage.Loading && w.Stage != Stage.Unloading)
                        continue;
                    var car = registry.GetTrainCarByCarGuid(w.Guid);
                    if (car == null || car.logicCar == null)
                        continue;
                    var job = jobs.GetJobOfCar(car.logicCar);
                    if (job == null || job.State != JobState.Available)
                        continue;
                    if (destinations == null)
                        destinations = new Dictionary<Job, Track>();
                    if (!destinations.TryGetValue(job, out var dest))
                        destinations[job] = dest = JobDestination(job);
                    if (dest == null || car.logicCar.FrontBogieTrack == dest)
                        continue;
                    if (only != null ? dest != only : !candidates.Contains(dest))
                        continue;
                    if (headingByTrack == null)
                        headingByTrack = new Dictionary<Track, List<Car>>();
                    if (!headingByTrack.TryGetValue(dest, out var list))
                        headingByTrack[dest] = list = new List<Car>();
                    list.Add(car.logicCar);
                }
            }

            Track best = null;
            var bestRoom = double.NegativeInfinity;
            for (var i = 0; i < spots.Count; i++)
            {
                var track = spots[i];
                var pending = headingByTrack != null && headingByTrack.TryGetValue(track, out var heading) && heading.Count > 0
                    ? spawner.GetTotalCarsLength(heading, true)
                    : 0f;
                var room = organizer.GetFreeSpaceOnTrack(track) - pending;
                if (best == null || room.CompareTo(bestRoom) > 0)
                {
                    best = track;
                    bestRoom = room;
                }
            }
            return (best, bestRoom);
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
                var queue = new List<WorkingCar>();
                var byCar = new Dictionary<WorkingCar, Waybill>();
                foreach (var w in line)
                {
                    var car = new WorkingCar(w.Guid, w.Spotted ?? now, w.Ready);
                    queue.Add(car);
                    byCar[car] = w;
                }
                var finished = IndustryWork.Advance(queue, daysPerCar, now);
                for (var i = 0; i < queue.Count; i++)
                {
                    var car = queue[i];
                    var w = byCar[car];
                    w.Spotted = car.Spotted;
                    w.Ready = car.Ready;
                }
                for (var i = 0; i < finished.Count; i++)
                {
                    var f = finished[i];
                    var w = byCar[f];
                    var train = registry?.GetTrainCarByCarGuid(w.Guid);
                    if (train != null && OnIndustryTrack(train, w))
                        done.Add((train, w, f.Ready.Value));
                }
            }
            if (done.Count == 0)
                return 0;
            var levels = State.Contracts.Where(c => c != null && !string.IsNullOrEmpty(c.Id) && c.Level >= 1 && Economy.Industries.ContainsKey(c.Id))
                .GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Level, StringComparer.Ordinal);
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
                var seed = unchecked((State.Seed + (ulong)Math.Max(0.0, Math.Floor(ready * 1440))) ^ SplitMix64.Hash(w.Guid));
                var a = IndustryPlanner.RouteFreedEmpty(Economy.Data, levels, w.Guid, car.carLivery?.parentType?.id, w.At,
                    new SplitMix64(seed), carrying);
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
    }
}
