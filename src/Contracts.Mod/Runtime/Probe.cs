using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Mod.Kiosk;
using DV;
using DV.InventorySystem;
using DV.ServicePenalty.UI;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The runtime's in-game check (probe.txt in the mod's folder, never shipped; run by
    /// scripts/run_spike.py --probe in a copy of the game). It writes probe.log beside the mod:
    /// - the career rules: every station flagged, no locomotive spawner running;
    /// - the economy and the interchange tracks;
    /// - the starting DE2 and the Manual Service licence (given as a new career would);
    /// - the shops' catalogues and prices (the stock locomotives and coaches, the custom car
    ///   loader's with their tenders), and purchases delivered at the nearest station;
    /// - a contract taken and started, a service run: the cars placed at the interchanges, their
    ///   waybills and their first jobs (types, stations and tracks);
    /// - the contract's standing: the day scored at midnight from its bills, a raise refused
    ///   without three good days and granted with them, a raise asked for refused at midnight
    ///   when the day's score pulls the average down;
    /// - the saved state's round trip;
    /// - errors logged meanwhile. Then the game quits, if probe.txt says "quit".
    /// </summary>
    internal sealed class Probe : MonoBehaviour
    {
        private static string folder;
        private static bool quitAfter;
        private StreamWriter log;
        private int problems;
        private static readonly List<string> errors = new List<string>();

        public static void Install(string modFolder)
        {
            folder = modFolder;
            var trigger = Path.Combine(folder, "probe.txt");
            if (!File.Exists(trigger))
                return;
            quitAfter = File.ReadAllText(trigger).IndexOf("quit", StringComparison.OrdinalIgnoreCase) >= 0;
            var go = new GameObject("[contracts probe]");
            DontDestroyOnLoad(go);
            var probe = go.AddComponent<Probe>();
            Application.logMessageReceived += (condition, stack, type) =>
            {
                if ((type == LogType.Exception || type == LogType.Error) && errors.Count < 300)
                    errors.Add($"{type}: {(condition ?? "").Split('\n')[0]} @ {(stack ?? "").Split('\n').FirstOrDefault()?.Trim()}");
            };
            var started = false;
            WorldStreamingInit.LoadingFinished += () =>
            {
                if (started)
                    return;
                started = true;
                probe.StartCoroutine(probe.Run());
            };
            Main.Log("probe requested");
        }

        private void Line(string text)
        {
            log?.WriteLine(text);
            log?.Flush();
            Main.Log("probe: " + text);
        }

        private void Problem(string text)
        {
            problems++;
            Line("PROBLEM: " + text);
        }

        private IEnumerator Run()
        {
            log = new StreamWriter(Path.Combine(folder, "probe.log"), false);
            Line($"probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            for (var waited = 0f; !(ContractRuntime.Active && AStartGameData.carsAndJobsLoadingFinished) && waited < 240f; waited += 1f)
                yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(5f);
            var rt = ContractRuntime.Instance;
            if (rt == null || !ContractRuntime.Active)
            {
                Problem("the runtime never started");
                yield return Finish();
                yield break;
            }
            foreach (var n in rt.Notes)
                Line("note: " + n);
            Try("career rules", CareerRulesCheck);
            yield return VisitCity("MF");
            Try("economy", () => EconomyCheck(rt));
            Try("starter", StarterCheck);
            yield return null;
            Try("shop", ShopCheck);
            yield return null;
            string taken = null;
            Try("contract and service", () => taken = ServiceCheck(rt));
            yield return new WaitForSeconds(3f);
            Try("jobs", () => JobsCheck(rt, taken));
            yield return Legs(rt, taken);
            // The industries work the cars spotted, and send them out; the legs then work the outbound.
            for (var cycle = 0; cycle < 3; cycle++)
            {
                var more = false;
                yield return Work(rt, taken, cycle, m => more = m);
                yield return Legs(rt, taken);
                if (!more)
                    break;
            }
            Try("every car between legs has a job, is worked, or waits for room", () => Settled(rt, taken));
            string moved = null;
            Try("a set-out car moved off the interchange", () => moved = MoveSetOut(rt, taken));
            yield return new WaitForSeconds(1f);
            Try("the desk after the set-out car moved", () => SetOutDesk(rt, moved));
            Try("second service", () => SecondService(rt, taken));
            Try("the moved car after the second service", () => MovedAfterService(rt, moved));
            yield return new WaitForSeconds(2f);
            Try("jobs after the second service", () => JobsCheck(rt, taken));
            Try("standing", () => StandingCheck(rt, taken));
            yield return PayCheck(rt, taken);
            Try("save round trip", () => SaveCheck(rt));
            yield return KioskLoad(rt);
            yield return Finish();
        }

        private void Try(string what, Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Problem($"{what} failed: {e}");
            }
        }

        private void CareerRulesCheck()
        {
            var stations = StationController.allStations.Where(s => s != null && s.logicStation != null).ToList();
            var unflagged = stations.Where(s => !PersistentJobs.HasSpawned(s.logicStation.ID)).Select(s => s.logicStation.ID).ToList();
            Line($"career: {stations.Count} stations, {stations.Count - unflagged.Count} flagged as spawned{(unflagged.Count > 0 ? "; not: " + string.Join(", ", unflagged) : "")}");
            if (unflagged.Count > 0)
                Problem("stations not flagged: " + string.Join(", ", unflagged));
            Line("Persistent Jobs members missing: " + (PersistentJobs.Missing.Count == 0 ? "none" : string.Join(", ", PersistentJobs.Missing)));
            if (PersistentJobs.Missing.Count > 0)
                Problem("Persistent Jobs members missing");
            var cars = SingletonBehaviour<CarSpawner>.Instance.AllCars;
            Line($"career: {cars.Count} car(s) in the world at the start: " +
                 string.Join(", ", cars.GroupBy(c => c.carLivery?.id).Select(g => $"{g.Key} x{g.Count()}")));
            var spawners = FindObjectsOfType<StationLocoSpawner>();
            Line($"career: {spawners.Length} station locomotive spawners (their Update is blocked)");
        }

        /// <summary>
        /// Stands the player at a city station (a passenger station too) for a while: no car, coach,
        /// locomotive or stock job may appear there (design D15, D17).
        /// </summary>
        private IEnumerator VisitCity(string yard)
        {
            var station = StationController.allStations.FirstOrDefault(s => s != null && s.stationInfo.YardID == yard);
            if (station == null)
            {
                Problem($"no station {yard}");
                yield break;
            }
            var before = new HashSet<TrainCar>(SingletonBehaviour<CarSpawner>.Instance.AllCars);
            var jobsBefore = station.logicStation.availableJobs.Count;
            PlayerManager.TeleportPlayer(station.transform.position + Vector3.up * 2f, Quaternion.identity, null, false);
            yield return new WaitForSeconds(45f);
            var added = SingletonBehaviour<CarSpawner>.Instance.AllCars.Where(c => !before.Contains(c)).ToList();
            var jobs = station.logicStation.availableJobs.Count;
            Line($"visit {yard}: {added.Count} car(s) appeared ({string.Join(", ", added.GroupBy(c => c.carLivery?.id).Select(g => $"{g.Key} x{g.Count()}"))}); " +
                 $"{jobs} job(s) on offer (before {jobsBefore}); spawned flag {PersistentJobs.HasSpawned(station.logicStation.ID)}");
            if (added.Count > 0 || jobs > jobsBefore)
                Problem($"cars or jobs appeared at {yard}");
        }

        private void EconomyCheck(ContractRuntime rt)
        {
            var all = InterchangeYards.All();
            foreach (var kv in all)
                Line($"interchange {kv.Key}: {kv.Value.Count} tracks: " + string.Join(", ", kv.Value.Select(t => $"{t.ID.FullDisplayID} {t.length:F0} m")));
            if (all.Count != 2)
                Problem($"{all.Count} interchanges found");
            var e = rt.Economy;
            Line($"economy: {e?.Industries?.Count ?? 0} industries, data {(e?.Data == null ? "missing" : "built")}");
            if (e?.Data == null)
                Problem("no economy data");
        }

        private void StarterCheck()
        {
            var licences = SingletonBehaviour<LicenseManager>.Instance;
            var before = SingletonBehaviour<CarSpawner>.Instance.AllCars.Count(c => c.uniqueCar);
            var notes = new List<string>();
            var given = CareerRules.GiveStarter(notes);
            foreach (var n in notes)
                Line(n);
            var de2 = SingletonBehaviour<CarSpawner>.Instance.AllCars.Where(c => c.uniqueCar && c.carLivery == TrainCarType.LocoShunter.ToV2()).ToList();
            var manual = licences.IsGeneralLicenseAcquired(GeneralLicenseType.ManualService.ToV2());
            Line($"starter: given {given}; owned DE2s {de2.Count} ({string.Join(", ", de2.Select(c => $"{c.ID} on {c.logicCar.FrontBogieTrack?.ID?.FullDisplayID}"))}); Manual Service {manual}; owned cars before {before}");
            if (!given || de2.Count == 0 || !manual)
                Problem("the starter was not given");
        }

        private void ShopCheck()
        {
            var locos = LocoShop.Catalogue(LocoShop.Kind.Locomotives);
            var coaches = LocoShop.Catalogue(LocoShop.Kind.Coaches);
            foreach (var (kind, items) in new[] { ("locomotives", locos), ("coaches", coaches) })
                foreach (var item in items)
                    Line($"shop {kind}: {item.Name}: ${item.Price:N0} ({string.Join(" + ", item.Cars.Select(c => $"{c.id} [{c.parentType?.kind?.id}]"))})");
            var s282 = locos.FirstOrDefault(i => i.Name == "S282");
            if (s282 == null || s282.Cars.Count != 2)
                Problem("the S282 is not sold with its tender");
            if (locos.Concat(coaches).Any(i => i.Name.IndexOf("tender", StringComparison.OrdinalIgnoreCase) >= 0))
                Problem("a name mentions its tender");
            var custom = Globals.G.Types.carTypes.Where(t => t?.GetType().Name == "CCL_CarType" && t.liveries != null).SelectMany(t => t.liveries)
                .Where(l => !l.isHidden && CarTypes.IsLocomotive(l)).ToList();
            var unsold = custom.Where(l => !locos.Any(i => i.Cars.Contains(l))).Select(l => l.id).ToList();
            Line($"shop: {custom.Count} custom locomotive liveries loaded; not sold: {(unsold.Count == 0 ? "none" : string.Join(", ", unsold))}");
            if (unsold.Count > 0)
                Problem("custom locomotives missing from the shop");
            var lonelySteam = locos.Where(i => i.Cars.Count == 1 && i.Cars[0].GetType().Name == "CCL_CarVariant" &&
                                               Globals.G.Types.carTypes.Any(t => t.liveries != null && t.liveries.Any(l => CarTypes.IsTender(l) && l.id.StartsWith(i.Cars[0].id.Split('_')[0], StringComparison.OrdinalIgnoreCase))))
                .Select(i => i.Name).ToList();
            if (lonelySteam.Count > 0)
                Problem("custom locomotives with a tender of their own sold without it: " + string.Join(", ", lonelySteam));
            if (coaches.Count < 3)
                Problem("the stock coaches are not all on sale");
            if (locos.Concat(coaches).Any(i => i.Price < 1000f))
                Problem("something costs under $1,000: " + string.Join(", ", locos.Concat(coaches).Where(i => i.Price < 1000f).Select(i => i.Name)));

            var inventory = SingletonBehaviour<Inventory>.Instance;
            var player = PlayerManager.PlayerTransform;
            var station = StationController.allStations.OrderBy(s => (s.transform.position - player.position).sqrMagnitude).First();
            var place = new KioskPlace { Id = station.stationInfo.YardID, Name = station.stationInfo.Name };
            var buys = new List<LocoShop.Item> { locos.First(i => i.Name == "DE6") };
            var tendered = locos.FirstOrDefault(i => i.Cars.Count > 1 && i.Cars[0].GetType().Name == "CCL_CarVariant");
            if (tendered != null)
                buys.Add(tendered);
            buys.Add(coaches.First());
            foreach (var item in buys)
            {
                inventory.AddMoney(item.Price + 1000f);
                var money = inventory.PlayerMoney;
                var existing = new HashSet<TrainCar>(SingletonBehaviour<CarSpawner>.Instance.AllCars);
                var before = existing.Count;
                var message = LocoShop.Buy(item, place);
                var after = SingletonBehaviour<CarSpawner>.Instance.AllCars.Count;
                var added = SingletonBehaviour<CarSpawner>.Instance.AllCars.Where(c => !existing.Contains(c)).ToList();
                Line($"shop: bought {item.Name} at {place.Id}: {message.Replace("\n", " ")}; money {money:N0} -> {inventory.PlayerMoney:N0}; cars +{after - before}");
                if (Math.Abs(money - item.Price - inventory.PlayerMoney) > 1.0 || after - before != item.Cars.Count)
                    Problem($"{item.Name} was not paid for or not delivered whole");
                // Whatever is bought arrives at an interchange, as every car does (owner, 2026-11-03).
                if (added.Any(c => !InterchangeYards.IsInterchangeTrack(c.logicCar?.FrontBogieTrack)))
                    Problem($"{item.Name} was delivered off the interchanges: {string.Join(", ", added.Select(c => c.logicCar?.FrontBogieTrack?.ID?.FullDisplayID))}");
            }
        }

        /// <summary>Takes a contract at an industry near an interchange, starts it, runs a service.</summary>
        private string ServiceCheck(ContractRuntime rt)
        {
            var desk = KioskMenu.Desk;
            var industry = rt.Economy.Industries.Values.FirstOrDefault(i => i.Id == "MF") ?? rt.Economy.Industries.Values.First();
            var offer = desk.Offers(new KioskPlace { Id = industry.Station, Name = industry.Station }).FirstOrDefault(o => o.Id == industry.Id);
            if (offer == null)
            {
                Problem($"no offer for {industry.Id}");
                return null;
            }
            desk.Take(offer, 3);
            var held = rt.State.Contracts.First(c => c.Id == industry.Id);
            Line($"contract: took {industry.Id}: level {held.Level}, asks {held.Requested} (a new contract starts at level 1)");
            if (held.Requested != 1)
                Problem("a new contract does not start at level 1");
            held.Level = 1;
            Line($"contract: {industry.Id} at level 1: {desk.Volume(offer, 1)} cars a day (out / in / waste)");
            var day = ContractRuntime.DayOf(ContractRuntime.Now ?? DateTime.Now) + 1000;
            serviceDay = day;
            var summary = SavedMidService(rt, day);
            Line("service: " + summary);
            var placed = rt.State.Waybills.Where(w => w.Contract == industry.Id).ToList();
            Line($"service: {placed.Count} waybill(s) for {industry.Id}: " +
                 string.Join("; ", placed.GroupBy(w => (w.Stage, w.Cargo, w.Next, w.Then, w.At)).Select(g => $"{g.Count()} x {g.Key.Stage} {g.Key.Cargo ?? "empty"} at {g.Key.At} -> {g.Key.Next}{(g.Key.Then != null ? " then " + g.Key.Then : "")}")));
            if (placed.Count == 0)
                Problem("the service placed no cars");
            return industry.Id;
        }

        /// <summary>
        /// A save while the service places its cars (it goes a step a frame): the service is begun
        /// and its first cut placed, then the game gathers a save. The save finishes the service
        /// first, so it holds every car placed, every car's bill, and every job. Returns the
        /// service's summary.
        /// </summary>
        private string SavedMidService(ContractRuntime rt, int day)
        {
            if (!rt.BeginService(day))
                return "no economy";
            var atOnce = rt.CutsAtOnce();
            rt.StepService();
            var placedBefore = rt.State.Waybills.Count;
            var saves = SingletonBehaviour<SaveGameManager>.Instance;
            HarmonyLib.AccessTools.Method(typeof(SaveGameManager), "UpdateInternalData").Invoke(saves, null);
            var done = rt.LastService;
            if (rt.Servicing || done == null || done.Day != day)
            {
                Problem("save mid-service: the save did not finish the service under way");
                rt.FinishService();
                return rt.LastService?.Summary;
            }
            var saved = ContractState.Read(saves.data, out var unreadable);
            var cars = saves.data.GetJObject(SaveGameKeys.Cars)?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var guids = rt.State.Waybills.Select(w => w.Guid).ToList();
            var notSaved = guids.Count(g => !cars.Contains(g));
            var billsSaved = saved == null ? -1 : saved.Waybills.Count(w => guids.Contains(w.Guid));
            var jobless = rt.State.Waybills.Where(w => w.Stage != Stage.Idle && w.Stage != Stage.OffMap)
                .Count(w => registry.GetTrainCarByCarGuid(w.Guid) is TrainCar c && jobs.GetJobOfCar(c.logicCar) == null);
            Line($"save mid-service: {placedBefore} bill(s) after the first step; the save finished the service ({done.Work.Run} step(s)): " +
                 $"{guids.Count} bill(s), {billsSaved} in the saved state (unreadable {unreadable}), {guids.Count - notSaved} of their cars in the saved cars, {jobless} billed car(s) without a job");
            if (placedBefore >= guids.Count)
                Problem("save mid-service: the first step placed every car, so the save did not interrupt the placement");
            if (unreadable || billsSaved != guids.Count || notSaved > 0 || jobless > 0)
                Problem("save mid-service: the save does not hold the whole service");
            SameCuts("save mid-service", atOnce, done);
            return done.Summary;
        }

        /// <summary>The cuts the steps placed are those the service would have placed at once.</summary>
        private void SameCuts(string what, List<string> atOnce, ContractRuntime.ServiceRun done)
        {
            var same = atOnce != null && atOnce.SequenceEqual(done.Cuts);
            Line($"{what}: {done.Cuts.Count} cut(s) placed, {(same ? "the same as" : "NOT the same as")} the {atOnce?.Count ?? 0} at once: {string.Join("; ", done.Cuts)}");
            if (!same)
            {
                Line($"{what}: at once would be: {string.Join("; ", atOnce ?? new List<string>())}");
                Problem($"{what}: the steps placed other cuts than the service at once");
            }
        }

        private void JobsCheck(ContractRuntime rt, string contract)
        {
            if (contract == null)
                return;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var cars = rt.State.Waybills.Where(w => w.Contract == contract).Select(w => registry.GetTrainCarByCarGuid(w.Guid)).Where(c => c != null).ToList();
            var byJob = cars.GroupBy(c => jobs.GetJobOfCar(c.logicCar)).ToList();
            foreach (var g in byJob)
            {
                var job = g.Key;
                var where = string.Join(", ", g.Select(c => c.logicCar.FrontBogieTrack?.ID?.FullDisplayID).Distinct());
                Line(job == null
                    ? $"jobs: {g.Count()} car(s) without a job, on {where}"
                    : $"jobs: {job.ID} {job.jobType} {job.chainData?.chainOriginYardId}->{job.chainData?.chainDestinationYardId}, {g.Count()} car(s) on {where}, wage {job.GetBasePaymentForTheJob():F0}, state {job.State}");
            }
            var without = byJob.Where(g => g.Key == null).Sum(g => g.Count());
            if (cars.Count > 0 && without == cars.Count)
                Problem("no contract car got a job");
            var onInterchange = cars.Count(c => InterchangeYards.IsInterchangeTrack(c.logicCar.FrontBogieTrack));
            Line($"jobs: {cars.Count} contract car(s), {onInterchange} on interchange tracks, {without} without a job");
        }

        private int serviceDay;

        /// <summary>
        /// Works the contract cars' jobs leg by leg, as a player would: each job is taken, its cars
        /// moved onto its destination track, loaded or unloaded as its warehouse would, and the job
        /// completed. The runtime should write each car's next leg as its job completes, until the
        /// cars rest empty at a receiver or wait on an interchange track to leave.
        /// </summary>
        private IEnumerator Legs(ContractRuntime rt, string contract)
        {
            if (contract == null)
                yield break;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var done = 0;
            for (var round = 0; round < 6; round++)
            {
                var cars = rt.State.Waybills.Where(w => w.Contract == contract).Select(w => registry.GetTrainCarByCarGuid(w.Guid)).Where(c => c != null).ToList();
                var pending = cars.Select(c => jobs.GetJobOfCar(c.logicCar)).Where(j => j != null && (j.State == JobState.Available || j.State == JobState.InProgress)).Distinct().ToList();
                if (pending.Count == 0)
                    break;
                foreach (var job in pending)
                {
                    var jobCars = cars.Where(c => jobs.GetJobOfCar(c.logicCar) == job).ToList();
                    string before = null, outcome = null;
                    try
                    {
                        before = Prepare(rt, job, jobCars);
                    }
                    catch (Exception e)
                    {
                        Problem($"working job {job.ID} failed: {e}");
                        continue;
                    }
                    // Let the moved cars settle on their track before the job completes, as on arrival.
                    yield return new WaitForSeconds(0.5f);
                    try
                    {
                        outcome = Complete(rt, job, jobCars, before);
                        done++;
                    }
                    catch (Exception e)
                    {
                        Problem($"completing job {job.ID} failed: {e}");
                        continue;
                    }
                    Line(outcome);
                }
                yield return new WaitForSeconds(1f);
            }
            var stages = rt.State.Waybills.Where(w => w.Contract == contract).GroupBy(w => w.Stage).Select(g => $"{g.Key} {g.Count()}");
            Line($"legs: {done} job(s) worked; the cars now: {string.Join(", ", stages)}");
            var at = rt.State.Waybills.Count(w => w.Contract == contract && ContractRuntime.Worked(w));
            if (at > 0)
                Line($"legs: {at} car(s) at the industry, worked there");
        }

        /// <summary>The taken contract's cars after the work: none between legs without a job, unless at its industry (worked, or waiting for room).</summary>
        private void Settled(ContractRuntime rt, string contract)
        {
            if (contract == null)
                return;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var stuck = new List<string>();
            foreach (var w in rt.State.Waybills.Where(w => w.Contract == contract && w.Stage != Stage.Idle && w.Stage != Stage.OffMap))
            {
                var car = registry.GetTrainCarByCarGuid(w.Guid);
                if (car == null || jobs.GetJobOfCar(car.logicCar) != null || ContractRuntime.Worked(w) && rt.OnIndustryTrack(car, w) || rt.WaitsToSpot(car, w))
                    continue;
                stuck.Add($"{car.ID} {w.Stage} on {car.logicCar.FrontBogieTrack?.ID?.FullDisplayID ?? "no track"}");
            }
            Line($"settled: {(stuck.Count == 0 ? "every car between legs has a job, is at its industry, or waits for room" : string.Join("; ", stuck))}");
            if (stuck.Count > 0)
                Problem($"{stuck.Count} car(s) are between legs with no job: {string.Join("; ", stuck)}");
        }

        /// <summary>
        /// The industries' work (design, "Industry work"): the cars the legs spotted are on their
        /// industry's tracks, each line's cars given ready times a car at a time at the contract's
        /// rate; done, a car is loaded or emptied and goes out at once, and the next car done on
        /// the same track for the same place joins its outbound job while it is not yet taken. The
        /// clock is moved on by hand: a car's ready time is set to now and the work run (the bills
        /// then open now, not in the future). Then every car is done, and those waiting for room
        /// are tried again. Tells the caller whether cars may be left to spot.
        /// </summary>
        private IEnumerator Work(ContractRuntime rt, string contract, int cycle, Action<bool> more)
        {
            more(false);
            if (contract == null)
                yield break;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var now = ContractRuntime.Clock ?? 0.0;
            List<Waybill> Worked() => rt.State.Waybills.Where(w => w.Contract == contract && ContractRuntime.Worked(w)).ToList();
            TrainCar CarOf(Waybill w) => registry.GetTrainCarByCarGuid(w.Guid);
            var worked = Worked();
            if (worked.Count == 0)
            {
                if (cycle == 0)
                    Problem("work: the legs spotted no car at its industry");
                yield break;
            }
            // Their ready times, a car at a time per line.
            Try("work: ready times", () => rt.WorkIndustries(now));
            Line($"work (cycle {cycle}): {worked.Count} car(s) at the industry: " + string.Join("; ", worked.OrderBy(w => w.Ready ?? 0).Select(w =>
            {
                var car = CarOf(w);
                return $"{car?.ID} {(w.Stage == Stage.BeingLoaded ? "loaded with " + w.Cargo : "emptied of " + car?.logicCar.CurrentCargoTypeInCar)} " +
                       $"on {car?.logicCar.FrontBogieTrack?.ID?.FullDisplayID} {(w.Ready is double r ? IndustryWork.When(r, now) : "no ready time")}";
            })));
            foreach (var w in worked)
            {
                var car = CarOf(w);
                if (car == null || !rt.OnIndustryTrack(car, w))
                    Problem($"work: {car?.ID ?? w.Guid} is at its industry but not on its industry track");
                if (w.Ready == null || w.Ready <= now)
                    Problem($"work: {car?.ID ?? w.Guid} has no ready time ahead of it");
                if (w.Opened != null)
                    Problem($"work: {car?.ID ?? w.Guid} has a bill open while its industry works it");
            }
            // A save while they are worked keeps where each is in its work.
            Try("work: a save while cars are worked", () =>
            {
                var data = SaveGameManager.MakeEmptySave();
                rt.State.Write(data);
                var back = ContractState.Read(data, out _);
                var lost = worked.Where(w => back?.Of(w.Guid) is Waybill b ? b.Stage != w.Stage || b.Spotted != w.Spotted || b.Ready != w.Ready : true).ToList();
                Line($"work: saved and read back: {worked.Count - lost.Count} of {worked.Count} car(s) with their stage, spot and ready times");
                if (lost.Count > 0)
                    Problem($"work: {lost.Count} car(s) lost their place in the industry's work through a save");
            });
            var desk = KioskMenu.Desk.Jobless();
            Line($"work: the desk's cars without jobs: {desk.Count}: {desk.Text?.Replace("\n\n", " / ")}");

            // Joining: the first loaded car done, then the next done on the same track for the same place.
            if (cycle == 0)
            {
                var loads = worked.Where(w => w.Stage == Stage.BeingLoaded && w.Ready.HasValue).OrderBy(w => w.Ready).ToList();
                // Two loads on the same track for the same place, if there are such; else the first.
                var pair = loads.GroupBy(w => (Track: CarOf(w)?.logicCar.FrontBogieTrack, w.Then)).FirstOrDefault(g => g.Key.Track != null && g.Count() >= 2)?.ToList();
                var first = pair?[0] ?? loads.FirstOrDefault();
                var second = pair?[1];
                if (first != null)
                {
                    var cargo = first.Cargo;
                    first.Ready = now;
                    Try("work: the first car done", () => rt.WorkIndustries(now));
                    yield return null;
                    var car = CarOf(first);
                    var job = car == null ? null : jobs.GetJobOfCar(car.logicCar);
                    Line($"work: {car?.ID} done: {car?.logicCar.CurrentCargoTypeInCar} in it, {first.Stage} to {first.Next}, bill opened {first.Opened:F3}; " +
                         $"job {job?.ID ?? "none"} {job?.jobType} with {(job == null ? 0 : ContractRuntime.JobCars(job).Count())} car(s), {job?.State}");
                    if (car == null || car.logicCar.CurrentCargoTypeInCar.ToString() != cargo)
                        Problem($"work: {car?.ID ?? first.Guid} was not loaded with {cargo}");
                    if (job == null)
                        Problem("work: a car its industry was done with got no outbound job");
                    if (second != null && job != null)
                    {
                        second.Ready = now;
                        Try("work: the second car done", () => rt.WorkIndustries(now));
                        yield return null;
                        var car2 = CarOf(second);
                        var job2 = car2 == null ? null : jobs.GetJobOfCar(car2.logicCar);
                        var joined = job2 != null && jobs.GetJobOfCar(car.logicCar) == job2;
                        Line($"work: {car2?.ID} done for the same place on the same track: job {job2?.ID ?? "none"} with " +
                             $"{(job2 == null ? 0 : ContractRuntime.JobCars(job2).Count())} car(s), {(joined ? "joined with" : "apart from")} {car.ID} (its job {job.ID} now {job.State})");
                        if (!joined)
                            Problem("work: a car done did not join the outbound job not yet taken from its track to the same place");
                    }
                    else
                        Line("work: no second loaded car for the same place on the same track; joining not seen");
                }
                else
                    Line("work: no car being loaded; the first car out not seen");
            }

            // The rest done.
            foreach (var w in Worked())
                w.Ready = now;
            var done = 0;
            Try("work: the rest done", () => done = rt.WorkIndustries(now));
            yield return null;
            var left = Worked();
            var after = worked.Select(w => (W: w, Car: CarOf(w))).Where(x => x.Car != null).Select(x => (x.W, x.Car, Job: jobs.GetJobOfCar(x.Car.logicCar))).ToList();
            Line($"work: {done} more car(s) done; now " + string.Join(", ", after.GroupBy(x => (x.W.Stage, x.W.Next, Job: x.Job?.jobType.ToString() ?? "no job"))
                .Select(g => $"{g.Count()} {g.Key.Stage} to {g.Key.Next ?? "-"} ({g.Key.Job})")));
            if (left.Count > 0)
                Problem($"work: {left.Count} car(s) still at the industry with their ready time past");
            foreach (var x in after.Where(x => x.W.Stage == Stage.ToReceiver || x.W.Stage == Stage.ToInterchange || x.W.Stage == Stage.ToShipper))
                if (x.Job == null)
                    Problem($"work: {x.Car.ID} went out ({x.W.Stage} to {x.W.Next}) without a job");
            var emptied = after.Where(x => x.W.Cargo == null && x.Car.logicCar.CurrentCargoTypeInCar != CargoType.None).ToList();
            if (emptied.Count > 0)
                Problem($"work: {emptied.Count} car(s) billed empty still hold cargo");
            var outbound = after.Where(x => x.Job != null).Select(x => x.Job).Distinct().ToList();
            Line($"work: outbound jobs: {string.Join(", ", outbound.Select(j => $"{j.ID} {j.jobType} {j.chainData?.chainOriginYardId}->{j.chainData?.chainDestinationYardId} {ContractRuntime.JobCars(j).Count()} car(s) wage {j.GetBasePaymentForTheJob():F0}"))}");

            // Waiting for room: tried again now that the track is being cleared.
            rt.SpotWaiting();
            var waiting = rt.State.Waybills.Where(w => w.Contract == contract && (w.Stage == Stage.Loading || w.Stage == Stage.Unloading)).ToList();
            if (waiting.Count > 0)
                Line($"work: {waiting.Count} car(s) at their industry's station to spot ({waiting.Count(w => CarOf(w) != null && jobs.GetJobOfCar(CarOf(w).logicCar) != null)} with a spot job)");
            more(waiting.Count > 0);
        }

        /// <summary>Takes a job, moves its cars onto its destination and loads or unloads them as its warehouse would.</summary>
        private string Prepare(ContractRuntime rt, Job job, List<TrainCar> cars)
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var destination = ContractRuntime.JobDestination(job);
            var before = string.Join("/", cars.Select(c => rt.State.Of(c.logicCar.carGuid)?.Stage.ToString()).Distinct());
            if (job.State == JobState.Available)
                jobs.TakeJob(job);
            var rail = destination != null && RailTrackRegistry.LogicToRailTrack.TryGetValue(destination, out var r) ? r : null;
            if (rail != null)
                MoveOnto(cars, rail);
            if (job.jobType == JobType.ShuntingLoad)
                foreach (var c in cars)
                {
                    var w = rt.State.Of(c.logicCar.carGuid);
                    if (w?.Cargo != null && Enum.TryParse(w.Cargo, out CargoType cargo))
                        c.logicCar.LoadCargo(c.logicCar.capacity, cargo);
                }
            if (job.jobType == JobType.ShuntingUnload)
                foreach (var c in cars)
                    c.logicCar.DumpCargo();
            return before;
        }

        private string Complete(ContractRuntime rt, Job job, List<TrainCar> cars, string before)
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var destination = ContractRuntime.JobDestination(job);
            jobs.CompleteTheJob(job);
            var after = cars.Select(c => (Stage: rt.State.Of(c.logicCar.carGuid)?.Stage.ToString() ?? "gone", Job: jobs.GetJobOfCar(c.logicCar))).ToList();
            var next = after.Where(a => a.Job != null && a.Job != job).Select(a => $"{a.Job.ID} {a.Job.jobType} {a.Job.chainData?.chainOriginYardId}->{a.Job.chainData?.chainDestinationYardId}").Distinct();
            return $"leg: {job.ID} {job.jobType} worked, {cars.Count} car(s) to {destination?.ID?.FullDisplayID ?? "?"}: {before} -> " +
                   $"{string.Join("/", after.Select(a => a.Stage).Distinct())}; next {(next.Any() ? string.Join(", ", next) : "none")}";
        }

        // The cars this probe moved onto each track and where (span from, to), so moved cars never overlap.
        private readonly Dictionary<RailTrack, List<(TrainCar Car, double From, double To)>> placed =
            new Dictionary<RailTrack, List<(TrainCar Car, double From, double To)>>();

        /// <summary>
        /// Moves cars onto a track one after another, from 40 % of its length (or earlier, where
        /// they wouldn't fit after it: an industry track's room is its free length), into the
        /// first gap left by the cars this probe moved there before and that are still there.
        /// </summary>
        private void MoveOnto(List<TrainCar> cars, RailTrack rail)
        {
            var points = rail.GetKinkedPointSet().points;
            var total = points[points.Length - 1].span;
            foreach (var list in placed.Values)
                list.RemoveAll(x => x.Car == null || cars.Contains(x.Car));
            if (!placed.TryGetValue(rail, out var here))
                placed[rail] = here = new List<(TrainCar Car, double From, double To)>();
            var logic = RailTrackRegistry.LogicToRailTrack.FirstOrDefault(kv => kv.Value == rail).Key;
            here.RemoveAll(x => x.Car.logicCar.FrontBogieTrack != logic && x.Car.logicCar.RearBogieTrack != logic);
            var needed = cars.Sum(c => c.InterCouplerDistance + 2.0);
            var span = here.Count == 0 ? Math.Max(total * 0.05, Math.Min(total * 0.4, total - needed - 25.0)) : Math.Max(12.0, total * 0.03);
            foreach (var car in cars)
            {
                var length = car.InterCouplerDistance;
                // Past every car already there that this one would overlap.
                for (var moved = true; moved;)
                {
                    moved = false;
                    foreach (var x in here)
                        if (span < x.To + 2.0 && span + length > x.From - 2.0)
                        {
                            span = x.To + 2.0;
                            moved = true;
                        }
                }
                var at = span + length / 2.0;
                var i = 0;
                while (i < points.Length - 1 && points[i].span < at)
                    i++;
                var p = points[i];
                car.MoveToTrackWithCarUncouple(rail, (Vector3)p.position + DV.OriginShift.OriginShift.currentMove, (Vector3)p.forward);
                here.Add((car, span, span + length));
                span += length + 2.0;
            }
        }

        /// <summary>
        /// A car set out for pickup, then moved off the interchange (owner, 2026-11-03: two
        /// refrigerator cars on the West Interchange with no job number): the desk lists the cars
        /// without jobs and why, and the next service brings the moved one back to idle instead of
        /// leaving it waiting for good. Moves one set-out car onto a free storage track of the
        /// interchange's station; returns its guid.
        /// </summary>
        private string MoveSetOut(ContractRuntime rt, string contract)
        {
            if (contract == null)
                return null;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            var before = KioskMenu.Desk.Jobless();
            Line($"set-out moved: the desk's cars without jobs before: {before.Count}: {before.Text?.Replace("\n\n", " | ")}");
            var setOut = rt.State.Waybills.Where(w => w.Contract == contract && w.Stage == Stage.OffMap)
                .Select(w => (W: w, Car: registry.GetTrainCarByCarGuid(w.Guid)))
                .Where(x => x.Car != null && InterchangeYards.IsInterchangeTrack(x.Car.logicCar.FrontBogieTrack)).ToList();
            if (setOut.Count == 0)
            {
                Line("set-out moved: no car set out for pickup to move");
                return null;
            }
            if (before.Count < setOut.Count || before.Text == null || !setOut.All(x => before.Text.Contains(x.Car.ID)) || !before.Text.Contains("pickup"))
                Problem("set-out moved: the desk does not list the cars set out for pickup");
            var (w0, car) = setOut[0];
            var station = Economy.Station(InterchangeYards.StationOf(InterchangeYards.InterchangeOf(car.logicCar.FrontBogieTrack)));
            var length = SingletonBehaviour<CarSpawner>.Instance.GetTotalTrainCarsLength(new List<Car> { car.logicCar }, true);
            var track = station?.logicStation?.yard?.StorageTracks
                .FirstOrDefault(t => t.GetCarsFullyOnTrack().Count == 0 && t.GetCarsPartiallyOnTrack().Count == 0 && organizer.GetFreeSpaceOnTrack(t) > length + 20.0);
            var rail = track != null && RailTrackRegistry.LogicToRailTrack.TryGetValue(track, out var r) ? r : null;
            if (rail == null)
            {
                Problem($"set-out moved: no free storage track at {station?.stationInfo?.YardID ?? "?"} to move {car.ID} to");
                return null;
            }
            MoveOnto(new List<TrainCar> { car }, rail);
            Line($"set-out moved: {car.ID} from the interchange to {track.ID.FullDisplayID}");
            return w0.Guid;
        }

        /// <summary>
        /// A second after the move (its bogies on the new track): the desk says the car was moved
        /// off the interchange, and the list's "Cars without jobs" row opens them all.
        /// </summary>
        private void SetOutDesk(ContractRuntime rt, string guid)
        {
            if (guid == null)
                return;
            var car = SingletonBehaviour<TrainCarRegistry>.Instance.GetTrainCarByCarGuid(guid);
            if (car == null)
            {
                Problem("set-out moved: the moved car is gone");
                return;
            }
            var w0 = rt.State.Of(guid);
            var after = KioskMenu.Desk.Jobless();
            Line($"set-out moved: the desk now: {after.Text?.Replace("\n\n", " | ")}");
            var group = after.Text?.Split(new[] { "\n\n" }, StringSplitOptions.None).FirstOrDefault(g => g.StartsWith("Moved off the interchange"));
            if (group == null || !group.Contains(car.ID))
                Problem("set-out moved: the desk does not say the car was moved off the interchange");

            // At a kiosk: the list's "Cars without jobs" row, with their count, opens the paragraph.
            var main = Resources.FindObjectsOfTypeAll<CareerManagerMainScreen>().FirstOrDefault(m => m.gameObject.scene.IsValid() && KioskMenu.TryGet(m, out _));
            if (main == null || !KioskMenu.TryGet(main, out var x))
            {
                Problem("set-out moved: no kiosk with Contracts");
                return;
            }
            var kiosk = main.screenSwitcher;
            kiosk.SetActiveDisplay(main);
            kiosk.SetActiveDisplay(x.List);
            var row = x.List.Entries.ToList().FindIndex(e => e.Id == RuntimeDesk.JoblessId);
            Line($"set-out moved: the list: {string.Join("; ", x.List.Shown())}");
            if (row < 0 || x.List.Entries[row].OpenWaybills != after.Count)
            {
                Problem("set-out moved: the list has no 'Cars without jobs' row with their count");
                return;
            }
            for (var i = 0; i < row; i++)
                kiosk.HandleInput(InputAction.Down);
            kiosk.HandleInput(InputAction.Confirm);
            var info = kiosk.CurrentScreen as CareerManagerInfoScreen;
            Line($"set-out moved: the row opens {kiosk.CurrentScreen?.GetType().Name}: '{info?.title.text}' / '{info?.paragraph.text.Replace("\n", " | ")}' (overflowing {info?.paragraph.isTextOverflowing})");
            if (info == null || !info.paragraph.text.Contains(car.ID) || info.paragraph.isTextOverflowing)
                Problem("set-out moved: the row does not show the cars without jobs in full");
            kiosk.HandleInput(InputAction.Confirm);
            if (!ReferenceEquals(kiosk.CurrentScreen, x.List))
                Problem($"set-out moved: the info screen went back to {kiosk.CurrentScreen?.GetType().Name}, not the list");
            kiosk.SetActiveDisplay(main);
            return;
        }

        /// <summary>The moved set-out car is still on the map, no longer waiting for a pickup that would never come.</summary>
        private void MovedAfterService(ContractRuntime rt, string guid)
        {
            if (guid == null)
                return;
            var w = rt.State.Of(guid);
            var car = SingletonBehaviour<TrainCarRegistry>.Instance.GetTrainCarByCarGuid(guid);
            var job = car == null ? null : SingletonBehaviour<JobsManager>.Instance.GetJobOfCar(car.logicCar);
            Line($"set-out moved: after the service, {car?.ID ?? "gone"}: {(w == null ? "no bill" : $"{w.Stage}, next {w.Next ?? "-"}")}; job {job?.ID ?? "none"}");
            if (car == null || w == null || w.Stage == Stage.OffMap)
                Problem("set-out moved: the service did not bring the moved car back to idle (or bill it again)");
        }

        /// <summary>The next morning's service: cars waiting on interchange tracks leave; idle cars are billed.</summary>
        private void SecondService(ContractRuntime rt, string contract)
        {
            if (contract == null)
                return;
            var waiting = rt.State.Waybills.Count(w => w.Contract == contract && w.Stage == Stage.OffMap);
            var idle = rt.State.Waybills.Count(w => w.Contract == contract && w.Stage == Stage.Idle);
            var summary = rt.Service(serviceDay + 1);
            Line($"second service ({waiting} waiting to leave, {idle} idle before): {summary}");
            var left = rt.State.Waybills.Count(w => w.Contract == contract && w.Stage == Stage.OffMap);
            if (waiting > 0 && left >= waiting)
                Problem("the cars waiting on the interchange tracks did not leave");
        }

        private void SaveCheck(ContractRuntime rt)
        {
            var data = SaveGameManager.MakeEmptySave();
            rt.State.Write(data);
            var back = ContractState.Read(data, out var unreadable);
            Line($"save: {rt.State.Contracts.Count} contracts, {rt.State.Waybills.Count} waybills written; read back {back?.Contracts.Count ?? -1} and {back?.Waybills.Count ?? -1} (unreadable {unreadable}); guids in save {ContractState.GuidsIn(data).Count}");
            if (back == null || back.Waybills.Count != rt.State.Waybills.Count || back.Contracts.Count != rt.State.Contracts.Count)
                Problem("the saved state did not round-trip");
        }

        /// <summary>
        /// The taken contract's standing: midnight scores its day from its bills; a raise needs
        /// three days averaging over 90%, at the kiosk and again at midnight.
        /// </summary>
        private void StandingCheck(ContractRuntime rt, string id)
        {
            var c = rt.State.Contracts.First(x => x.Id == id);
            var view = KioskMenu.Desk.Mine().First(v => v.Id == id);
            var open = rt.State.Waybills.Count(w => w.Opened.HasValue);
            Line($"standing: {id} level {c.Level}, history [{string.Join(", ", c.History)}], closed today [{string.Join(", ", c.Closed)}], {open} open bill(s)");
            if (c.Closed.Count == 0)
                Problem("the legs closed no bills");
            var blocker = KioskMenu.Desk.RaiseBlocker(view);
            Line($"standing: raise now: {blocker ?? "allowed"}");
            if (blocker == null)
                Problem("a raise was allowed without any history");
            KioskMenu.Desk.RequestLevel(view, c.Level + 1);
            if (c.Requested != c.Level)
                Problem("the desk took a raise without the standing for it");

            // The day ends: it is scored from the bills.
            var level = c.Level;
            rt.State.Day++;
            rt.Midnight();
            Line($"standing: after midnight: level {c.Level}, history [{string.Join(", ", c.History)}], closed [{string.Join(", ", c.Closed)}]");
            if (c.History.Count != 1 || c.Level != level)
                Problem("midnight did not score the day (or changed the level)");

            // Three good days: the raise is offered, asked for, and granted at midnight.
            c.History.Clear();
            c.History.AddRange(new[] { 1.0, 0.95, 0.93 });
            foreach (var w in rt.State.Waybills)
                w.Opened = null;   // nothing open, so the next midnight scores nothing
            view = KioskMenu.Desk.Mine().First(v => v.Id == id);
            blocker = KioskMenu.Desk.RaiseBlocker(view);
            KioskMenu.Desk.RequestLevel(view, c.Level + 1);
            Line($"standing: with [{string.Join(", ", c.History)}]: raise {blocker ?? "allowed"}; requested {c.Requested}; performance shown {view.Performance:0.00}");
            if (blocker != null || c.Requested != level + 1)
                Problem("a raise with three good days was not taken");
            rt.State.Day++;
            rt.Midnight();
            Line($"standing: after midnight: level {c.Level}, history [{string.Join(", ", c.History)}]");
            if (c.Level != level + 1 || c.History.Count != 0)
                Problem("the raise did not take effect at midnight (or kept the old history)");

            // Asked for with good days, but the last day's score pulls the average under 90%.
            c.History.AddRange(new[] { 1.0, 1.0, 1.0 });
            view = KioskMenu.Desk.Mine().First(v => v.Id == id);
            KioskMenu.Desk.RequestLevel(view, c.Level + 1);
            c.Closed.Add(4.6);   // a car that took 4.6 days, delivered today
            rt.State.Day++;
            rt.Midnight();
            Line($"standing: raise asked, then a slow car: level {c.Level}, requested {c.Requested}, history [{string.Join(", ", c.History)}]");
            if (c.Level != level + 1 || c.Requested != c.Level)
                Problem("a raise went through after the average fell under 90%");
        }

        /// <summary>
        /// Pay on top (design D7; ContractTerms, ContractPay), on the taken contract at level 2: a
        /// job of its cars done with fresh bills earns the quick-delivery bonus with its pay;
        /// lowering costs the penalty at midnight; three poor days force two levels down with the
        /// penalty; ending costs it at once, and what the wallet can't cover is owed, kept in the
        /// save, and taken as cash comes in.
        /// </summary>
        private IEnumerator PayCheck(ContractRuntime rt, string id)
        {
            var c = id == null ? null : rt.State.Contracts.FirstOrDefault(x => x.Id == id);
            if (c == null)
            {
                Problem("pay: no taken contract to check");
                yield break;
            }
            if (c.Level < 2)
                c.Level = c.Requested = 2;
            var inventory = SingletonBehaviour<Inventory>.Instance;

            // 1. The quick-delivery bonus, on a job done with fresh bills.
            Job job = null;
            List<TrainCar> jobCars = null;
            string before = null;
            Try("pay: a job to work", () =>
            {
                var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
                var jobs = SingletonBehaviour<JobsManager>.Instance;
                List<TrainCar> Cars() => rt.State.Waybills.Where(w => w.Contract == id).Select(w => registry.GetTrainCarByCarGuid(w.Guid)).Where(t => t != null).ToList();
                Job Pending(List<TrainCar> cars) => cars.Select(t => jobs.GetJobOfCar(t.logicCar))
                    .FirstOrDefault(j => j != null && (j.State == JobState.Available || j.State == JobState.InProgress));
                var all = Cars();
                job = Pending(all);
                if (job == null)
                {
                    Line("pay: no contract job pending; another service: " + rt.Service(serviceDay + 2));
                    all = Cars();
                    job = Pending(all);
                }
                if (job == null)
                {
                    Problem("pay: no contract job to work for the bonus");
                    return;
                }
                jobCars = all.Where(t => jobs.GetJobOfCar(t.logicCar) == job).ToList();
                foreach (var t in jobCars)
                {
                    var w = rt.State.Of(t.logicCar.carGuid);
                    if (w != null)
                        w.Opened = ContractRuntime.Clock;
                }
                before = Prepare(rt, job, jobCars);
            });
            if (job != null && before != null)
            {
                yield return new WaitForSeconds(0.5f);
                Try("pay: the bonus", () =>
                {
                    var basePay = (double)job.GetBasePaymentForTheJob();
                    var count = ContractRuntime.JobCars(job).Count(x => x != null);
                    var open = jobCars.Count(t => rt.State.Of(t.logicCar.carGuid)?.Opened != null);
                    var expected = count == 0 ? 0.0 : open * ContractTerms.Bonus(basePay / count, c.Level, 0.0);
                    Line(Complete(rt, job, jobCars, before));
                    var stock = job.GetJobCompletionTime() <= job.TimeLimit + 60f ? job.GetPotentialBonusPaymentForTheJob() : 0f;
                    var paid = job.GetBonusPaymentForTheJob() - stock;
                    var wage = job.GetWageForTheJob();
                    Line($"pay: {job.ID} at level {c.Level}, {count} car(s), {open} with fresh bills: base ${basePay:N0}, time bonus ${stock:N0}, " +
                         $"quick-delivery bonus ${paid:N0} (expected ${expected:N0}); wage ${wage:N0}");
                    if (expected < 1.0)
                        Problem("pay: the job had no bonus to earn");
                    else if (Math.Abs(paid - expected) > 1.5)
                        Problem("pay: the quick-delivery bonus paid is not the level's share of the job's base wage");
                    if (Math.Abs(wage - (basePay + stock + paid)) > 1.5)
                        Problem("pay: the job's wage does not carry the bonus");
                });
            }

            // 2. Lowering costs the penalty when it takes effect at midnight.
            Try("pay: lowering", () =>
            {
                foreach (var w in rt.State.Waybills)
                    w.Opened = null;
                c.Closed.Clear();   // nothing to score, so the history stays as it is
                var view = KioskMenu.Desk.Mine().First(v => v.Id == id);
                var from = c.Level;
                var days = c.History.Count;
                var quoted = KioskMenu.Desk.Penalty(view, from - 1);
                var expected = ContractTerms.Penalty(from, from - 1, c.History.Count);
                inventory.SetMoney(1_000_000);
                KioskMenu.TakeMessage();
                KioskMenu.Desk.RequestLevel(view, from - 1);
                rt.State.Day++;
                rt.Midnight();
                var charged = 1_000_000 - inventory.PlayerMoney;
                var told = KioskMenu.TakeMessage();
                Line($"pay: lowered {id} from level {from} with {days} day(s) of history: now {c.Level}; quoted ${quoted:N0}, charged ${charged:N0} " +
                     $"(expected ${expected:N0}); owed ${rt.State.Owed:N0}; the kiosk tells '{told}'");
                if (c.Level != from - 1 || Math.Abs(charged - expected) > 0.5 || Math.Abs(quoted - expected) > 0.5 || rt.State.Owed > 0.5)
                    Problem("pay: lowering did not cost the quoted penalty at midnight");
                if (told == null || !told.Contains($"{id} L{from}>{from - 1}"))
                    Problem("pay: the kiosk was not told of the drop");
            });

            // 3. Three poor days force the contract two levels down, with the penalty.
            Try("pay: a forced drop", () =>
            {
                c.Level = c.Requested = 3;
                c.History.Clear();
                c.History.AddRange(new[] { 0.2, 0.3, 0.4 });
                c.Closed.Clear();
                foreach (var w in rt.State.Waybills)
                    w.Opened = null;
                var expected = ContractTerms.Penalty(3, 1, 3);
                inventory.SetMoney(1_000_000);
                KioskMenu.TakeMessage();
                rt.State.Day++;
                rt.Midnight();
                var charged = 1_000_000 - inventory.PlayerMoney;
                var told = KioskMenu.TakeMessage();
                Line($"pay: three poor days at level 3: now {c.Level}; charged ${charged:N0} (expected ${expected:N0}); the kiosk tells '{told}'");
                if (c.Level != 1 || Math.Abs(charged - expected) > 0.5)
                    Problem("pay: three poor days did not force two levels down with the penalty");
            });

            // 4. Ending costs the penalty at once; what the wallet can't cover is owed.
            var owed = 0.0;
            Try("pay: ending", () =>
            {
                var view = KioskMenu.Desk.Mine().First(v => v.Id == id);
                var (level, days) = (c.Level, c.History.Count);
                var expected = ContractTerms.Penalty(level, 0, days);
                var quoted = KioskMenu.Desk.Penalty(view, 0);
                inventory.SetMoney(1_000);
                KioskMenu.Desk.End(view);
                owed = rt.State.Owed;
                var data = SaveGameManager.MakeEmptySave();
                rt.State.Write(data);
                var back = ContractState.Read(data, out _);
                Line($"pay: ended {id} at level {level} with {days} day(s) of history: quoted ${quoted:N0} (expected ${expected:N0}); " +
                     $"wallet ${inventory.PlayerMoney:N0}, owed ${owed:N0} (the desk shows ${KioskMenu.Desk.Owed:N0}); the save reads back owed ${back?.Owed ?? -1:N0}");
                if (rt.State.Contracts.Any(x => x.Id == id) || Math.Abs(quoted - expected) > 0.5 || inventory.PlayerMoney > 0.5
                    || Math.Abs(owed - (expected - 1_000)) > 0.5 || back == null || Math.Abs(back.Owed - owed) > 0.5)
                    Problem("pay: ending did not take the wallet and owe the rest of the penalty");
            });
            if (owed > 0.5)
            {
                inventory.AddMoney(owed + 5_000);
                yield return new WaitForSeconds(5f);
                Try("pay: owed taken", () =>
                {
                    Line($"pay: after ${owed + 5_000:N0} came in: wallet ${inventory.PlayerMoney:N0}, owed ${rt.State.Owed:N0}");
                    if (rt.State.Owed > 0.5 || Math.Abs(inventory.PlayerMoney - 5_000) > 0.5)
                        Problem("pay: what was owed was not taken as cash came in");
                });
            }
        }

        /// <summary>
        /// The kiosk under load (a Discord report, 2026-11-03: "huge lag spikes (around 2 s) when
        /// selecting contracts", on a new save, taking every contract): every license given;
        /// standing at the West Interchange office's kiosk, the frames watched before any kiosk
        /// use; five contracts taken through the kiosk with every offer listed, each step timed;
        /// the rest taken, and midnight timed; the 06:00 service run as the game runs it (begun,
        /// then a step a frame), its work and the frames meanwhile and after it measured; then each
        /// kiosk action timed with every contract held, and the frames watched with the list open
        /// while the cursor moves. A kiosk action or a service step over 100 ms, or a frame over
        /// 200 ms, is a problem.
        /// </summary>
        private IEnumerator KioskLoad(ContractRuntime rt)
        {
            var desk = KioskMenu.Desk;
            var mains = Resources.FindObjectsOfTypeAll<CareerManagerMainScreen>().Where(m => m.gameObject.scene.IsValid() && KioskMenu.TryGet(m, out _)).ToList();
            var main = mains.FirstOrDefault(m => KioskPlaces.Of(m.screenSwitcher)?.IsInterchange == true) ?? mains.FirstOrDefault();
            if (desk == null || main == null || !KioskMenu.TryGet(main, out var x))
            {
                Problem("kiosk load: no kiosk with the Contracts menu");
                yield break;
            }
            var place = KioskPlaces.Of(main.screenSwitcher);
            var kiosk = main.screenSwitcher;
            Try("kiosk load: every license", () =>
            {
                var licenses = SingletonBehaviour<LicenseManager>.Instance;
                foreach (var l in Globals.G.Types.jobLicenses.Where(l => l != null && !licenses.IsJobLicenseAcquired(l)).ToList())
                    licenses.AcquireJobLicense(l);
                Line($"kiosk load: {desk.Offers(place).Count} contracts on offer at {place?.Id}, {rt.State.Contracts.Count} held");
            });

            // Stand at the kiosk, as a player does.
            var facing = kiosk.transform.TransformDirection(Vector3.left);
            facing.y = 0f;
            facing.Normalize();
            var screen = kiosk.transform.TransformPoint(new Vector3(-0.10f, 1.32f, -0.08f));
            var stand = screen + facing * 0.8f;
            stand.y = kiosk.transform.position.y + 0.15f;
            PlayerManager.TeleportPlayer(stand, Quaternion.LookRotation(-facing, Vector3.up), null, true);
            yield return new WaitForSeconds(10f);

            var watch = new System.Diagnostics.Stopwatch();
            double Ms(Action act)
            {
                watch.Restart();
                act();
                watch.Stop();
                return watch.Elapsed.TotalMilliseconds;
            }
            var slow = new List<string>();
            void Timed(string what, double ms, double limit = 100.0)
            {
                Line($"kiosk load: {what}: {ms:F1} ms");
                if (ms > limit)
                    slow.Add($"{what} {ms:F0} ms");
            }
            IEnumerator Frames(string what, float seconds, bool pressing)
            {
                var start = UnityEngine.Time.realtimeSinceStartup;
                var next = start;
                float worst = 0f;
                var spikes = new List<string>();
                var frames = 0;
                while (UnityEngine.Time.realtimeSinceStartup - start < seconds)
                {
                    if (pressing && UnityEngine.Time.realtimeSinceStartup >= next)
                    {
                        kiosk.HandleInput(InputAction.Down);
                        next += 0.5f;
                    }
                    yield return null;
                    frames++;
                    var dt = UnityEngine.Time.unscaledDeltaTime;
                    worst = Mathf.Max(worst, dt);
                    if (dt > 0.2f)
                        spikes.Add($"{dt * 1000f:F0} ms at {UnityEngine.Time.realtimeSinceStartup - start:F1} s");
                }
                Line($"kiosk load: frames {what}, {seconds:F0} s: {frames} frames, worst {worst * 1000f:F0} ms; over 200 ms: " +
                     (spikes.Count == 0 ? "none" : string.Join(", ", spikes)));
                if (spikes.Count > 0)
                    slow.Add($"{spikes.Count} frame(s) over 200 ms {what}");
            }

            yield return Frames("standing at the kiosk, before using it", 15f, false);

            // Taking contracts as a player does, with every offer still listed: open an offer,
            // confirm its first row (take at level 1), confirm on the confirm screen, back on the list.
            kiosk.SetActiveDisplay(main);
            yield return null;
            Timed("opening the Contracts list, every offer listed", Ms(() => kiosk.SetActiveDisplay(x.List)));
            yield return null;
            for (var n = 0; n < 5; n++)
            {
                var offer = x.List.Entries.FirstOrDefault(e => e.IsOffer && e.Needs == null);
                if (offer == null)
                    break;
                Timed($"opening offer {n + 1} ({offer.Short})", Ms(() =>
                {
                    x.Detail.Show(offer);
                    kiosk.SetActiveDisplay(x.Detail);
                }));
                yield return null;
                Timed($"choosing take at level 1 ({offer.Short})", Ms(() => kiosk.HandleInput(InputAction.Confirm)));
                yield return null;
                Timed($"confirming the take ({offer.Short})", Ms(() => kiosk.HandleInput(InputAction.Confirm)));
                yield return null;
                if (!rt.State.Contracts.Any(c => c.Id == offer.Id) || !ReferenceEquals(kiosk.CurrentScreen, x.List))
                    Problem($"kiosk load: taking {offer.Id} through the kiosk did not take it and go back to the list");
            }
            yield return Frames("after taking contracts through the kiosk", 5f, false);
            List<string> atOnce = null;
            Try("kiosk load: every contract", () =>
            {
                foreach (var offer in desk.Offers(place).ToList())
                    desk.Take(offer, 1);
                rt.State.Day++;
                Timed("midnight with every contract newly taken", Ms(() => rt.Midnight()));
                // The 06:00 service as the game runs it: begun now, then a step a frame.
                Timed("the 06:00 service begun (the sweep, the plan, the bills)", Ms(() => rt.BeginService(serviceDay + 3)));
                // The check below, in the same frame, before the first step (it is in the first frame's time, not the service's).
                Line($"kiosk load: the cuts at once worked out for the check: {Ms(() => atOnce = rt.CutsAtOnce()):F1} ms");
            });
            {
                var start = UnityEngine.Time.realtimeSinceStartup;
                var frames = 0;
                float worst = 0f;
                var spikes = new List<string>();
                var all = new List<float>();
                while (rt.Servicing && UnityEngine.Time.realtimeSinceStartup - start < 60f)
                {
                    yield return null;
                    frames++;
                    var dt = UnityEngine.Time.unscaledDeltaTime;
                    all.Add(dt);
                    worst = Mathf.Max(worst, dt);
                    if (dt > 0.2f)
                        spikes.Add($"{dt * 1000f:F0} ms at frame {frames}");
                }
                // A few frames after it, where the last steps' cars and jobs settle.
                for (var i = 0; i < 5; i++)
                {
                    yield return null;
                    all.Add(UnityEngine.Time.unscaledDeltaTime);
                }
                var done = rt.LastService;
                if (rt.Servicing || done == null || done.Day != serviceDay + 3)
                    Problem("kiosk load: the 06:00 service did not finish within a minute");
                else
                {
                    Line($"kiosk load: service: {done.Summary}");
                    Line($"kiosk load: the service's work: {done.WorkMs:F1} ms in {done.Work.Run} step(s) after the plan, the longest {done.LongestStepMs:F1} ms; " +
                         $"{frames} frame(s), {UnityEngine.Time.realtimeSinceStartup - start:F2} s; worst frame {worst * 1000f:F0} ms; over 200 ms: " +
                         (spikes.Count == 0 ? "none" : string.Join(", ", spikes)));
                    Line("kiosk load: the service's longest steps: " + string.Join("; ", done.Steps.Select((x, i) => (x.What, x.Ms, i))
                        .OrderByDescending(x => x.Ms).Take(6).Select(x => $"step {x.i + 1} {x.Ms:F1} ms, {x.What ?? "-"}")));
                    Line("kiosk load: the longest frames (the first is the one the probe paused in): " + string.Join("; ", all.Select((dt, i) => (dt, i))
                        .OrderByDescending(x => x.dt).Take(6).Select(x => $"frame {x.i + 1} {x.dt * 1000f:F0} ms")));
                    SameCuts("kiosk load", atOnce, done);
                    if (done.LongestStepMs > 100.0)
                        slow.Add($"the service's longest step {done.LongestStepMs:F0} ms");
                    if (spikes.Count > 0)
                        slow.Add($"{spikes.Count} frame(s) over 200 ms while the service placed its cars");
                }
            }
            Try("kiosk load: every contract running", () =>
            {
                Line($"kiosk load: {rt.State.Contracts.Count} contracts held, {rt.State.Waybills.Count} waybills, " +
                     $"{SingletonBehaviour<JobsManager>.Instance.currentJobs.Count} jobs taken");
                rt.State.Day++;
                Timed("midnight with every contract running", Ms(() => rt.Midnight()));
            });
            yield return Frames("after the service", 15f, false);
            for (var i = 0; i < 3; i++)
            {
                Timed($"the desk's own contracts (pass {i + 1})", Ms(() => desk.Mine()));
                Timed($"the desk's offers (pass {i + 1})", Ms(() => desk.Offers(place)));
                Timed($"the passenger cities (pass {i + 1})", Ms(() => Passengers.Cities()));
                Timed($"the licenses held (pass {i + 1})", Ms(() => LicenseRules.HeldIds()));
                yield return null;
            }
            kiosk.SetActiveDisplay(main);
            yield return null;
            Timed("opening the Contracts list", Ms(() => kiosk.SetActiveDisplay(x.List)));
            yield return null;
            for (var i = 0; i < 5; i++)
            {
                Timed($"cursor down {i + 1}", Ms(() => kiosk.HandleInput(InputAction.Down)));
                yield return null;
            }
            var kinds = new (string Name, Func<ContractView, bool> Pick)[]
            {
                ("an industry contract of yours", e => !e.IsOffer && Runtime.RuntimeDesk.IsContract(e.Id) && !Runtime.RuntimeDesk.IsPassenger(e.Id) && e.Id != Economy.BridgeId),
                ("a passenger contract of yours", e => !e.IsOffer && Runtime.RuntimeDesk.IsPassenger(e.Id)),
                ("the bridge contract", e => e.Id == Economy.BridgeId),
                ("the locomotive shop", e => e.Id == Runtime.RuntimeDesk.ShopId),
                ("an offer", e => e.IsOffer),
            };
            foreach (var (name, pick) in kinds)
            {
                var entry = x.List.Entries.FirstOrDefault(pick);
                if (entry == null)
                {
                    Line($"kiosk load: not in the list: {name}");
                    continue;
                }
                Timed($"opening {name} ({entry.Short})", Ms(() =>
                {
                    x.Detail.Show(entry);
                    kiosk.SetActiveDisplay(x.Detail);
                }));
                yield return null;
                Timed($"back from {name} to the list", Ms(() => kiosk.HandleInput(InputAction.Cancel)));
                yield return null;
            }
            yield return Frames("with the list open, the cursor moving", 15f, true);
            kiosk.SetActiveDisplay(main);
            Line($"kiosk load: {(slow.Count == 0 ? "nothing slow" : "slow: " + string.Join("; ", slow))}");
            if (slow.Count > 0)
                Problem("kiosk load: " + string.Join("; ", slow));
        }

        private IEnumerator Finish()
        {
            foreach (var e in errors.Distinct().Take(80))
                Line("log: " + e);
            Line($"PROBE DONE: {problems} problem(s)");
            log?.Close();
            if (quitAfter)
            {
                yield return new WaitForSeconds(2f);
                Application.Quit();
            }
        }
    }
}
