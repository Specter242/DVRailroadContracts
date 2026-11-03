using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Contracts.Core.Industries;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using UnityEngine;
using Random = System.Random;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// Passenger service under contract (design D13, D23; owner, 2026-11-03: "yes we need
    /// passenger service"), on Passenger Jobs 5.3, reached by reflection as Persistent Jobs does:
    ///
    /// - **A city's contract** (CW, MF, FF, HB, GF: the cities where Passenger Jobs starts trains)
    ///   makes its level's departures due at each service (Contracts.Core PassengerService).
    /// - **A train** runs to the city's daily timetable (PassengerRuns), with the player's own
    ///   coaches (bought: D14): a Passenger Jobs job for them, made with its own builder
    ///   (<c>GenerateJobFromConsist</c>), boarding on one of the city's platforms, express and
    ///   local in turn. Express needs the Passengers 2 license; without it every train is local.
    /// - **After a train's job** its coaches stay where it ended: neither Passenger Jobs nor
    ///   Persistent Jobs gives them their next job; the next train due anywhere takes them.
    /// - Taking a city's contract needs the Fragile and Passengers 1 licenses (D19).
    /// </summary>
    internal static class Passengers
    {
        public const JobType Express = (JobType)101, Local = (JobType)102;
        public static readonly string[] Licenses = { "Fragile", "Passengers1" };
        public const string ExpressLicense = "Passengers2";

        private static Type generatorType, consistType, routeTrackType;
        private static MethodInfo tryGet, fromConsist;
        private static FieldInfo stationData;
        private static bool looked;

        /// <summary>Whether Passenger Jobs is installed with everything this uses.</summary>
        public static bool Available
        {
            get
            {
                Look();
                return tryGet != null && fromConsist != null && stationData != null && consistType != null && routeTrackType != null;
            }
        }

        private static void Look()
        {
            if (looked)
                return;
            looked = true;
            generatorType = AccessTools.TypeByName("PassengerJobs.Generation.PassengerJobGenerator");
            consistType = AccessTools.TypeByName("PassengerJobs.Generation.PassConsistInfo");
            routeTrackType = AccessTools.TypeByName("PassengerJobs.Generation.RouteTrack");
            if (generatorType == null)
                return;
            tryGet = AccessTools.Method(generatorType, "TryGetInstance");
            fromConsist = AccessTools.Method(generatorType, "GenerateJobFromConsist");
            stationData = AccessTools.Field(generatorType, "_stationData");
            if (!Available)
                Main.Error("Passenger Jobs: its generator is not as expected; passenger contracts stay off");
        }

        /// <summary>
        /// A finished train's coaches are the contracts' to work, not Passenger Jobs' (its own next
        /// job is blocked) nor Persistent Jobs' (it replaces the train chain's completion with its
        /// own, which reassigns the coaches as its own cars): first, the departure closes and the
        /// player's coaches leave the chain (ContractRuntime.ReleaseCoaches).
        /// </summary>
        public static void Install(Harmony harmony)
        {
            Look();
            var next = generatorType == null ? null : AccessTools.Method(generatorType, "StartGenerationFromConsistAsync");
            if (next != null)
                harmony.Patch(next, prefix: new HarmonyMethod(AccessTools.Method(typeof(Passengers), nameof(NoOwnNextJob))));
            var chainType = AccessTools.TypeByName("PassengerJobs.Generation.PassengerChainController");
            var done = chainType == null ? null : AccessTools.Method(chainType, "OnLastJobInChainCompleted", new[] { typeof(Job) });
            if (done != null)
                harmony.Patch(done, prefix: new HarmonyMethod(AccessTools.Method(typeof(Passengers), nameof(TrainDone))) { priority = Priority.First });
            if (next == null || done == null)
                Main.Error("Passenger Jobs: a finished train can't be taken over; passenger contracts may misbehave");
            else
                Main.Log("Passenger Jobs: a finished train's coaches are left to the passenger contracts");
        }

        private static bool NoOwnNextJob() => !ContractRuntime.Active;

        private static void TrainDone(JobChainController __instance, Job lastJobInChain)
        {
            try
            {
                if (ContractRuntime.Active && __instance?.carsForJobChain != null && lastJobInChain != null)
                    ContractRuntime.Instance.ReleaseCoaches(__instance, lastJobInChain);
            }
            catch (Exception e)
            {
                Main.Error($"passengers: taking over the coaches of {lastJobInChain?.ID} failed: {e}");
            }
        }

        // ------------------------------------------------------------ cities

        private static object DataOf(string city)
        {
            if (!Available || city == null)
                return null;
            var args = new object[] { city, null };
            return (bool)tryGet.Invoke(null, args) && args[1] != null ? stationData.GetValue(args[1]) : null;
        }

        private static List<Track> Tracks(object data, string field) =>
            data == null ? new List<Track>() : (Traverse.Create(data).Field(field).GetValue<List<Track>>() ?? new List<Track>()).Where(t => t != null).ToList();

        /// <summary>The tracks a city's trains start on (Passenger Jobs' terminus tracks).</summary>
        public static List<Track> Terminus(string city) => Tracks(DataOf(city), "TerminusTracks");

        /// <summary>A city's platform and storage tracks, where its coaches may stand.</summary>
        public static List<Track> AllTracks(string city)
        {
            var data = DataOf(city);
            if (data == null)
                return new List<Track>();
            var platforms = (Traverse.Create(data).Field("Platforms").GetValue() as System.Collections.IEnumerable)?.Cast<object>()
                .Select(p => Traverse.Create(p).Field("Track").GetValue<Track>()).Where(t => t != null) ?? Enumerable.Empty<Track>();
            return platforms.Concat(Tracks(data, "StorageTracks")).Concat(Tracks(data, "TerminusTracks")).Distinct().ToList();
        }

        /// <summary>The cities where a passenger train can start: Passenger Jobs' stations with a terminus track.</summary>
        public static List<string> Cities() =>
            !Available ? new List<string>()
            : (StationController.allStations ?? new List<StationController>()).Where(s => s?.stationInfo != null)
                .Select(s => s.stationInfo.YardID).Where(y => Terminus(y).Count > 0).Distinct().OrderBy(y => y, StringComparer.Ordinal).ToList();

        // ------------------------------------------------------------ coaches

        /// <summary>A coach of the player's own: bought (an owned car) and of a passenger type.</summary>
        public static bool IsCoach(TrainCar car) => car != null && car.uniqueCar && LocoShop.IsCoach(car.carLivery);

        /// <summary>
        /// The player's coaches that are free for a train: one list per train of them (its coaches
        /// in order; its locomotive, if any, is not part of the job), wherever it stands, stopped,
        /// with no coach on a job.
        /// </summary>
        public static List<List<TrainCar>> IdleConsists()
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var result = new List<List<TrainCar>>();
            if (jobs == null || registry == null)
                return result;
            var seen = new HashSet<Trainset>();
            foreach (var car in registry.logicCarToTrainCar.Values)
            {
                if (!IsCoach(car) || car.trainset == null || !seen.Add(car.trainset))
                    continue;
                var coaches = car.trainset.cars.Where(IsCoach).ToList();
                if (coaches.Any(c => c.logicCar == null || c.logicCar.FrontBogieTrack == null || jobs.GetJobOfCar(c.logicCar) != null)
                    || car.trainset.cars.Any(c => c.GetAbsSpeed() > 0.2f))
                    continue;
                result.Add(coaches);
            }
            return result;
        }

        /// <summary>The track a train of coaches stands on (its first coach's).</summary>
        public static Track TrackOf(List<TrainCar> coaches) => coaches?.FirstOrDefault()?.logicCar?.FrontBogieTrack;

        /// <summary>A platform of the city a train of coaches can board on: empty, and long enough for them.</summary>
        public static Track FreePlatform(string city, List<TrainCar> coaches)
        {
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            if (organizer == null || spawner == null)
                return null;
            var length = spawner.GetTotalTrainCarsLength(coaches.Select(c => c.logicCar).ToList(), true);
            return Terminus(city).FirstOrDefault(t => t.GetCarsFullyOnTrack().Count == 0 && t.GetCarsPartiallyOnTrack().Count == 0
                                                     && organizer.GetFreeSpaceOnTrack(t) >= length + 10.0);
        }

        // ------------------------------------------------------------ jobs

        /// <summary>A Passenger Jobs job for a consist standing on a city's platform track, or null if it found no route.</summary>
        public static JobChainController Departure(string city, Track track, List<TrainCar> coaches, JobType type)
        {
            var data = DataOf(city);
            if (data == null)
                return null;
            var args = new object[] { city, null };
            if (!(bool)tryGet.Invoke(null, args) || args[1] == null)
                return null;
            try
            {
                var start = Activator.CreateInstance(routeTrackType, data, track);
                var consist = Activator.CreateInstance(consistType, start, coaches.Select(c => c.logicCar).ToList());
                return fromConsist.Invoke(args[1], new[] { (object)type, consist }) as JobChainController;
            }
            catch (TargetInvocationException e)
            {
                Main.Error($"passengers: a {(type == Express ? "express" : "local")} from {track.ID.FullDisplayID} failed: {e.InnerException?.Message ?? e.Message}");
                return null;
            }
        }

        /// <summary>The licenses a passenger contract needs that the player lacks (ids).</summary>
        public static List<string> Missing(HashSet<string> held) => Licenses.Where(l => !held.Contains(l)).ToList();
    }
}
