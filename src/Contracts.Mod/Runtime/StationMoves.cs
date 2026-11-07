using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// A move of cars between two tracks of one station (design, "Industry work"): spotting them
    /// on an industry's track, or pulling an empty off it to the station's storage. A stock job
    /// chain built as Persistent Jobs' transport and empty haul generators build theirs: a
    /// transport for loaded cars, an empty haul for empties, from the station to itself (the game
    /// already runs such hauls, as MF to its own interchange yard). It is shunting work: it asks
    /// for the shunting license, and pays half the stock wage of a shunting job on one track, a
    /// stock shunting job being a spot and a pull.
    ///
    /// And it reads as shunting (owner, 2026-11-05: the moves within the industry "are labeled
    /// as logistics and freight haul rather than shunting"). Its job ID has a stock shunting code,
    /// SL for a move that serves loading and SU for one that serves unloading, and its papers
    /// (MovePapers) say Shunting in the shunting color. The job itself stays a stock transport or
    /// empty haul, so saves, Persistent Jobs and Multiplayer see nothing new.
    /// </summary>
    internal static class StationMoves
    {
        /// <summary>
        /// The move's job chain: <paramref name="forLoading"/> for an empty spotted to be loaded,
        /// otherwise a load spotted to be unloaded or an emptied car pulled into storage. Null if
        /// the cars are mixed loaded and empty, or loaded for loading.
        /// </summary>
        public static JobChainController Move(StationController station, Track from, Track to, List<TrainCar> cars, bool forLoading)
        {
            var cargo = cars.Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();
            var loaded = cargo.All(c => c != CargoType.None);
            if (!loaded && cargo.Any(c => c != CargoType.None) || loaded && forLoading)
                return null;
            var logicCars = TrainCar.ExtractLogicCars(cars);
            var licenses = SingletonBehaviour<LicenseManager>.Instance;
            var wage = JobPaymentCalculator.CalculateJobPayment(JobType.ShuntingUnload, 500f, PaymentData(cars, loaded ? cargo : new List<CargoType>())) / 2f;
            var time = JobPaymentCalculator.CalculateShuntingBonusTimeLimit(1);
            var required = JobLicenseType_v2.ListToFlags(licenses.GetRequiredLicensesForJobType(JobType.ShuntingUnload))
                           | (loaded ? JobLicenseType_v2.ListToFlags(licenses.GetRequiredLicensesForCargoTypes(cargo)) : JobLicenses.Basic)
                           | (licenses.GetRequiredLicenseForNumberOfTransportedCars(cars.Count)?.v1 ?? JobLicenses.Basic);
            var type = loaded ? JobType.Transport : JobType.EmptyHaul;
            var id = station.logicStation.ID;
            var go = new GameObject($"ChainJob[{type}]: {id} - {id} (contracts: {to.ID.FullDisplayID})");
            go.transform.SetParent(station.transform);
            var chain = new JobChainController(go) { carsForJobChain = logicCars.ToList() };
            var chainData = new StationsChainData(station.stationInfo.YardID, station.stationInfo.YardID);
            StaticJobDefinition definition;
            if (loaded)
            {
                var transport = go.AddComponent<StaticTransportJobDefinition>();
                transport.PopulateBaseJobDefinition(station.logicStation, time, wage, chainData, required);
                transport.startingTrack = from;
                transport.destinationTrack = to;
                transport.carsToTransport = logicCars.ToList();
                transport.transportedCargoPerCar = cargo;
                transport.cargoAmountPerCar = cars.Select(c => c.logicCar.LoadedCargoAmount).ToList();
                transport.forceCorrectCargoStateOnCars = false;
                definition = transport;
            }
            else
            {
                var haul = go.AddComponent<StaticEmptyHaulJobDefinition>();
                haul.PopulateBaseJobDefinition(station.logicStation, time, wage, chainData, required);
                haul.startingTrack = from;
                haul.destinationTrack = to;
                haul.carsToTransport = logicCars.ToList();
                definition = haul;
            }
            // The ID is the next free one with the shunting code. The generator registers what it
            // hands out, and a job given its ID registers it again, so it is let go here; the
            // caller makes the job at once (FinalizeSetupAndGenerateFirstJob), before any other.
            var ids = SingletonBehaviour<IdGenerator>.Instance;
            var jobId = ids.GenerateJobID(forLoading ? JobType.ShuntingLoad : JobType.ShuntingUnload, chainData);
            ids.UnregisterJobId(jobId);
            definition.ForceJobId(jobId);
            chain.AddJobDefinitionToChain(definition);
            return chain;
        }

        /// <summary>What a station move does.</summary>
        internal enum Kind
        {
            /// <summary>A load spotted on its industry's track to be unloaded.</summary>
            SpotLoad,
            /// <summary>An empty spotted on its industry's track to be loaded.</summary>
            SpotEmpty,
            /// <summary>An emptied car pulled into the station's storage.</summary>
            Pull,
        }

        /// <summary>
        /// What a job does if it is a station move, read from its ID and type alone, so it holds
        /// for a job loaded from a save or shown on a Multiplayer client: a transport or empty haul
        /// whose ID has a shunting code ("CMS-SU-07"), which only a station move has. Null for any
        /// other job, and for a move written by 0.1.6 or earlier (its ID has the haul's code).
        /// </summary>
        internal static Kind? KindOf(string id, JobType type)
        {
            var parts = id?.Split('-');
            var code = parts == null || parts.Length < 2 ? null : parts[parts.Length - 2];
            switch (type)
            {
                case JobType.Transport when code == "SU":
                    return Kind.SpotLoad;
                case JobType.EmptyHaul when code == "SL":
                    return Kind.SpotEmpty;
                case JobType.EmptyHaul when code == "SU":
                    return Kind.Pull;
                default:
                    return null;
            }
        }

        /// <summary>The stock pay formula's input: the cars by livery and their cargo by type.</summary>
        private static PaymentCalculationData PaymentData(List<TrainCar> cars, List<CargoType> cargo)
        {
            var liveries = new Dictionary<TrainCarLivery, int>();
            foreach (var c in cars)
                liveries[c.carLivery] = liveries.TryGetValue(c.carLivery, out var n) ? n + 1 : 1;
            var cargos = new Dictionary<CargoType, int>();
            foreach (var c in cargo)
                cargos[c] = cargos.TryGetValue(c, out var n) ? n + 1 : 1;
            return new PaymentCalculationData(liveries, cargos);
        }
    }
}
