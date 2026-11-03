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
    ///   cars already on the map: transport, shunting load and unload, and empty haul.
    /// </summary>
    internal static class PersistentJobs
    {
        private static object flags;
        private static MethodInfo hasSpawned, setSpawned;
        private static MethodInfo transport, shuntingLoad, shuntingUnload, emptyHaul;
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
            shuntingLoad = Method("PersistentJobsMod.JobGenerators.ShuntingLoadJobGenerator", "GenerateJobChainController");
            shuntingUnload = Method("PersistentJobsMod.JobGenerators.ShuntingUnloadJobGenerator", "TryGenerateJobChainController");
            emptyHaul = Method("PersistentJobsMod.JobGenerators.EmptyHaulJobGenerator", "GenerateEmptyHaulJobWithExistingCarsOrNull");
            foreach (var (m, name) in new[] { (hasSpawned, "spawned flag get"), (setSpawned, "spawned flag set"), (transport, "transport generator"),
                         (shuntingLoad, "shunting load generator"), (shuntingUnload, "shunting unload generator"), (emptyHaul, "empty haul generator") })
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

        /// <summary>A shunting load chain: cars on their tracks to a warehouse to load, for a destination station.</summary>
        public static JobChainController ShuntingLoad(StationController station, List<CarsPerTrack> carsPerTrack, WarehouseMachine machine,
            StationController destination, List<TrainCar> cars, List<DV.ThingTypes.CargoType> cargoPerCar, bool forceCargo) =>
            Call(shuntingLoad, new object[] { station, carsPerTrack, machine, destination, cars, cargoPerCar, forceCargo }, "shunting load job");

        /// <summary>A shunting unload chain for loaded cars standing on a track at their station.</summary>
        public static JobChainController ShuntingUnload(StationController station, Track startTrack, List<TrainCar> cars, Random random) =>
            Call(shuntingUnload, new object[] { station, startTrack, station, cars, random, false }, "shunting unload job");

        /// <summary>An empty haul of cars on a track to another station (its storage, or the given track).</summary>
        public static JobChainController EmptyHaul(StationController from, StationController to, Track startTrack, IReadOnlyList<TrainCar> cars,
            Random random, Track toTrack) =>
            Call(emptyHaul, new object[] { from, to, startTrack, cars, random, toTrack }, "empty haul job");
    }
}
