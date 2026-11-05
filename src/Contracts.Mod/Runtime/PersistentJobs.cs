using System;
using System.Collections.Generic;
using System.Reflection;
using DV.Logic.Job;
using HarmonyLib;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// What the runtime uses of Persistent Jobs, reached by reflection: it is built for a newer
    /// .NET Framework than this mod, and binding by name keeps its internals out of the build
    /// (design, "Hooks we use"). Every member is looked up once; a missing one is reported, and
    /// the feature that needs it stays off.
    /// - Its per-station "spawned cars" flags (public), which stop its one round of car spawning
    ///   and Passenger Jobs' generation.
    /// - Its job generators (internal classes, public methods), which build stock job chains for
    ///   cars already on the map: transport and empty haul. (Spotting at an industry is the
    ///   mod's own move, StationMoves; the industries load and unload the cars themselves.)
    /// </summary>
    internal static class PersistentJobs
    {
        private static object flags;
        private static MethodInfo hasSpawned, setSpawned;
        private static MethodInfo transport, emptyHaul;
        private static bool looked;
        public static readonly List<string> Missing = new List<string>();

        private static void Look()
        {
            if (looked)
                return;
            looked = true;
            var persistence = AccessTools.TypeByName("PersistentJobsMod.Persistence.StationIdCarSpawningPersistence");
            flags = persistence == null ? null : AccessTools.Field(persistence, "Instance")?.GetValue(null);
            hasSpawned = persistence == null ? null : AccessTools.Method(persistence, "GetHasStationSpawnedCarsFlag", new[] { typeof(string) });
            setSpawned = persistence == null ? null : AccessTools.Method(persistence, "SetHasStationSpawnedCarsFlag", new[] { typeof(string), typeof(bool) });
            transport = Method("PersistentJobsMod.JobGenerators.TransportJobGenerator", "TryGenerateJobChainController");
            emptyHaul = Method("PersistentJobsMod.JobGenerators.EmptyHaulJobGenerator", "GenerateEmptyHaulJobWithExistingCarsOrNull");
            foreach (var (m, name) in new[] { (hasSpawned, "spawned flag get"), (setSpawned, "spawned flag set"), (transport, "transport generator"),
                         (emptyHaul, "empty haul generator") })
                if (m == null)
                    Missing.Add(name);
            if (flags == null)
                Missing.Add("spawned flags instance");
            if (Missing.Count > 0)
                Main.Error("Persistent Jobs: not found: " + string.Join(", ", Missing));
        }

        private static MethodInfo Method(string type, string name)
        {
            var t = AccessTools.TypeByName(type);
            return t == null ? null : AccessTools.Method(t, name);
        }

        public static bool HasSpawned(string stationId)
        {
            Look();
            return flags != null && hasSpawned != null && (bool)hasSpawned.Invoke(flags, new object[] { stationId });
        }

        public static void SetSpawned(string stationId)
        {
            Look();
            if (flags != null && setSpawned != null)
                setSpawned.Invoke(flags, new object[] { stationId, true });
        }

        private static JobChainController Call(MethodInfo m, object[] args, string what)
        {
            Look();
            if (m == null)
                return null;
            try
            {
                return m.Invoke(null, args) as JobChainController;
            }
            catch (TargetInvocationException e)
            {
                Main.Error($"{what}: {e.InnerException?.Message ?? e.Message}");
                return null;
            }
        }

        /// <summary>A transport chain for cars on a track to a station (its transfer-in tracks, or the given track).</summary>
        public static JobChainController Transport(StationController from, Track startTrack, StationController to, IReadOnlyList<TrainCar> cars,
            List<DV.ThingTypes.CargoType> cargoPerCar, Random random, bool forceCargo, Track toTrack) =>
            Call(transport, new object[] { from, startTrack, to, cars, cargoPerCar, random, forceCargo, toTrack }, "transport job");

        // ---------------------------------------------------------- far cars (FarCarOpt)

        private static Type farCars;
        private static FieldInfo suspended, suspending;
        private static bool farLooked;

        private static void LookFar()
        {
            if (farLooked)
                return;
            farLooked = true;
            farCars = AccessTools.TypeByName("PersistentJobsMod.Optimization.FarCarOpt");
            suspended = farCars == null ? null : AccessTools.Field(farCars, "SuspendedCarObjects");
            suspending = farCars == null ? null : AccessTools.Field(farCars, "CurrentTrainCarToSuspend");
            if (farCars != null && (suspended == null || suspending == null))
                Main.Warn("Persistent Jobs: its far-car suspension's state not found; a car it suspends may be taken for deleted");
        }

        /// <summary>Persistent Jobs' far-car suspension (FarCarOpt), if it is there.</summary>
        internal static Type FarCars
        {
            get
            {
                LookFar();
                return farCars;
            }
        }

        /// <summary>Whether Persistent Jobs holds a car suspended ("Suspend far away cars"): out of the world, to come back.</summary>
        public static bool IsSuspended(string guid)
        {
            LookFar();
            return guid != null && suspended?.GetValue(null) is System.Collections.IDictionary held && held.Contains(guid);
        }

        /// <summary>Whether Persistent Jobs is suspending this car now (its deletion is a suspension, not a loss).</summary>
        public static bool Suspending(UnityEngine.Object car)
        {
            LookFar();
            return car != null && suspending?.GetValue(null) is UnityEngine.Object current && current == car;
        }

        /// <summary>An empty haul of cars on a track to another station (its storage, or the given track).</summary>
        public static JobChainController EmptyHaul(StationController from, StationController to, Track startTrack, IReadOnlyList<TrainCar> cars,
            Random random, Track toTrack) =>
            Call(emptyHaul, new object[] { from, to, startTrack, cars, random, toTrack }, "empty haul job");
    }
}
