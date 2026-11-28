using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DV;
using DV.Garages;
using DV.LocoRestoration;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using UnityEngine;
using static System.FormattableString;
using RestorationState = DV.LocoRestoration.LocoRestorationController.RestorationState;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The game's restoration wrecks against the mod's ownership rules (#45), in the new-career
    /// check (NewCareerCheck; never shipped):
    /// - every wreck at the career's start: not the player's (not listed to sell), no licence
    ///   given for it, kept from Clear by the game itself;
    /// - with "restore", one wreck taken through the game's restoration to the end, each step by
    ///   the game's own code as a player's actions call it: its licences bought, the wreck
    ///   rerailed on its restoration track, the part ordered at the register, loaded at the
    ///   warehouse into a car on its track, brought to the restoration area, its installation paid,
    ///   the locomotive serviced at a pit stop, then painted. At each step it stays nobody's until
    ///   the game hands it over (serviced: its garage's demonstrator), and then the kiosk still
    ///   does not sell it.
    /// </summary>
    internal static class RestorationCheck
    {
        private static readonly AccessTools.FieldRef<LocoRestorationController, TrainCar> LocoOf =
            AccessTools.FieldRefAccess<LocoRestorationController, TrainCar>("loco");
        private static readonly AccessTools.FieldRef<LocoRestorationController, TrainCar> SecondOf =
            AccessTools.FieldRefAccess<LocoRestorationController, TrainCar>("secondCar");

        private static IEnumerable<TrainCar> CarsOf(LocoRestorationController r) => new[] { LocoOf(r), SecondOf(r) }.Where(c => c != null);

        /// <summary>The wrecks at a career's start, against the runtime's notes (licences given) and the kiosk's list.</summary>
        internal static void Start(IEnumerable<string> notes, Action<string> line, Action<string> problem)
        {
            var all = LocoRestorationController.allLocoRestorationControllers.Where(r => r != null).ToList();
            var owned = VehicleSales.Owned().SelectMany(u => u.Cars).ToList();
            var noteList = notes.ToList();
            line($"restoration: {all.Count} restorations; wrecks the mod reads: {RestorationWrecks.Cars().Count}");
            foreach (var r in all)
                line($"restoration {r.SaveID}: its part {r.locoPartCargo?.id} loads into " +
                     $"{string.Join(", ", r.locoPartCargo != null && Globals.G.Types.CargoToLoadableCarTypes.TryGetValue(r.locoPartCargo, out var t) ? t.Select(x => x.id) : new string[0])} at {r.warehouseMachineForPartPickup?.warehouseMachine?.WarehouseTrack?.ID?.FullDisplayID}");
            foreach (var r in all)
            {
                var cars = CarsOf(r).ToList();
                var licensedFor = cars.Where(c => noteList.Any(n => n.StartsWith("licenses:", StringComparison.Ordinal) && n.Contains(c.ID))).ToList();
                line($"restoration {r.SaveID}: {r.State}; cars {string.Join(", ", cars.Select(c => $"{c.ID} {c.carLivery.id} (kept from Clear {c.preventDelete}, derailed {c.derailed}, garage car {c.GetComponent<HomeGarageReference>() != null})"))}; " +
                     $"needs {r.locoLivery?.requiredLicense?.id} + {r.requiredRestorationLicense?.id}; garage {r.garageSpawner?.garageType?.id}; listed to sell {cars.Any(owned.Contains)}; licence given for it {licensedFor.Count > 0}");
                if (r.State < RestorationState.S9_LocoServiced)
                {
                    if (cars.Any(owned.Contains))
                        problem($"restoration {r.SaveID}: a wreck is listed as the player's");
                    if (licensedFor.Count > 0)
                        problem($"restoration {r.SaveID}: a licence was given for a wreck");
                    if (cars.Any(c => !c.preventDelete))
                        problem($"restoration {r.SaveID}: a wreck can be cleared (no preventDelete)");
                }
            }
        }

        /// <summary>One wreck (without a second car) through the game's restoration to the end.</summary>
        internal static IEnumerator Restore(Action<string> line, Action<string> problem)
        {
            var r = LocoRestorationController.allLocoRestorationControllers
                .Where(c => c != null && c.secondCarLivery == null && c.State == RestorationState.S0_Initialized && LocoOf(c) != null)
                .OrderBy(c => c.locoLivery.id == "LocoDM3" ? 0 : 1).FirstOrDefault();
            if (r == null)
            {
                problem("restore: no single-car wreck to restore");
                yield break;
            }
            var loco = LocoOf(r);
            var licences = SingletonBehaviour<LicenseManager>.Instance;
            line($"restore {r.SaveID}: {loco.ID} at {Invariant($"{loco.transform.position - DV.OriginShift.OriginShift.currentMove:F0}")}; destination {r.destinationTrackId}; " +
                 $"part {r.locoPartCargo?.id} at {r.warehouseMachineForPartPickup?.warehouseMachine?.WarehouseTrack?.ID?.FullDisplayID}; part costs ${r.orderPartsModule.price:N0}, installing ${r.installPartsModule.price:N0}");
            PlayerManager.TeleportPlayer(loco.transform.position + Vector3.up * 3f + loco.transform.right * 8f, Quaternion.identity, null, false);
            yield return new WaitForSecondsRealtime(20f);

            bool Still(string step)
            {
                var owned = VehicleSales.Owned().Any(u => u.Cars.Contains(loco));
                line($"restore: {step}: {r.State}; the player's {owned}; kept from Clear {loco.preventDelete}; garage car {loco.GetComponent<HomeGarageReference>() != null}");
                if (owned)
                    problem($"restore: the wreck is the player's at {r.State}");
                return !owned;
            }

            // S1, S2: its licences bought (the game unblocks its cab when both are held).
            var own = r.locoLivery.requiredLicense;
            var held = licences.IsGeneralLicenseAcquired(own);
            line($"restore: before buying: {own.id} held {held} (a free one would be the bug), {r.requiredRestorationLicense.id} held {licences.IsGeneralLicenseAcquired(r.requiredRestorationLicense)}");
            if (held && own.id != "DE2")
                problem($"restore: the career holds {own.id} without buying it");
            if (!held)
                licences.AcquireGeneralLicense(own);
            licences.AcquireGeneralLicense(r.requiredRestorationLicense);
            yield return new WaitForSecondsRealtime(2f);
            if (r.State != RestorationState.S2_LocoUnblocked)
                problem($"restore: buying its licences left it at {r.State}, not unblocked");
            Still("licences bought");

            // S3, S4: rerailed on its restoration track (the comms radio's rerail, at the track's middle).
            var track = SingletonBehaviour<RailTrackRegistryBase>.Instance.GetTrackWithName(r.destinationTrackId);
            if (track == null)
            {
                problem($"restore: no track {r.destinationTrackId}");
                yield break;
            }
            loco.Rerail(track, LocoShop.Middle(track), Forward(track));
            for (var waited = 0f; r.State < RestorationState.S4_OnDestinationTrack && waited < 20f; waited += 1f)
                yield return new WaitForSecondsRealtime(1f);
            if (r.State != RestorationState.S4_OnDestinationTrack)
                problem($"restore: rerailed on {r.destinationTrackId} it is at {r.State}");
            Still("on its track");

            // S5: the part bought at the register (the module the game puts in the cart).
            r.orderPartsModule.GetBoughtResource();
            yield return new WaitForSecondsRealtime(1f);
            if (r.State != RestorationState.S5_PartOrdered)
                problem($"restore: ordering the part left it at {r.State}");

            // S6: a car on the warehouse's track, loaded with the part as the warehouse machine loads it.
            var machine = r.warehouseMachineForPartPickup.warehouseMachine;
            var carType = Globals.G.Types.CargoToLoadableCarTypes.TryGetValue(r.locoPartCargo, out var types) ? types.FirstOrDefault() : null;
            var livery = carType?.liveries?.FirstOrDefault(l => l != null && l.prefab != null);
            var warehouseRail = RailTrackRegistry.LogicToRailTrack.TryGetValue(machine.WarehouseTrack, out var wr) ? wr : null;
            line($"restore: the part loads into {string.Join(", ", types?.Select(t => t.id) ?? new string[0])}; a {livery?.id} on {machine.WarehouseTrack.ID.FullDisplayID}");
            var carrier = livery == null || warehouseRail == null ? null : SpawnOn(livery, warehouseRail);
            if (carrier == null)
            {
                problem("restore: no car for the part on the warehouse's track");
                yield break;
            }
            yield return new WaitForSecondsRealtime(3f);
            var delivery = machine.GetCurrentLoadUnloadData(WarehouseTaskType.Loading).Select(d => d.specialDeliveryToProcess).FirstOrDefault(d => d != null);
            if (delivery == null)
            {
                problem("restore: the warehouse machine offers no special delivery for the car");
                yield break;
            }
            machine.LoadSpecialDelivery(delivery);
            yield return new WaitForSecondsRealtime(1f);
            if (r.State != RestorationState.S6_PartPickedUp)
                problem($"restore: loading the part left it at {r.State}");

            // S7: the car brought to the restoration area and stopped (checked every 10 s by the game).
            var spot = AreaSpot(r, loco);
            if (spot.track == null)
            {
                problem("restore: no free track spot in the restoration area");
                yield break;
            }
            // Moved as the comms radio's rerail moves a car: off the rails, then onto the spot.
            carrier.Derail(true);
            yield return new WaitForFixedUpdate();
            yield return new WaitForSecondsRealtime(1f);
            carrier.Rerail(spot.track, spot.position, Forward(spot.track));
            yield return new WaitForSecondsRealtime(3f);
            line(Invariant($"restore: the part's car rerailed {Vector3.Distance(carrier.transform.position, r.restorationAreaAnchor.position):F0} m from the area's anchor (radius {r.restorationAreaRadius}), derailed {carrier.derailed}"));
            for (var waited = 0f; r.State < RestorationState.S7_PartDelivered && waited < 40f; waited += 1f)
                yield return new WaitForSecondsRealtime(1f);
            if (r.State != RestorationState.S7_PartDelivered)
                problem($"restore: the part brought to the area ({Invariant($"{Vector3.Distance(carrier.transform.position, r.restorationAreaAnchor.position):F0}")} m from its anchor, radius {r.restorationAreaRadius}) left it at {r.State}");
            Still("part delivered");

            // S8: its installation paid at the register.
            r.installPartsModule.GetBoughtResource();
            yield return new WaitForSecondsRealtime(1f);
            if (r.State != RestorationState.S8_PartInstalled)
                problem($"restore: paying for the installation left it at {r.State}");
            Still("part installed");

            // S9: serviced as a pit stop does it (refilled, then its body repaired): the game hands it over.
            loco.SimController?.resourceContainerController?.RefillAllResourceContainers();
            loco.GetComponent<DV.PitStops.SimulatedCarPitStopParameters>()?.UpdateCarPitStopParameter(ResourceType.Car_DMG, 100f);
            yield return new WaitForSecondsRealtime(2f);
            if (r.State != RestorationState.S9_LocoServiced)
                problem($"restore: servicing it left it at {r.State}");
            var units = VehicleSales.Owned();
            var garage = loco.GetComponent<HomeGarageReference>() != null;
            line($"restore: serviced: {r.State}; garage car {garage} (garage {r.garageSpawner.garageType.id} unlocked {licences.IsGarageUnlocked(r.garageSpawner.garageType)}); " +
                 $"listed to sell {units.Any(u => u.Cars.Contains(loco))}; kept from Clear {loco.preventDelete}; the mod reads it as a wreck {RestorationWrecks.IsWreck(loco)}");
            if (!garage || units.Any(u => u.Cars.Contains(loco)) || RestorationWrecks.IsWreck(loco))
                problem("restore: once serviced it is not the player's garage demonstrator, or the kiosk would sell it");
            var notes = new List<string>();
            LicenseRules.LicenseOwned(new[] { loco }, notes);
            line($"restore: licences for it now: {(notes.Count == 0 ? "none to give (bought for the restoration)" : string.Join("; ", notes))}");

            // S10: painted.
            var theme = Resources.FindObjectsOfTypeAll<DV.Customization.Paint.PaintTheme>()
                .FirstOrDefault(t => t != null && t != r.abandonedTheme && t != r.primerTheme && t.name.IndexOf("Default", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? Resources.FindObjectsOfTypeAll<DV.Customization.Paint.PaintTheme>().FirstOrDefault(t => t != null && t != r.abandonedTheme && t != r.primerTheme);
            if (loco.PaintExterior != null)
                loco.PaintExterior.CurrentTheme = theme;
            if (loco.PaintInterior != null)
                loco.PaintInterior.CurrentTheme = theme;
            yield return new WaitForSecondsRealtime(1f);
            line($"restore: painted {theme?.name}: {r.State}");
            if (r.State != RestorationState.S10_PaintJobDone)
                problem($"restore: painting it left it at {r.State}");
            Still("restored");
        }

        private static Vector3 Forward(RailTrack track)
        {
            var points = track.GetKinkedPointSet().points;
            var i = Math.Max(1, points.Length / 2);
            return ((Vector3)points[Math.Min(i, points.Length - 1)].position - (Vector3)points[i - 1].position).normalized;
        }

        private static TrainCar SpawnOn(TrainCarLivery livery, RailTrack track)
        {
            var data = CarSpawner.GetTrackMiddleBasedSpawnData(new List<TrainCarLivery> { livery }, null, track, startSpan: 0.0, flipTrainConsist: false);
            if (data.result != CarSpawner.SpawnDataResult.OK)
                return null;
            return SingletonBehaviour<CarSpawner>.Instance.SpawnCars(data, preventAutoCoupleOnLastCars: true, applyHandbrakeOnLastCars: true)?.FirstOrDefault();
        }

        /// <summary>A point on a track inside the restoration area, clear of the locomotive.</summary>
        private static (RailTrack track, Vector3 position) AreaSpot(LocoRestorationController r, TrainCar loco)
        {
            var anchor = r.restorationAreaAnchor.position;
            var radius = r.restorationAreaRadius;
            var best = (track: (RailTrack)null, position: Vector3.zero, clear: 0f);
            foreach (var track in RailTrackRegistry.LogicToRailTrack.Values.Where(t => t != null))
                foreach (var p in track.GetKinkedPointSet().points)
                {
                    var at = (Vector3)p.position + DV.OriginShift.OriginShift.currentMove;
                    if ((at - anchor).sqrMagnitude > radius * radius * 0.6f)
                        continue;
                    var clear = (at - loco.transform.position).magnitude;
                    if (clear > best.clear)
                        best = (track, at, clear);
                }
            return best.clear > 12f ? (best.track, best.position) : (null, Vector3.zero);
        }
    }
}
