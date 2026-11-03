using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Mod.Kiosk;
using DV;
using DV.InventorySystem;
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
            Try("second service", () => SecondService(rt, taken));
            yield return new WaitForSeconds(2f);
            Try("jobs after the second service", () => JobsCheck(rt, taken));
            Try("standing", () => StandingCheck(rt, taken));
            yield return PayCheck(rt, taken);
            Try("save round trip", () => SaveCheck(rt));
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
                var before = SingletonBehaviour<CarSpawner>.Instance.AllCars.Count;
                var message = LocoShop.Buy(item, place);
                var after = SingletonBehaviour<CarSpawner>.Instance.AllCars.Count;
                Line($"shop: bought {item.Name} at {place.Id}: {message}; money {money:N0} -> {inventory.PlayerMoney:N0}; cars +{after - before}");
                if (Math.Abs(money - item.Price - inventory.PlayerMoney) > 1.0 || after - before != item.Cars.Count)
                    Problem($"{item.Name} was not paid for or not delivered whole");
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
            var summary = rt.Service(day);
            Line("service: " + summary);
            var placed = rt.State.Waybills.Where(w => w.Contract == industry.Id).ToList();
            Line($"service: {placed.Count} waybill(s) for {industry.Id}: " +
                 string.Join("; ", placed.GroupBy(w => (w.Stage, w.Cargo, w.Next, w.Then, w.At)).Select(g => $"{g.Count()} x {g.Key.Stage} {g.Key.Cargo ?? "empty"} at {g.Key.At} -> {g.Key.Next}{(g.Key.Then != null ? " then " + g.Key.Then : "")}")));
            if (placed.Count == 0)
                Problem("the service placed no cars");
            return industry.Id;
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
            var stuck = rt.State.Waybills.Count(w => w.Contract == contract && w.Stage != Stage.Idle && w.Stage != Stage.OffMap);
            if (stuck > 0)
                Problem($"{stuck} car(s) are still between legs with no job worked to the end");
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

        // Where the next moved car goes on each track (span), so moved cars line up and never overlap.
        private readonly Dictionary<RailTrack, double> nextSpan = new Dictionary<RailTrack, double>();

        /// <summary>Moves cars onto a track one after another, from 40 % of its length, behind any moved there before.</summary>
        private void MoveOnto(List<TrainCar> cars, RailTrack rail)
        {
            var points = rail.GetKinkedPointSet().points;
            var total = points[points.Length - 1].span;
            if (!nextSpan.TryGetValue(rail, out var span))
                span = total * 0.4;
            foreach (var car in cars)
            {
                var at = span + car.InterCouplerDistance / 2.0;
                var i = 0;
                while (i < points.Length - 1 && points[i].span < at)
                    i++;
                var p = points[i];
                car.MoveToTrackWithCarUncouple(rail, (Vector3)p.position + DV.OriginShift.OriginShift.currentMove, (Vector3)p.forward);
                span += car.InterCouplerDistance + 2.0;
            }
            nextSpan[rail] = span;
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
