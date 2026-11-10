using System;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The career the contracts run in (owner, 2026-11-01: "industries were already populated ...
    /// Trains will need to be purchasable with no free spawns. A single DE2 available at the
    /// start. Manual servicing available at the start."):
    ///
    /// - Stations never spawn cars or jobs: every station is flagged as having spawned its cars
    ///   in Persistent Jobs, which then skips its one round of car spawning, and Passenger Jobs'
    ///   generation, which Persistent Jobs gates on the same flag (design, rule 3). The stock
    ///   generator is blocked too.
    /// - Stations never spawn locomotives (StationLocoSpawner, and the Multiplayer mod's own spawning).
    /// - A new career starts with one DE2, owned (as a garage's vehicle is), on the locomotive
    ///   track of the station the player starts at, and the Manual Service licence.
    /// - Contract cars are kept from the unused-car deleter and from Persistent Jobs' reassigning
    ///   of jobless cars: the mod gives each its next job itself (design, "Protection").
    /// </summary>
    internal static class CareerRules
    {
        /// <summary>Flags every station as having spawned its cars, so none ever do.</summary>
        public static int FlagAllStations()
        {
            var n = 0;
            foreach (var station in StationController.allStations ?? new List<StationController>())
            {
                if (station == null || station.logicStation == null)
                    continue;
                if (!PersistentJobs.HasSpawned(station.logicStation.ID))
                {
                    PersistentJobs.SetSpawned(station.logicStation.ID);
                    n++;
                }
            }
            return n;
        }

        /// <summary>Gives a new career its starting DE2 and Manual Service licence; true once done.</summary>
        public static bool GiveStarter(List<string> notes)
        {
            var licences = SingletonBehaviour<LicenseManager>.Instance;
            var manual = GeneralLicenseType.ManualService.ToV2();
            if (licences != null && manual != null && !licences.IsGeneralLicenseAcquired(manual))
            {
                licences.AcquireGeneralLicense(manual);
                notes.Add("starter: Manual Service licence acquired");
            }
            var player = PlayerManager.PlayerTransform;
            if (player == null)
                return false;
            var station = StationController.allStations?.Where(s => s != null)
                .OrderBy(s => (s.transform.position - player.position).sqrMagnitude).FirstOrDefault();
            var de2 = TrainCarType.LocoShunter.ToV2();
            if (station == null || de2 == null)
                return false;
            var spawned = LocoShop.Deliver(new[] { de2 }, station, out var where);
            // It came free: sold back, it fetches nothing (VehicleSales).
            if (spawned != null)
                VehicleSales.Record(spawned, 0.0, false);
            notes.Add(spawned == null
                ? $"starter: no free locomotive track at {station.stationInfo.YardID} for the DE2"
                : $"starter: DE2 {spawned[0].ID} delivered at {station.stationInfo.YardID}, {where}");
            return spawned != null;
        }

        /// <summary>
        /// A station's generator, run as the player arrives, spawns nothing. First the station is
        /// flagged as spawned (a prefix that can't skip anything, so every other one still runs);
        /// then Persistent Jobs' own prefix, finding the flag, brings back the cars it suspended
        /// there (its "Suspend far away cars") and skips the generator. Last, should it not have,
        /// the generator is skipped here. A prefix that returns false skips every later one that
        /// can skip the original: this block used to run first and return false, so Persistent
        /// Jobs never brought a station's cars back (a Nexus bug report, 2026-11-04: cars left on a
        /// station's tracks disappeared once the player drove away).
        /// </summary>
        [HarmonyPatch(typeof(StationProceduralJobsController), nameof(StationProceduralJobsController.TryToGenerateJobs))]
        private static class FlagStationSpawned
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(StationProceduralJobsController __instance)
            {
                try
                {
                    var id = __instance.stationController?.logicStation?.ID;
                    if (id != null)
                        PersistentJobs.SetSpawned(id);
                }
                catch (Exception e)
                {
                    Main.Error($"flagging a station as spawned failed: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobsController), nameof(StationProceduralJobsController.TryToGenerateJobs))]
        private static class NoStationSpawning
        {
            [HarmonyPriority(Priority.Last)]
            private static bool Prefix() => false;
        }

        /// <summary>
        /// Passenger Jobs' generator, if it is installed: its station is flagged before Persistent
        /// Jobs' own gate looks at the flag (patched by hand: the type is another mod's).
        /// </summary>
        public static void GuardPassengerJobs(Harmony harmony)
        {
            var generator = AccessTools.TypeByName("PassengerJobs.Generation.PassengerJobGenerator");
            var start = generator == null ? null : AccessTools.Method(generator, "StartGenerationAsync");
            if (start == null)
                return;
            harmony.Patch(start, prefix: new HarmonyMethod(AccessTools.Method(typeof(CareerRules), nameof(FlagPassengerStation))) { priority = Priority.First });
            Main.Log("Passenger Jobs: its generation is gated on the stations' spawned flags");
        }

        private static void FlagPassengerStation(object __instance)
        {
            try
            {
                var station = Traverse.Create(__instance).Field("Controller").GetValue<StationController>();
                var id = station?.logicStation?.ID;
                if (id != null)
                    PersistentJobs.SetSpawned(id);
            }
            catch (Exception e)
            {
                Main.Error($"flagging a passenger station failed: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(StationLocoSpawner), "Update")]
        private static class NoFreeLocomotives
        {
            private static bool Prefix() => false;
        }

        /// <summary>Whether the Multiplayer mod's own station locomotives are blocked (for the probe).</summary>
        internal static bool MultiplayerLocomotivesBlocked { get; private set; }

        /// <summary>
        /// The Multiplayer mod's own station locomotives, if it is installed: it blocks the
        /// spawner's Update as this mod does, and on the host spawns a station's locomotives
        /// itself when any player comes near (its StationLocoSpawner.Start patch). Those are
        /// blocked too, so a career keeps its one DE2 in Multiplayer (Blaze221, Nexus, 2026-11-08:
        /// "locomotives spawned at the MF and SM"). Patched by hand: the type is another mod's.
        /// </summary>
        public static void GuardMultiplayerLocomotives(Harmony harmony)
        {
            var patch = AccessTools.TypeByName("Multiplayer.Patches.World.StationLocoSpawner_Start_Patch");
            if (patch == null)
                return;
            var spawn = AccessTools.Method(patch, "SpawnLocomotives");
            if (spawn == null)
            {
                Main.Warn("Multiplayer: its station locomotive spawning is not where it was; stations may spawn locomotives in Multiplayer");
                return;
            }
            harmony.Patch(spawn, prefix: new HarmonyMethod(AccessTools.Method(typeof(CareerRules), nameof(NoMultiplayerLocomotives))));
            MultiplayerLocomotivesBlocked = true;
            Main.Log("Multiplayer: its station locomotives are blocked too");
        }

        private static bool NoMultiplayerLocomotives() => false;

        // ------------------------------------------------------------ protection

        /// <summary>Contract cars: the live waybills, or, while a save loads, those it holds.</summary>
        internal static Func<string, bool> IsContractCar = guid => false;

        /// <summary>
        /// A contract car, or one of the player's coaches (a passenger contract's: Persistent Jobs
        /// would take a jobless coach marked unused as its own, and give it its own work).
        /// </summary>
        /// <summary>A car the game and other mods must leave alone: a contract car, or one of the player's coaches.</summary>
        internal static bool Protected(Car car)
        {
            if (car == null)
                return false;
            if (IsContractCar(car.carGuid))
                return true;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            return ContractRuntime.Active && registry != null && registry.logicCarToTrainCar.TryGetValue(car, out var t) && Passengers.IsCoach(t);
        }

        [HarmonyPatch(typeof(UnusedTrainCarDeleter), nameof(UnusedTrainCarDeleter.MarkForDelete), typeof(Car))]
        private static class KeepOneOffDeleteList
        {
            private static bool Prefix(Car unusedCar) => !Protected(unusedCar);
        }

        [HarmonyPatch(typeof(UnusedTrainCarDeleter), nameof(UnusedTrainCarDeleter.MarkForDelete), typeof(List<Car>))]
        private static class KeepOffDeleteList
        {
            private static void Prefix(ref List<Car> unusedCars)
            {
                if (unusedCars != null && unusedCars.Any(Protected))
                    unusedCars = unusedCars.Where(c => !Protected(c)).ToList();
            }
        }

        [HarmonyPatch(typeof(UnusedTrainCarDeleter), "AreDeleteConditionsFulfilled")]
        private static class DeleteGuard
        {
            private static bool Prefix(TrainCar trainCar, ref bool __result)
            {
                if (trainCar == null || !Protected(trainCar.logicCar))
                    return true;
                __result = false;
                return false;
            }
        }

        /// <summary>
        /// After a save loads, the game marks every car outside a job for deletion. Contract cars
        /// are read straight from the save (the runtime starts later) and unmarked. This runs in
        /// the game's job-load block, whose failure deletes every car: it must never throw.
        /// </summary>
        [HarmonyPatch(typeof(JobSaveManager), nameof(JobSaveManager.MarkAllNonJobCarsAsUnused))]
        private static class UnmarkAfterLoad
        {
            private static void Postfix()
            {
                try
                {
                    var saved = ContractState.GuidsIn(ContractState.CurrentSave);
                    if (saved.Count == 0)
                        return;
                    var previous = IsContractCar;
                    IsContractCar = g => g != null && (saved.Contains(g) || previous(g));
                    var deleter = SingletonBehaviour<UnusedTrainCarDeleter>.Instance;
                    var marked = deleter.GetUnusedTrainCarsList().Where(c => c != null && c.logicCar != null && saved.Contains(c.logicCar.carGuid)).ToList();
                    if (marked.Count > 0)
                        deleter.UnmarkFromDeleteList(marked);
                    Main.Log($"after load: {marked.Count} of {saved.Count} contract cars unmarked from deletion");
                }
                catch (Exception e)
                {
                    Main.Error($"unmarking contract cars after load failed (the load goes on): {e}");
                }
            }
        }
    }
}
