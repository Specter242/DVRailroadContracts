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
    /// stock shunting job being a spot and a pull. Returns null if the cars are mixed loaded and
    /// empty.
    /// </summary>
    internal static class StationMoves
    {
        public static JobChainController Move(StationController station, Track from, Track to, List<TrainCar> cars)
        {
            var cargo = cars.Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();
            var loaded = cargo.All(c => c != CargoType.None);
            if (!loaded && cargo.Any(c => c != CargoType.None))
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
            chain.AddJobDefinitionToChain(definition);
            return chain;
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
