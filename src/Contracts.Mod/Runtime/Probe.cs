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
using DV.Booklets;
using DV.Localization;
using DV.Logic.Job;
using DV.RenderTextureSystem.BookletRender;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using HarmonyLib;
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
    /// - each move within a station (StationMoves): its shunting code, and its overview, booklet
    ///   and reports saying Shunting;
    /// - the contract's standing: the day scored at midnight from its bills, a raise refused
    ///   without three good days and granted with them, a raise asked for refused at midnight
    ///   when the day's score pulls the average down;
    /// - the saved state's round trip;
    /// - the contract's cars at the kiosk: their counts against the cars, the list's order, the
    ///   contract's page and its Cars page, and that their lines fit (views, probe-cars-*.png);
    /// - errors logged meanwhile. Then the game quits, if probe.txt says "quit".
    /// </summary>
    internal sealed class Probe : MonoBehaviour
    {
        private static string folder;
        private static bool quitAfter;
        private static bool saveCheck;
        private StreamWriter log;
        private int problems;
        private int movesChecked;
        private int interchangePapersChecked;
        private static readonly List<string> errors = new List<string>();

        public static void Install(string modFolder)
        {
            folder = modFolder;
            var trigger = Path.Combine(folder, "probe.txt");
            if (!File.Exists(trigger))
                return;
            quitAfter = File.ReadAllText(trigger).IndexOf("quit", StringComparison.OrdinalIgnoreCase) >= 0;
            saveCheck = File.ReadAllText(trigger).IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0;
            UseLiteNetLib();
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
                probe.StartCoroutine(saveCheck ? probe.SaveRun() : probe.Run());
            };
            Main.Log("probe requested");
        }

        // ------------------------------------------------------------ Multiplayer

        private static System.Reflection.ConstructorInfo steamTransport, liteTransport;
        private static bool multiplayerLoaded;

        /// <summary>
        /// The Multiplayer mod, if it runs here, connects even a single-player game through Steam's
        /// networking, which can't reach Steam in the clone: the local connection ends
        /// "Misc_SteamConnectivity" at once, and the game goes back to the main menu, again and
        /// again (results/mp-diag1, 2026-11-09). Its managers get the mod's own LiteNetLib transport
        /// instead, which needs no Steam. Probe runs only; the host's side of the mod is otherwise
        /// as it is.
        /// </summary>
        private static void UseLiteNetLib()
        {
            try
            {
                var manager = AccessTools.TypeByName("Multiplayer.Networking.Managers.NetworkManager");
                if (manager == null)
                    return;
                multiplayerLoaded = true;
                steamTransport = AccessTools.Constructor(AccessTools.TypeByName("Multiplayer.Networking.TransportLayers.SteamWorksTransport"), Type.EmptyTypes);
                liteTransport = AccessTools.Constructor(AccessTools.TypeByName("Multiplayer.Networking.TransportLayers.LiteNetLibTransport"), Type.EmptyTypes);
                var made = AccessTools.GetDeclaredConstructors(manager).FirstOrDefault(c => !c.IsStatic);
                if (steamTransport == null || liteTransport == null || made == null)
                {
                    Main.Warn("probe: Multiplayer's transports are not where they were; its runs need Steam's networking");
                    return;
                }
                new HarmonyLib.Harmony("Contracts.probe").Patch(made, transpiler: new HarmonyLib.HarmonyMethod(typeof(Probe), nameof(SwapTransport)));
                Main.Log("probe: Multiplayer runs on its LiteNetLib transport in this run");
            }
            catch (Exception e)
            {
                Main.Error($"probe: switching Multiplayer's transport failed: {e.Message}");
            }
        }

        private static IEnumerable<HarmonyLib.CodeInstruction> SwapTransport(IEnumerable<HarmonyLib.CodeInstruction> code)
        {
            foreach (var i in code)
            {
                if (i.opcode == System.Reflection.Emit.OpCodes.Newobj && Equals(i.operand, steamTransport))
                    i.operand = liteTransport;
                yield return i;
            }
        }

        /// <summary>
        /// Every interchange track is found by the name a stock track with its ID would have
        /// ("[Y]_[WI]_[A-01-X]"), as the Multiplayer mod's players look a job's tracks up
        /// (Interchange Yards 0.1.7; Blaze221, Nexus, 2026-11-08: no booklets for the players).
        /// </summary>
        private void TrackNamesCheck()
        {
            var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
            var tracks = InterchangeYards.All().SelectMany(kv => kv.Value).ToList();
            var missing = tracks.Where(t => registry.GetTrackWithName(t.ID.RailTrackGameObjectID)?.LogicTrack() != t).Select(t => t.ID.FullID).ToList();
            Line($"track names: {tracks.Count - missing.Count} of {tracks.Count} interchange tracks found by their stock names" +
                 (tracks.Count > 0 ? $", e.g. {tracks[0].ID.RailTrackGameObjectID}" : ""));
            if (tracks.Count == 0 || missing.Count > 0)
                Problem($"track names: not found by their stock names: {string.Join(", ", missing)}");
        }

        /// <summary>
        /// With the Multiplayer mod, on the host (the clone's game is one): its transport, its own
        /// station locomotives blocked (spawning one on an empty spawn track leaves it empty), and
        /// the job validator it pairs with each station, the interchanges' among them (it pairs a
        /// validator whose parent is X with the station under "X_office_anchor"; a player other
        /// than the host takes and hands in jobs through it).
        /// </summary>
        private IEnumerator MultiplayerCheck()
        {
            if (!multiplayerLoaded)
            {
                Line("multiplayer: not loaded; its checks skipped");
                yield break;
            }
            var lifecycleType = AccessTools.TypeByName("Multiplayer.Components.Networking.NetworkLifecycle");
            var lifecycle = lifecycleType == null ? null : FindObjectOfType(lifecycleType);
            var server = lifecycle == null ? null : HarmonyLib.Traverse.Create(lifecycle).Property("Server").GetValue();
            var transport = server == null ? null : HarmonyLib.Traverse.Create(server).Field("transport").GetValue();
            Line($"multiplayer: host {(server != null ? "running" : "not running")}, transport {transport?.GetType().Name ?? "?"}");
            if (server == null)
                Problem("multiplayer: no host running");

            // B. Its own station locomotives.
            Line($"multiplayer: its station locomotives blocked: {CareerRules.MultiplayerLocomotivesBlocked}");
            if (!CareerRules.MultiplayerLocomotivesBlocked)
                Problem("multiplayer: its station locomotives are not blocked");
            var spawnType = AccessTools.TypeByName("Multiplayer.Patches.World.StationLocoSpawner_Start_Patch");
            var spawn = spawnType == null ? null : AccessTools.Method(spawnType, "SpawnLocomotives");
            int Locos(Track t) => t?.GetCarsFullyOnTrack().Count(c => CarTypes.IsLocomotive(c.carType)) ?? -1;
            var spawners = FindObjectsOfType<StationLocoSpawner>()
                .Select(sp => (Spawner: sp, Track: HarmonyLib.Traverse.Create(sp).Field("locoSpawnTrack").GetValue<RailTrack>()?.LogicTrack()))
                .Where(x => x.Track != null).ToList();
            var empty = spawners.Where(x => Locos(x.Track) == 0).ToList();
            Line($"multiplayer: {spawners.Count} station locomotive spawners, {empty.Count} with an empty spawn track");
            if (spawn == null || empty.Count == 0)
                Problem("multiplayer: no spawning of its own, or no empty spawn track, to try");
            else
            {
                var carsBefore = SingletonBehaviour<CarSpawner>.Instance.AllCars.Count;
                foreach (var x in empty.Take(3))
                    spawn.Invoke(null, new object[] { x.Spawner });
                yield return new WaitForSeconds(3f);
                var after = empty.Take(3).Select(x => $"{x.Track.ID.FullDisplayID} {Locos(x.Track)}").ToList();
                var carsAfter = SingletonBehaviour<CarSpawner>.Instance.AllCars.Count;
                Line($"multiplayer: its spawning run on {string.Join(", ", after)} locomotive(s); cars {carsBefore} before, {carsAfter} after");
                if (empty.Take(3).Any(x => Locos(x.Track) != 0) || carsAfter != carsBefore)
                    Problem("multiplayer: its station spawning made a locomotive");
            }

            // C. The validators it pairs with the stations.
            var nscType = AccessTools.TypeByName("Multiplayer.Components.Networking.World.NetworkedStationController");
            var fromStation = nscType == null ? null : AccessTools.Method(nscType, "GetFromStationController");
            if (fromStation == null)
            {
                Problem("multiplayer: its networked stations are not where they were");
                yield break;
            }
            JobValidator ValidatorOf(StationController sc)
            {
                var args = new object[] { sc, null };
                return (bool)fromStation.Invoke(null, args) && args[1] != null ? HarmonyLib.Traverse.Create(args[1]).Field("JobValidator").GetValue<JobValidator>() : null;
            }
            var stations = StationController.allStations.Where(sc => sc != null && sc.logicStation != null).ToList();
            var paired = stations.Where(sc => !InterchangeStations.Is(sc) && ValidatorOf(sc) != null).Select(sc => sc.stationInfo.YardID).ToList();
            var stock = stations.Where(sc => !InterchangeStations.Is(sc)).ToList();
            Line($"multiplayer: stock stations with a validator: {paired.Count} of {stock.Count}");
            var example = stock.FirstOrDefault(sc => ValidatorOf(sc) != null);
            if (example != null)
                Line($"multiplayer: e.g. {example.stationInfo.YardID}: station under '{example.transform.parent?.name}', validator under '{ValidatorOf(example).transform.parent?.name}'");
            foreach (var sc in stations.Where(InterchangeStations.Is))
            {
                // The office is the object under Offices_w3 (its station's anchor's name starts as the office's).
                var office = sc.transform;
                while (office != null && office.parent != null && office.parent.name != "Offices_w3")
                    office = office.parent;
                if (office != null && !office.name.StartsWith("[interchange office] ", StringComparison.Ordinal))
                    office = null;
                var own = office == null ? new JobValidator[0] : office.GetComponentsInChildren<JobValidator>(true);
                var mine = ValidatorOf(sc);
                Line($"multiplayer: {sc.stationInfo.YardID}: station under '{sc.transform.parent?.name}', office validators under " +
                     $"{string.Join(", ", own.Select(v => $"'{v.transform.parent?.name}'"))}; paired with {(mine == null ? "none" : own.Contains(mine) ? "its office's" : "another's")}");
                if (mine == null || !own.Contains(mine))
                    Problem($"multiplayer: {sc.stationInfo.YardID}'s office validator is not paired with it");
            }
        }

        /// <summary>
        /// Each job's tasks made again from the Multiplayer mod's own data for them, as a player
        /// other than the host makes them (TaskNetworkDataFactory, then ToTask): they must build,
        /// on the same tracks.
        /// </summary>
        private void MultiplayerJobsCheck()
        {
            if (!multiplayerLoaded)
                return;
            var factory = AccessTools.TypeByName("Multiplayer.Networking.Data.Jobs.TaskNetworkDataFactory");
            var convert = factory == null ? null : AccessTools.Method(factory, "ConvertTask", new[] { typeof(Task) });
            if (convert == null)
            {
                Problem("multiplayer jobs: its task data is not where it was");
                return;
            }
            string Tracks(TaskData d) => d == null ? "" : d.nestedTasks != null && d.nestedTasks.Count > 0
                ? string.Join(";", d.nestedTasks.Select(n => Tracks(n.GetTaskData())))
                : $"{d.startTrack?.ID?.FullID}>{d.destinationTrack?.ID?.FullID}";
            var jobs = StationController.allStations.Where(sc => sc?.logicStation != null).SelectMany(sc => sc.logicStation.availableJobs).Take(60).ToList();
            var built = 0;
            var failed = new List<string>();
            foreach (var job in jobs)
            {
                try
                {
                    foreach (var task in job.tasks)
                    {
                        var data = convert.Invoke(null, new object[] { task });
                        var toTask = AccessTools.Method(data.GetType(), "ToTask");
                        var args = new object[] { new Dictionary<ushort, Task>() };
                        var again = (Task)toTask.Invoke(data, args);
                        if (Tracks(again.GetTaskData()) != Tracks(task.GetTaskData()))
                            throw new Exception($"tracks {Tracks(again.GetTaskData())}, not {Tracks(task.GetTaskData())}");
                    }
                    built++;
                }
                catch (Exception e)
                {
                    var why = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                    failed.Add($"{job.ID} ({why.GetType().Name}: {why.Message})");
                }
            }
            Line($"multiplayer jobs: {built} of {jobs.Count} job(s) built again from Multiplayer's data, on the same tracks" +
                 (failed.Count > 0 ? $"; not: {string.Join("; ", failed.Take(8))}" : ""));
            if (jobs.Count == 0 || failed.Count > 0)
                Problem($"multiplayer jobs: {failed.Count} of {jobs.Count} job(s) don't build as a player other than the host builds them");
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
            Try("industry spots", () => SpotsCheck(rt));
            Try("interchange stations", () => StationsCheck());
            Try("interchange tracks by their stock names", TrackNamesCheck);
            yield return MultiplayerCheck();
            Try("starter", StarterCheck);
            yield return null;
            Try("shop", ShopCheck);
            yield return null;
            yield return SaleCheck();
            Try("the career's first contract", () => FirstContractCheck(rt));
            // The checks from here take contracts the old way (at midnight) and run their services themselves.
            RuntimeDesk.OpensAtOnce = false;
            string taken = null;
            Try("contract and service", () => taken = ServiceCheck(rt));
            yield return new WaitForSeconds(3f);
            Try("jobs", () => JobsCheck(rt, taken));
            Try("jobs as Multiplayer's players build them", MultiplayerJobsCheck);
            yield return BookletsCheck(taken);
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
            if (taken != null && movesChecked == 0)
                Problem("papers: the legs had no move within a station to check");
            if (taken != null && interchangePapersChecked == 0)
                Problem("papers: the legs had no job to or from an interchange to check");
            string moved = null;
            Try("a set-out car moved off the interchange", () => moved = MoveSetOut(rt, taken));
            yield return new WaitForSeconds(1f);
            Try("the desk after the set-out car moved", () => SetOutDesk(rt, moved));
            yield return PagingCheck();
            Try("second service", () => SecondService(rt, taken));
            Try("the moved car after the second service", () => MovedAfterService(rt, moved));
            yield return new WaitForSeconds(2f);
            Try("jobs after the second service", () => JobsCheck(rt, taken));
            Try("standing", () => StandingCheck(rt, taken));
            Try("the day's roll and the hold", () => RollCheck(rt, taken));
            Try("the kiosk of a player who joined", () => RemoteDeskCheck(rt, taken));
            // While the contract is still held: the pay check lowers and ends it.
            yield return CarsCheck(rt, taken);
            yield return PayCheck(rt, taken);
            yield return SuspensionCheck(rt, taken);
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
        /// <summary>
        /// The interchange offices' booklets come out for any player within range, not only the
        /// local one (Blaze221, Nexus, 2026-11-05: contracts "spawn only for the host" in
        /// Multiplayer). The range check counts the local player and, standing in for a
        /// Multiplayer client, another player the probe places; single player has no Multiplayer
        /// players. Then, at an office no player is near (the player is at MF), the waiting
        /// jobs' booklets stay off its table until the stand-in walks up, and are then all laid
        /// out on it.
        /// </summary>
        private IEnumerator BookletsCheck(string contract)
        {
            if (contract == null)
                yield break;
            var wrong = new List<string>();
            var here = PlayerManager.PlayerTransform.position;
            var away = here + new Vector3(0f, 0f, 2000f);
            if (!InterchangeBooklets.AnyPlayerNear(here))
                wrong.Add("the local player does not count");
            if (InterchangeBooklets.AnyPlayerNear(away))
                wrong.Add("a point 2 km from the player counts");
            var mp = Kiosk.Multiplayer.PlayerPositions();
            if (mp.Count != 0)
                wrong.Add($"{mp.Count} Multiplayer player(s) in single player");
            InterchangeBooklets.StandIns.Add(away);
            if (!InterchangeBooklets.AnyPlayerNear(away + new Vector3(100f, 0f, 0f)))
                wrong.Add("another player 100 m off does not count");
            InterchangeBooklets.StandIns.Clear();

            // An interchange office no player is near, with jobs waiting there of the station beside
            // it (written before the interchanges were stations of their own; the interchange
            // stations lay out their own jobs' booklets, for any player under Multiplayer).
            string office = null;
            PointOnPlane table = null;
            StationController station = null;
            List<Job> waiting = null;
            HashSet<Job> processed = null;
            foreach (var interchange in InterchangeYards.All().Keys)
            {
                var t = InterchangeBooklets.Table(interchange);
                var sc = Economy.Station(InterchangeYards.NeighborOf(interchange));
                var done = sc == null ? null : HarmonyLib.Traverse.Create(sc).Field("processedNewJobs").GetValue<HashSet<Job>>();
                if (t == null || done == null || InterchangeBooklets.AnyPlayerNear(t.transform.position))
                    continue;
                var jobs = sc.logicStation.availableJobs.Where(j => InterchangeBooklets.InterchangeOf(j) == interchange && !done.Contains(j)).ToList();
                if (jobs.Count == 0)
                    continue;
                (office, table, station, waiting, processed) = (interchange, t, sc, jobs, done);
                break;
            }
            if (office == null)
            {
                var own = InterchangeYards.All().Keys.All(i => InterchangeStations.Is(Economy.Station(InterchangeYards.StationOf(i))));
                if (own)
                    Line($"booklets: the range rule checked (single player: {mp.Count} Multiplayer player(s)); no jobs of the stations beside the interchanges start there, " +
                         "and the interchange stations lay out their own");
                else
                    Problem("booklets: no interchange office without a player near and with jobs waiting");
                if (wrong.Count > 0)
                    Problem($"booklets: {string.Join("; ", wrong)}");
                yield break;
            }
            yield return new WaitForSeconds(1.5f);
            var early = waiting.Count(processed.Contains);
            if (early > 0)
                wrong.Add($"{early} of {waiting.Count} booklet(s) laid out at {office} with no player near");
            InterchangeBooklets.StandIns.Add(table.transform.position + new Vector3(20f, 0f, 0f));
            for (var waited = 0f; waited < 10f && !waiting.All(processed.Contains); waited += 0.5f)
                yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(0.5f);
            var at = table.transform.position;
            InterchangeBooklets.StandIns.Clear();
            var spawned = HarmonyLib.Traverse.Create(station).Field("spawnedJobOverviews").GetValue<List<JobOverview>>() ?? new List<JobOverview>();
            var onTable = waiting.Count(j => spawned.Any(o => o != null && o.job == j && (o.transform.position - at).sqrMagnitude < 10f * 10f));
            if (onTable != waiting.Count)
                wrong.Add($"{onTable} of {waiting.Count} booklet(s) on {office}'s table with another player there");
            Line($"booklets: at {office} ({station.stationInfo.YardID}'s jobs), {Vector3.Distance(here, at):F0} m from the player: {early} laid out with no one near; " +
                 $"{onTable} of {waiting.Count} on its table with another player there ({string.Join(", ", waiting.Select(j => j.ID))}); single player: {mp.Count} Multiplayer player(s)");
            if (wrong.Count > 0)
                Problem($"booklets: {string.Join("; ", wrong)}");
        }

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

        /// <summary>
        /// Each interchange a station of its own (InterchangeStations; owner, 2026-11-05: "Let's
        /// get these working as their own industry locations"): the game finds it by its code,
        /// with its name (by its own translation key), color and booklet table; the interchange's
        /// tracks are in its yard.
        /// </summary>
        private void StationsCheck()
        {
            var all = InterchangeYards.All();
            if (all.Count == 0)
                Problem("stations: no interchange tracks");
            foreach (var kv in all)
            {
                var code = InterchangeYards.CodeOf(kv.Key);
                var station = Economy.Station(code);
                if (station == null || !InterchangeStations.Is(station))
                {
                    Problem($"stations: no station {code} for {kv.Key}");
                    continue;
                }
                var info = station.stationInfo;
                var wrong = new List<string>();
                if (InterchangeYards.StationOf(kv.Key) != code)
                    wrong.Add($"its jobs' station is {InterchangeYards.StationOf(kv.Key)}");
                if (kv.Value.Any(t => t.ID.yardId != code))
                    wrong.Add($"tracks outside its yard: {string.Join(", ", kv.Value.Where(t => t.ID.yardId != code).Select(t => t.ID.FullDisplayID))}");
                if (LocalizationAPI.L(info.LocalizationKey) != InterchangeYards.TitleOf(kv.Key) || info.StationColor != InterchangeYards.ColorOf(kv.Key))
                    wrong.Add($"named '{LocalizationAPI.L(info.LocalizationKey)}' in {info.StationColor}");
                if (station.logicStation == null || !StationController.allStations.Contains(station) || StationController.GetStationByYardID(code) != station)
                    wrong.Add("not a station the game knows");
                if (HarmonyLib.Traverse.Create(station).Field("jobBookletSpawnSurface").GetValue<PointOnPlane>() == null)
                    wrong.Add("no booklet table");
                var office = HarmonyLib.Traverse.Create(station).Field("stationRange").GetValue<StationJobGenerationRange>();
                Line($"stations: {code} '{LocalizationAPI.L(info.LocalizationKey)}' for {kv.Key}: {kv.Value.Count} tracks ({string.Join(", ", kv.Value.Select(t => t.ID.FullDisplayID))}), " +
                     $"office at {(office == null ? "?" : $"{Math.Sqrt(office.PlayerSqrDistanceFromStationOffice):F0} m from the player")}");
                // A job saved with Interchange Yards 0.1.4 names the track by its old ID (MF-WI-01-X).
                var old = $"{InterchangeYards.NeighborOf(kv.Key)}-{code}-01-X";
                var loader = UnityEngine.Object.FindObjectOfType<JobSaveManager>();
                var found = loader == null ? null : HarmonyLib.AccessTools.Method(typeof(JobSaveManager), "GetYardTrackWithId", new[] { typeof(string) })?.Invoke(loader, new object[] { old }) as Track;
                if (found?.ID.FullID != $"{code}-A-01-X")
                    wrong.Add($"a saved job's track {old} is {found?.ID.FullID ?? "not found"}{(loader == null ? " (no save manager)" : "")}");
                else
                    Line($"stations: a saved job's track {old} is {found.ID.FullID}");
                if (wrong.Count > 0)
                    Problem($"stations: {code}: {string.Join("; ", wrong)}");
            }
        }

        /// <summary>
        /// Every spot of the table (Contracts.Core IndustrySpots; design, "Industry spots") on the
        /// map: its tracks are its station's, its cargo is its station's industry's, and the
        /// runtime sends that cargo to those tracks; a cargo no spot names goes to the warehouse.
        /// </summary>
        private void SpotsCheck(ContractRuntime rt)
        {
            var industries = rt.Economy?.Industries?.Values.ToList() ?? new List<Core.Industries.Industry>();
            foreach (var spot in Core.Industries.IndustrySpots.All)
            {
                var station = Economy.Station(spot.Station);
                if (station == null)
                {
                    Problem($"spots: no station {spot.Station}");
                    continue;
                }
                var handled = new HashSet<string>(industries.Where(i => i.Station == spot.Station).SelectMany(i => i.Lines).SelectMany(l => l.Cargo));
                foreach (var cargo in spot.Cargo)
                {
                    if (!handled.Contains(cargo))
                        Problem($"spots: {spot.Station} has no industry line for {cargo}");
                    if (!Enum.TryParse(cargo, out CargoType type))
                    {
                        Problem($"spots: {cargo} is not a cargo");
                        continue;
                    }
                    var tracks = ContractRuntime.IndustryTracks(station, type).Select(t => t.ID.TrackPartOnly).ToList();
                    if (!tracks.OrderBy(t => t).SequenceEqual(spot.Tracks.OrderBy(t => t)))
                        Problem($"spots: {spot.Station} {cargo} goes to {string.Join(", ", tracks)}, not {string.Join(", ", spot.Tracks)}");
                }
                Line($"spots: {spot.Station} {spot.What}: {string.Join(", ", spot.Tracks)} for {string.Join(", ", spot.Cargo)}");
            }
            var sm = Economy.Station("SM");
            if (sm != null)
            {
                var ore = ContractRuntime.IndustryTracks(sm, CargoType.IronOre).Select(t => t.ID.TrackPartOnly).ToList();
                Line($"spots: SM IronOre (no spot) goes to {string.Join(", ", ore)}");
                if (!ore.SequenceEqual(new[] { "A7L" }))
                    Problem("spots: SM's iron ore does not go to its warehouse track, A7L");
            }
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
            // The comms radio's Clear passes over an owned vehicle (preventDelete), and selling the only locomotive is warned of.
            var units = VehicleSales.Owned();
            var loco = units.FirstOrDefault(u => u.Cars.Any(c => de2.Contains(c)));
            var ask = loco == null ? null : VehicleSales.Ask(loco);
            Line($"starter: kept from Clear: {string.Join(", ", de2.Select(c => $"{c.ID} {c.preventDelete}"))}; {units.Count} owned unit(s); selling it asks: {ask}");
            if (de2.Any(c => !c.preventDelete))
                Problem("starter: an owned locomotive is not kept from the comms radio's Clear");
            if (units.Count(u => u.Cars.Any(c => CarTypes.IsLocomotive(c.carLivery))) == 1 && (ask == null || !ask.Contains("It is your only locomotive")))
                Problem("starter: selling the only locomotive is not warned of");
        }

        private void ShopCheck()
        {
            var locos = LocoShop.Catalogue(LocoShop.Kind.Locomotives);
            var coaches = LocoShop.Catalogue(LocoShop.Kind.Coaches);
            foreach (var (kind, items) in new[] { ("locomotives", locos), ("coaches", coaches) })
                foreach (var item in items)
                    Line($"shop {kind}: {item.Name}: ${item.Price:N0} ({(item.Stock ? "stock" : "custom")} {item.Priced}, game value ${item.Value:N0}, mass {item.Mass:F1}; " +
                         $"{string.Join(" + ", item.Cars.Select(c => $"{c.id} [{c.parentType?.kind?.id}]"))})");
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
            // Stock locomotives at the game's value; stock coaches at a third of theirs (VehiclePricing).
            foreach (var i in locos.Concat(coaches).Where(i => i.Stock))
            {
                var expected = Math.Max(500.0, Math.Round((i.Kind == LocoShop.Kind.Coaches ? i.Value / 3.0 : i.Value) / 500.0, MidpointRounding.AwayFromZero) * 500.0);
                if (Math.Abs(i.Price - expected) > 0.5)
                    Problem($"{i.Name} costs ${i.Price:N0}, not ${expected:N0}");
            }
            var dearest = locos.Where(i => i.Stock).Max(i => i.Price);
            Line($"shop: custom vehicles against the dearest stock locomotive (${dearest:N0}): " +
                 string.Join(", ", locos.Concat(coaches).Where(i => !i.Stock).Select(i => $"{i.Name} {i.Price / dearest:F2}x")));
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
            // A custom locomotive given the tender its mod left out of its set arrives with it.
            var paired = locos.FirstOrDefault(i => i.Paired);
            if (paired != null)
                buys.Add(paired);
            var pairs = string.Join(", ", locos.Where(i => i.Paired).Select(i => $"{i.Name} ({string.Join(" + ", i.Cars.Select(c => c.id))})"));
            Line($"shop: locomotives given a tender their mod leaves out: {(pairs.Length > 0 ? pairs : "none")}");
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

        /// <summary>
        /// Selling bought vehicles back (VehicleSales; owner, 2026-11-07), and purchases that never
        /// take a cleared vehicle's number. First the game's own carry-over, with fresh purchases
        /// off: a DE2 bought, cleared as the comms radio's Clear does it, and a DE2 bought again.
        /// Then with them on: a DE2 bought after another was cleared has a number of its own and
        /// the cleared one's bill is gone. Then sales: a DE2 sold pays its offer (half what was
        /// paid, less its repairs and refills) and leaves no car, bill, kept state or record; a
        /// derailed one sells where it stands; a train of two coaches sells together; a DE2
        /// bought after a sale is new; and the kiosk's Sell vehicles rows are the offers.
        /// </summary>
        /// <summary>
        /// A career's first contract (owner, 2026-11-10: "Faster service is fine"; mikebackwards,
        /// Nexus: "need to wait 6 real time hours ... until I can play the game?"):
        ///
        /// - with none at work, a contract taken starts at level 1 at once and the service comes
        ///   for it now, as the day's own: cars placed, billed, with jobs;
        /// - a second contract taken then waits for midnight, as before, and is given up at once;
        /// - the first, asked to end the day it was taken, quotes no penalty; midnight ends it at
        ///   no cost, and its cars still on interchange tracks, their jobs not taken, are set out
        ///   for the pickup with their jobs withdrawn (they kept the tracks from new cuts);
        /// - the bridge is locked until a contract is at level 3.
        /// </summary>
        private void FirstContractCheck(ContractRuntime rt)
        {
            var desk = KioskMenu.Desk;
            var inventory = SingletonBehaviour<Inventory>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var place = new KioskPlace { Id = "MF", Name = "MF" };
            var offers = desk.Offers(place);
            var bridge = offers.FirstOrDefault(o => o.Id == Economy.BridgeId);
            Line($"first: the bridge offer: {(bridge == null ? "none" : $"needs '{bridge.Needs}'")}; starts at once {desk.StartsAtOnce}");
            if (bridge == null || bridge.Needs != "Needs a contract at level 3")
                Problem("first: the bridge is not locked until a contract is at level 3");
            else
            {
                desk.Take(bridge, 1);
                if (rt.State.Contracts.Any(c => c.Id == Economy.BridgeId))
                    Problem("first: the locked bridge was taken");
            }
            var first = offers.FirstOrDefault(o => o.Needs == null && o.Id != "MF" && o.Id != Economy.BridgeId && !RuntimeDesk.IsPassenger(o.Id));
            if (first == null || !desk.StartsAtOnce)
            {
                Problem("first: no offer to take as the career's first, or it would not start at once");
                return;
            }
            var today = ContractRuntime.DayOf(ContractRuntime.Now ?? DateTime.Now);
            var money = inventory.PlayerMoney;
            desk.Take(first, 1);
            var held = rt.State.Contracts.FirstOrDefault(c => c.Id == first.Id);
            var begun = rt.Servicing;
            rt.FinishService();
            var run = rt.LastService;
            var theirs = rt.State.Waybills.Where(w => w.Contract == first.Id).ToList();
            var withJobs = theirs.Count(w => registry.GetTrainCarByCarGuid(w.Guid) is TrainCar c && jobs.GetJobOfCar(c.logicCar) != null);
            Line($"first: took {first.Id}: level {held?.Level}, service begun {begun}, day {run?.Day} (today {today}, service day {rt.State.ServiceDay}): {run?.Summary}");
            Line($"first: {theirs.Count} car(s) billed to it, {withJobs} with a job; starts at once now {desk.StartsAtOnce}");
            if (held == null || held.Level != 1 || !begun || run == null || run.Day != today || rt.State.ServiceDay != today || theirs.Count == 0 || withJobs == 0)
                Problem("first: the career's first contract did not start at once with its cars and jobs");
            if (held == null)
                return;
            if (desk.StartsAtOnce)
                Problem("first: a second contract would start at once too");
            var second = desk.Offers(place).FirstOrDefault(o => o.Needs == null && o.Id != "MF" && o.Id != Economy.BridgeId && !RuntimeDesk.IsPassenger(o.Id));
            if (second != null)
            {
                desk.Take(second, 1);
                var later = rt.State.Contracts.FirstOrDefault(c => c.Id == second.Id);
                Line($"first: then took {second.Id}: level {later?.Level}, asks {later?.Requested}");
                if (later == null || later.Level != 0 || rt.Servicing)
                    Problem("first: a second contract did not wait for midnight");
                if (later != null)
                    desk.End(desk.Mine().First(v => v.Id == second.Id));
                if (rt.State.Contracts.Any(c => c.Id == second.Id))
                    Problem("first: a contract not yet started was not given up at once");
            }
            // Given up the day it was taken: free, and its untouched cars leave the interchange tracks to the pickup.
            var view = desk.Mine().First(v => v.Id == first.Id);
            var quoted = desk.Penalty(view, 0);
            desk.End(view);
            rt.State.Day++;
            rt.Midnight();
            var left = rt.State.Waybills.Where(w => w.Contract == first.Id).ToList();
            var onInterchange = left.Where(w => registry.GetTrainCarByCarGuid(w.Guid) is TrainCar c && InterchangeYards.IsInterchangeTrack(c.logicCar.FrontBogieTrack)).ToList();
            var stillJobs = onInterchange.Count(w => jobs.GetJobOfCar(registry.GetTrainCarByCarGuid(w.Guid).logicCar) != null);
            Line($"first: given up the same day: quoted ${quoted:N0}; held {rt.State.Contracts.Any(c => c.Id == first.Id)}, money {money:N0} -> {inventory.PlayerMoney:N0}, owed ${rt.State.Owed:N0}; " +
                 $"{onInterchange.Count} of its {left.Count} car(s) on interchange tracks: {onInterchange.Count(w => w.Stage == Stage.OffMap)} set out, {stillJobs} still with a job; the kiosk tells '{KioskMenu.TakeMessage()}'");
            if (quoted > 0.5 || rt.State.Contracts.Any(c => c.Id == first.Id) || Math.Abs(inventory.PlayerMoney - money) > 0.5 || rt.State.Owed > 0.5)
                Problem("first: giving up a contract before its first midnight was not free");
            if (onInterchange.Count == 0 || onInterchange.Any(w => w.Stage != Stage.OffMap) || stillJobs > 0)
                Problem("first: the ended contract's cars on interchange tracks were not set out with their jobs withdrawn");
        }

        private IEnumerator SaleCheck()
        {
            var inventory = SingletonBehaviour<Inventory>.Instance;
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var de2 = LocoShop.Catalogue(LocoShop.Kind.Locomotives).FirstOrDefault(i => i.Name == "DE2");
            var pair = LocoShop.Catalogue(LocoShop.Kind.Coaches).FirstOrDefault(i => i.Cars.Count == 2);
            if (inventory == null || spawner == null || de2 == null || pair == null)
            {
                Problem("sale: no shop");
                yield break;
            }
            var livery = de2.Cars[0];
            Line("sale: the kiosk lists: " + string.Join("; ", VehicleSales.Owned().Select(u => $"{u.Label} ${u.Offer:N0} (paid ${u.Paid:N0}{(u.PaidRecorded ? "" : " today")}, owed ${u.Owed:N0}{(u.Blocker != null ? ", " + u.Blocker : "")})")));
            List<TrainCar> Buy(LocoShop.Item item)
            {
                inventory.AddMoney(item.Price + 1000f);
                var before = new HashSet<TrainCar>(spawner.AllCars);
                var message = LocoShop.Buy(item, null);
                var added = spawner.AllCars.Where(c => !before.Contains(c)).ToList();
                if (added.Count != item.Cars.Count)
                    Problem($"sale: buying {item.Name} delivered {added.Count} car(s): {message.Replace("\n", " ")}");
                return added;
            }
            void Clear(TrainCar car)
            {
                spawner.DeleteCar(car);
                SingletonBehaviour<UnusedTrainCarDeleter>.Instance.ClearInvalidCarReferencesAfterManualDelete();
                if (car != null)
                    car.gameObject.SetActive(false);
            }
            bool Gone(string guid) => registry.GetTrainCarByCarGuid(guid) == null;
            bool Recorded(string guid) => ContractRuntime.Instance?.State.Bought.Any(b => b.Guid == guid) ?? false;
            string Sold(TrainCar car, string what)
            {
                var unit = VehicleSales.Owned().FirstOrDefault(u => u.Cars.Contains(car));
                if (unit == null)
                {
                    Problem($"sale: {what} {car.ID} is not on the kiosk's list");
                    return null;
                }
                var money = inventory.PlayerMoney;
                var offer = unit.Offer;
                var expected = Contracts.Core.Shop.VehicleSale.Offer(unit.Paid, unit.Owed);
                var message = VehicleSales.Sell(unit);
                var paid = inventory.PlayerMoney - money;
                Line($"sale: {what} {unit.Label} ({string.Join(", ", unit.Cars.Select(c => c.ID))}): paid ${unit.Paid:N0}{(unit.PaidRecorded ? "" : " today")}, owed ${unit.Owed:N0}, " +
                     $"offer ${offer:N0}; '{message.Replace("\n", " ")}'; money +{paid:N0}");
                if (Math.Abs(offer - expected) > 0.5 || Math.Abs(paid - offer) > 1.0)
                    Problem($"sale: {what} paid ${paid:N0}, offered ${offer:N0}, the rule says ${expected:N0}");
                return unit.Label;
            }

            // 1. The game's own carry-over: a cleared owned vehicle's number goes to the next of its model.
            VehicleSales.FreshPurchases = false;
            var a = Buy(de2).FirstOrDefault();
            if (a == null)
                yield break;
            var aId = a.ID;
            Clear(a);
            yield return new WaitForSeconds(1f);
            var keptA = VehicleSales.KeptId(livery);
            var billA = VehicleSales.HasBill(aId);
            var b = Buy(de2).FirstOrDefault();
            if (b == null)
                yield break;
            var carried = b.ID == aId;
            Line($"sale: the game's own carry-over: DE2 {aId} cleared (kept state {keptA ?? "none"}, bill {billA}); the next DE2 bought is {b.ID}, " +
                 $"{(carried ? "the cleared one's number" : "a number of its own")}; the cleared one's bill now {VehicleSales.HasBill(aId)}");
            if (!carried)
                Line("sale: the carry-over did not happen here; fresh purchases change nothing");
            var bId = b.ID;
            Clear(b);
            yield return new WaitForSeconds(1f);

            // 2. Fresh purchases: a new DE2 gets a number of its own, and the cleared one's bill goes.
            VehicleSales.FreshPurchases = true;
            var keptB = VehicleSales.KeptId(livery);
            var c = Buy(de2).FirstOrDefault();
            if (c == null)
                yield break;
            Line($"sale: fresh: DE2 {bId} cleared (kept state {keptB ?? "none"}, bill {VehicleSales.HasBill(bId)}); the next DE2 is {c.ID}; kept state now {VehicleSales.KeptId(livery) ?? "none"}, " +
                 $"the cleared one's bill {VehicleSales.HasBill(bId)}");
            if (c.ID == bId || VehicleSales.KeptId(livery) != null || VehicleSales.HasBill(bId))
                Problem($"sale: a DE2 bought after one was cleared is {c.ID} (cleared {bId}), or the cleared one's state or bill stayed");

            // 3. A DE2 in good order sold: its offer paid, and nothing left behind.
            var cId = c.ID;
            var cGuid = c.logicCar.carGuid;
            yield return new WaitForSeconds(1f);
            Sold(c, "DE2");
            yield return new WaitForSeconds(1f);
            if (!Gone(cGuid) || VehicleSales.HasBill(cId) || VehicleSales.KeptId(livery) == cId || Recorded(cGuid))
                Problem($"sale: DE2 {cId} sold left something: car {!Gone(cGuid)}, bill {VehicleSales.HasBill(cId)}, kept state {VehicleSales.KeptId(livery) == cId}, record {Recorded(cGuid)}");

            // 4. A wreck sells where it stands.
            var e = Buy(de2).FirstOrDefault();
            if (e == null)
                yield break;
            var eGuid = e.logicCar.carGuid;
            e.Derail();
            yield return new WaitForSeconds(2f);
            Line($"sale: DE2 {e.ID} derailed: {e.derailed}");
            Sold(e, "derailed DE2");
            yield return new WaitForSeconds(1f);
            if (!Gone(eGuid))
                Problem("sale: the derailed DE2 is still there");

            // 5. A train of two coaches sells together.
            var coaches = Buy(pair);
            if (coaches.Count != 2)
                yield break;
            yield return new WaitForSeconds(1f);
            var unit2 = VehicleSales.Owned().FirstOrDefault(u => u.Cars.Contains(coaches[0]));
            if (unit2 == null || unit2.Cars.Count != 2 || !unit2.Cars.Contains(coaches[1]) || unit2.Blocker != null)
                Problem($"sale: the two coaches bought together are not one sale: {unit2?.Cars.Count ?? 0} car(s), {unit2?.Blocker ?? "no blocker"}");
            var guids = coaches.Select(x => x.logicCar.carGuid).ToList();
            Sold(coaches[0], "coaches");
            yield return new WaitForSeconds(1f);
            if (!guids.All(Gone))
                Problem("sale: a coach of the pair is still there");

            // 6. A DE2 bought after a sale is new.
            var f = Buy(de2).FirstOrDefault();
            if (f != null)
            {
                Line($"sale: the next DE2 bought is {f.ID} (sold {cId})");
                if (f.ID == cId)
                    Problem("sale: a DE2 bought after a sale took the sold one's number");
                // The kiosk's rows are the offers.
                var main = Resources.FindObjectsOfTypeAll<CareerManagerMainScreen>().FirstOrDefault(m => m.gameObject.scene.IsValid() && KioskMenu.TryGet(m, out _));
                if (main != null && KioskMenu.TryGet(main, out var x))
                {
                    var kiosk = main.screenSwitcher;
                    kiosk.SetActiveDisplay(main);
                    kiosk.SetActiveDisplay(x.List);
                    var row = x.List.Entries.FirstOrDefault(r => r.Id == RuntimeDesk.SellId);
                    if (row == null)
                        Problem("sale: no Sell vehicles row on the kiosk");
                    else
                    {
                        x.Detail.Show(row);
                        kiosk.SetActiveDisplay(x.Detail);
                        yield return null;
                        var shown = x.Detail.Shown().ToList();
                        Line($"sale: the kiosk's Sell vehicles: {string.Join("; ", shown)}");
                        var mine = VehicleSales.Owned().FirstOrDefault(u => u.Cars.Contains(f));
                        if (mine == null || !shown.Any(s => s.StartsWith(mine.Label) && s.Contains($"${mine.Offer:N0}")))
                            Problem("sale: the kiosk does not offer the DE2 just bought at its price");
                        kiosk.SetActiveDisplay(x.List);
                        kiosk.SetActiveDisplay(main);
                    }
                }
                else
                    Problem("sale: no kiosk with Contracts");
                Sold(f, "DE2");
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
                    Try($"papers of {job.ID}", () => Papers(job));
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

        /// <summary>
        /// A move within a station (StationMoves; owner, 2026-11-05: they "are labeled as
        /// logistics and freight haul rather than shunting"): a transport or empty haul between
        /// two of one station's tracks, neither an interchange's. Its ID has the shunting code,
        /// and its papers, made as the game makes them, say Shunting in the shunting color: the
        /// overview, the booklet (cover, front page, then couple, uncouple and validate, numbered
        /// in order) and the expired and missing-license reports.
        /// </summary>
        private void Papers(Job job)
        {
            var start = job.tasks.FirstOrDefault()?.GetTaskData().startTrack;
            var end = ContractRuntime.JobDestination(job);
            if (InterchangeYards.IsInterchangeTrack(start) || InterchangeYards.IsInterchangeTrack(end))
            {
                InterchangePapersCheck(job, start, end);
                return;
            }
            if (job.jobType != JobType.Transport && job.jobType != JobType.EmptyHaul || start == null || end == null
                || start.ID.yardId != end.ID.yardId || InterchangeYards.IsInterchangeTrack(start) || InterchangeYards.IsInterchangeTrack(end))
                return;
            movesChecked++;
            var kind = StationMoves.KindOf(job.ID, job.jobType);
            if (kind == null)
            {
                Problem($"papers: {job.ID} ({job.jobType}) moves cars within {start.ID.yardId} but has no shunting code");
                return;
            }
            var data = new Job_data(job);
            List<TemplatePaperData> Made(Type creator, string method, params object[] args) =>
                (List<TemplatePaperData>)HarmonyLib.AccessTools.Method(creator, method, args.Select(a => a.GetType()).ToArray()).Invoke(null, args);
            var overview = BookletCreator_JobOverview.GetJobOverviewTemplateData(data);
            var booklet = Made(typeof(BookletCreator_Job), "GetBookletTemplateData", data);
            var expired = Made(typeof(BookletCreator_JobExpiredReport), "GetJobExpiredTemplateData", data);
            var missing = Made(typeof(BookletCreator_JobMissingLicense), "GetMissingLicenseTemplateData", data, true);
            var name = C.SHUNTING_JOB_TYPE_NAME;
            var color = C.SHUNTING_UNLOAD_JOB_TYPE_COLOR;
            var wrong = new List<string>();
            void Expect(bool ok, string what)
            {
                if (!ok)
                    wrong.Add(what);
            }
            var fronts = overview.Concat(booklet).OfType<FrontPageTemplatePaperData>().ToList();
            Expect(fronts.Count == 2, $"{fronts.Count} front pages, not 2");
            foreach (var f in fronts)
            {
                Expect(f.jobType == name && f.jobTypeColor == color, $"a front page says {f.jobType}");
                Expect(f.jobDescription == MovePapers.Description(kind.Value, f.distinctCargoTypes), $"a front page's description is '{f.jobDescription}'");
            }
            Expect(booklet.OfType<CoverPageTemplatePaperData>().All(c => c.jobType == LocalizationAPI.L("job/shunting_job_cover_page")), "the cover does not say shunting");
            var kinds = booklet.Select(p => p is TaskTemplatePaperData t ? t.taskType : p.GetType().Name.Replace("TemplatePaperData", "")).ToList();
            var expected = new[] { "CoverPage", "FrontPage", LocalizationAPI.L("job/task_type_couple"), LocalizationAPI.L("job/task_type_uncouple"), "ValidateJobTask" };
            Expect(kinds.SequenceEqual(expected), $"the booklet's pages are {string.Join(", ", kinds)}");
            var numbers = booklet.Select(p => p is CoverPageTemplatePaperData c ? c.pageNumber + "/" + c.totalPages
                : p is FrontPageTemplatePaperData f ? f.pageNumber + "/" + f.totalPages
                : p is TaskTemplatePaperData t ? t.pageNumber + "/" + t.totalPages + " step " + t.stepNum
                : p is ValidateJobTaskTemplatePaperData v ? v.pageNumber + "/" + v.totalPages + " step " + v.stepNum : "?").ToList();
            var n = booklet.Count;
            Expect(numbers.SequenceEqual(new[] { $"1/{n}", $"2/{n}", $"3/{n} step 1", $"4/{n} step 2", $"5/{n} step 3" }), $"the booklet is numbered {string.Join(", ", numbers)}");
            Expect(expired.OfType<JobExpiredTemplatePaperData>().Single().jobType == name, "the expired report does not say shunting");
            Expect(missing.OfType<MissingLicensesPageTemplatePaperData>().Single().jobType == name, "the missing-license report does not say shunting");
            Line($"papers: {job.ID} {job.jobType} ({kind}) {start.ID.FullDisplayID} -> {end.ID.FullDisplayID}: {fronts.FirstOrDefault()?.jobType}, " +
                 $"'{fronts.FirstOrDefault()?.jobDescription}'; booklet {string.Join(", ", kinds)}");
            if (wrong.Count > 0)
                Problem($"papers: {job.ID}: {string.Join("; ", wrong)}");
        }

        /// <summary>
        /// A job to or from an interchange (InterchangePapers; owner, 2026-11-05: "The job bills
        /// don't have the interchanges as a destination or source"): its overview's and booklet's
        /// front pages name the interchange as the start or end, in its color; the booklet's haul
        /// page names it as the destination; its couple or uncouple page gives the interchange's
        /// code as the yard of its track.
        /// </summary>
        private void InterchangePapersCheck(Job job, Track start, Track end, bool writtenBefore = false)
        {
            interchangePapersChecked++;
            var from = InterchangeYards.InterchangeOf(start);
            var to = InterchangeYards.InterchangeOf(end);
            var data = new Job_data(job);
            var stationWrong = new List<string>();
            // The interchange's own station's job (InterchangeStations): its ID, its chain's end, a haul.
            if (from != null)
            {
                var code = InterchangeYards.CodeOf(from);
                var holder = StationController.allStations.FirstOrDefault(s => s?.logicStation != null
                    && (s.logicStation.availableJobs.Contains(job) || s.logicStation.takenJobs.Contains(job)))?.stationInfo.YardID;
                // A job written before the interchanges were stations is the neighbor station's.
                var neighbor = writtenBefore && holder == InterchangeYards.NeighborOf(from) && job.chainData.chainOriginYardId == holder;
                if (!neighbor && (!job.ID.StartsWith(code + "-", StringComparison.Ordinal) || job.chainData.chainOriginYardId != code || holder != code))
                    stationWrong.Add($"starts at the interchange but is {job.ID} of {holder ?? "no station"}, from {job.chainData.chainOriginYardId}");
            }
            if (to != null && job.chainData.chainDestinationYardId != InterchangeYards.CodeOf(to)
                && !(writtenBefore && job.chainData.chainDestinationYardId == InterchangeYards.NeighborOf(to)))
                stationWrong.Add($"ends at the interchange but goes to {job.chainData.chainDestinationYardId}");
            if (StationMoves.KindOf(job.ID, job.jobType) != null && !writtenBefore)
                stationWrong.Add($"is a station move ({StationMoves.KindOf(job.ID, job.jobType)}), not a haul");
            var overview = BookletCreator_JobOverview.GetJobOverviewTemplateData(data);
            var booklet = (List<TemplatePaperData>)HarmonyLib.AccessTools.Method(typeof(BookletCreator_Job), "GetBookletTemplateData", new[] { typeof(Job_data) }).Invoke(null, new object[] { data });
            var wrong = new List<string>();
            void Expect(bool ok, string what)
            {
                if (!ok)
                    wrong.Add(what);
            }
            var fronts = overview.Concat(booklet).OfType<FrontPageTemplatePaperData>().ToList();
            Expect(fronts.Count == 2, $"{fronts.Count} front pages, not 2");
            foreach (var f in fronts)
            {
                if (from != null)
                    Expect(f.startStationName == InterchangeYards.TitleOf(from) && f.startStationBgColor == InterchangeYards.ColorOf(from),
                        $"a front page starts at '{f.startStationName}' ({f.startStationType})");
                else
                    Expect(f.startStationName == LocalizationAPI.L(data.chainOriginStationInfo.LocalizationKey), $"a front page starts at '{f.startStationName}', not the station");
                if (to != null)
                    Expect(f.endStationName == InterchangeYards.TitleOf(to) && f.endStationBgColor == InterchangeYards.ColorOf(to),
                        $"a front page ends at '{f.endStationName}' ({f.endStationType})");
                else
                    Expect(f.endStationName == LocalizationAPI.L(data.chainDestinationStationInfo.LocalizationKey), $"a front page ends at '{f.endStationName}', not the station");
            }
            var tasks = booklet.OfType<TaskTemplatePaperData>().ToList();
            var haul = tasks.FirstOrDefault(t => t.taskType == LocalizationAPI.L("job/task_type_haul"));
            if (to != null)
                Expect(haul != null && haul.stationName == InterchangeYards.TitleOf(to) && haul.stationColor == InterchangeYards.ColorOf(to),
                    $"the haul page goes to '{haul?.stationName}'");
            var couple = tasks.FirstOrDefault(t => t.taskType == LocalizationAPI.L("job/task_type_couple"));
            var uncouple = tasks.LastOrDefault(t => t.taskType == LocalizationAPI.L("job/task_type_uncouple"));
            if (from != null)
                Expect(couple != null && couple.yardId == InterchangeYards.CodeOf(from) && couple.trackId == start.ID.TrackPartOnly && couple.yardColor == InterchangeYards.ColorOf(from),
                    $"the couple page's track is {couple?.yardId} {couple?.trackId}");
            else if (couple != null)
                Expect(couple.yardId == start.ID.yardId, $"the couple page's yard is {couple.yardId}, not {start.ID.yardId}");
            if (to != null)
                Expect(uncouple != null && uncouple.yardId == InterchangeYards.CodeOf(to) && uncouple.trackId == end.ID.TrackPartOnly && uncouple.yardColor == InterchangeYards.ColorOf(to),
                    $"the uncouple page's track is {uncouple?.yardId} {uncouple?.trackId}");
            else if (uncouple != null)
                Expect(uncouple.yardId == end.ID.yardId, $"the uncouple page's yard is {uncouple.yardId}, not {end.ID.yardId}");
            var f0 = fronts.FirstOrDefault();
            Line($"papers: {job.ID} {job.jobType} {start?.ID.FullDisplayID} -> {end?.ID.FullDisplayID}: from '{f0?.startStationName}' ({f0?.startStationType}) " +
                 $"to '{f0?.endStationName}' ({f0?.endStationType}); couple {couple?.yardId} {couple?.trackId}, haul to '{haul?.stationName}', uncouple {uncouple?.yardId} {uncouple?.trackId}; " +
                 $"stock {data.chainOriginStationInfo?.YardID} '{data.chainOriginStationInfo?.Type}' -> {data.chainDestinationStationInfo?.YardID} '{data.chainDestinationStationInfo?.Type}'");
            if (wrong.Count > 0)
                Problem($"papers: {job.ID}: {string.Join("; ", wrong)}");
            if (stationWrong.Count > 0)
                Problem($"stations: {job.ID}: {string.Join("; ", stationWrong)}");
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
            // The desk's text names six cars a line ("and 2 more"); its list has them all.
            var waiting = new HashSet<string>(rt.Jobless().Where(c => c.Reason == JoblessReason.WaitingForPickup).Select(c => c.Id));
            if (before.Count < setOut.Count || before.Text == null || !setOut.All(x => waiting.Contains(x.Car.ID)) || !before.Text.Contains("pickup"))
                Problem("set-out moved: the desk does not list the cars set out for pickup");
            var (w0, car) = setOut[0];
            // Into the yard of the station beside the interchange (the interchange's own has no storage).
            var station = Economy.Station(InterchangeYards.NeighborOf(InterchangeYards.InterchangeOf(car.logicCar.FrontBogieTrack)));
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
            // Cars come on the industry's request (owner, 2026-11-10): the service orders no more
            // than the contract asked for; and with this service's cars now in work, the next day's
            // request is its level's carloads less them, role by role (less still to ship while it
            // waits for materials).
            var run = rt.LastService;
            var hand = rt.InHand();
            var industry = rt.Economy.Industries.TryGetValue(contract, out var found) ? found : null;
            var level = rt.State.Contracts.FirstOrDefault(c => c.Id == contract)?.Level ?? 0;
            var ordered = run?.Orders.Where(o => o.Order.Contract == contract).Sum(o => o.Order.Count) ?? -1;
            var asked = run != null && run.Requests.TryGetValue(contract, out var r) ? r : (Asked: -1, Daily: -1);
            Line($"second service: {contract} asked for {asked.Asked} of its {asked.Daily} carloads a day and {ordered} car(s) were ordered for it");
            if (asked.Asked < 0 || ordered > asked.Asked)
                Problem($"second service: {ordered} car(s) ordered for a request of {asked.Asked}");
            if (industry != null && level >= 1)
            {
                var roles = new[] { LineRole.Product, LineRole.Material, LineRole.Waste };
                var next = IndustryPlanner.Requests(industry, level, hand);
                Line("second service: in work now, and what the next day would ask: " +
                     string.Join(", ", roles.Select(role => $"{role} {hand.Of(contract, role)} in work, {next[role]} of {industry.Daily(role, level)}")));
                if (roles.Sum(role => hand.Of(contract, role)) == 0)
                    Problem("second service: none of the contract's cars count as in work after its service");
                if (roles.Any(role => next[role] > Math.Max(0, industry.Daily(role, level) - hand.Of(contract, role))))
                    Problem("second service: the next day would ask for more than the level's carloads less the cars in work");
                if (roles.Sum(role => next[role]) >= roles.Sum(role => industry.Daily(role, level)))
                    Problem("second service: with cars in work, the next day would ask for a full day again");
            }
        }

        /// <summary>
        /// The kiosk's info screen a page at a time (InfoPages): a long "Cars without jobs" list
        /// is laid out in pages, the title says which, up and down turn them and stop at the ends,
        /// and confirm goes back to the list with the paragraph's layout as it was.
        /// </summary>
        private IEnumerator PagingCheck()
        {
            var main = Resources.FindObjectsOfTypeAll<CareerManagerMainScreen>().FirstOrDefault(m => m.gameObject.scene.IsValid() && KioskMenu.TryGet(m, out _));
            if (main == null || !KioskMenu.TryGet(main, out var x))
            {
                Problem("paging: no kiosk with Contracts");
                yield break;
            }
            var kiosk = main.screenSwitcher;
            var info = kiosk.GetComponentInChildren<CareerManagerInfoScreen>(true);
            var layout = info.paragraph.overflowMode;
            var reasons = (JoblessReason[])Enum.GetValues(typeof(JoblessReason));
            var places = new[] { "CMS", "CP", "HB", "IMW", "MF", "SM", "FF", "GF", "WI", "NEI" };
            var cars = Enumerable.Range(0, 60).Select(i => new JoblessCar($"T-{i:D3}", reasons[i % reasons.Length], places[i % places.Length],
                reasons[i % reasons.Length] == JoblessReason.BeingLoaded ? "ready tomorrow 08:17" : null)).ToList();
            var text = JoblessReport.Paragraph(cars, 6);
            kiosk.SetActiveDisplay(x.List);
            InfoPages.Show(kiosk, info, x.List, ContractsScreen.JoblessTitle, text);
            yield return null;
            var (page, count) = InfoPages.State(info);
            Line($"paging: {cars.Count} cars in {text.Split(new[] { "\n\n" }, StringSplitOptions.None).Length} groups: page {page} of {count}, title '{info.title.text}', showing page {info.paragraph.pageToDisplay}");
            if (count < 2 || page != 1 || info.title.text != $"{ContractsScreen.JoblessTitle} 1/{count}")
                Problem("paging: a long list is not shown in pages from the first");
            kiosk.HandleInput(InputAction.Down);
            if (InfoPages.State(info).Page != 2 || info.paragraph.pageToDisplay != 2 || !info.title.text.EndsWith($" 2/{count}"))
                Problem("paging: down did not turn to the second page");
            kiosk.HandleInput(InputAction.Up);
            kiosk.HandleInput(InputAction.Up);
            if (InfoPages.State(info).Page != 1 || !ReferenceEquals(kiosk.CurrentScreen, info))
                Problem("paging: up did not stop at the first page on the screen");
            for (var i = 0; i < count + 2; i++)
                kiosk.HandleInput(InputAction.Down);
            Line($"paging: after {count + 2} downs: page {InfoPages.State(info).Page} of {count}, title '{info.title.text}'");
            if (InfoPages.State(info).Page != count || !ReferenceEquals(kiosk.CurrentScreen, info))
                Problem("paging: down did not stop at the last page on the screen");
            kiosk.HandleInput(InputAction.Confirm);
            yield return null;
            Line($"paging: confirm: back to {kiosk.CurrentScreen?.GetType().Name}; the paragraph's layout {info.paragraph.overflowMode} (was {layout}); paging {InfoPages.State(info).Count}");
            if (!ReferenceEquals(kiosk.CurrentScreen, x.List) || info.paragraph.overflowMode != layout || InfoPages.State(info).Count != 0)
                Problem("paging: confirm did not go back to the list with the info screen as it was");
            kiosk.SetActiveDisplay(main);
        }

        /// <summary>
        /// Persistent Jobs' "Suspend far away cars" (FarCarGuard; a Nexus bug report, 2026-11-04):
        /// its suspension of a station's cars, forced at MF, leaves the contract cars there and
        /// takes a plain car; the player's arrival at the station (its generator's run) brings the
        /// plain car back. With the guard off for one contract car, the car suspended keeps its
        /// bill, through a service too, and comes back a contract car.
        /// </summary>
        private IEnumerator SuspensionCheck(ContractRuntime rt, string contract)
        {
            var far = PersistentJobs.FarCars;
            var mf = Economy.Station("MF");
            if (far == null || mf == null)
            {
                Problem("suspension: no Persistent Jobs far-car suspension or no MF");
                yield break;
            }
            var run = HarmonyLib.AccessTools.Method(far, "RunSuspendCars");
            var resume = HarmonyLib.AccessTools.Method(far, "ResumeCarsInStation");
            bool Busy() => (bool)(HarmonyLib.AccessTools.Field(far, "SuspendCoroRunning")?.GetValue(null) ?? false) || (bool)(HarmonyLib.AccessTools.Field(far, "ResumeCoroRunning")?.GetValue(null) ?? false);
            IEnumerator Settle()
            {
                yield return new WaitForSeconds(0.5f);
                for (var waited = 0f; Busy() && waited < 30f; waited += 0.5f)
                    yield return new WaitForSeconds(0.5f);
                yield return new WaitForSeconds(1f);
            }
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var rails = new HashSet<RailTrack>(mf.AllStationTracks);
            bool AtMf(TrainCar c) => c?.logicCar?.FrontBogieTrack != null && RailTrackRegistry.LogicToRailTrack.TryGetValue(c.logicCar.FrontBogieTrack, out var r) && rails.Contains(r);
            var ours = rt.State.Waybills.Select(w => registry.GetTrainCarByCarGuid(w.Guid)).Where(AtMf).ToList();
            // A plain car (no contract's) on a free storage track of MF, to be suspended and brought back.
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            var storage = mf.logicStation.yard.StorageTracks.FirstOrDefault(t => t.GetCarsFullyOnTrack().Count == 0 && t.GetCarsPartiallyOnTrack().Count == 0
                                                                                  && organizer.GetFreeSpaceOnTrack(t) > 60.0 && RailTrackRegistry.LogicToRailTrack.ContainsKey(t));
            var livery = Globals.G.Types.carTypes.Where(t => t?.liveries != null).SelectMany(t => t.liveries)
                .FirstOrDefault(l => l != null && !l.isHidden && l.prefab != null && CarTypes.IsRegularCar(l) && !LocoShop.IsCoach(l));
            if (ours.Count == 0 || storage == null || livery == null)
            {
                Problem($"suspension: nothing to check with ({ours.Count} contract car(s) on MF's tracks, a free storage track {storage != null}, a plain car type {livery != null})");
                yield break;
            }
            var plain = SingletonBehaviour<CarSpawner>.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { livery }, null, RailTrackRegistry.LogicToRailTrack[storage], true, true)?.FirstOrDefault();
            yield return new WaitForSeconds(1f);
            var plainGuid = plain?.logicCar?.carGuid;
            var before = SingletonBehaviour<CarSpawner>.Instance.AllCars.Count;
            Line($"suspension: {ours.Count} contract car(s) on MF's tracks ({string.Join(", ", ours.Select(c => c.ID))}); a plain {livery.id} {plain?.ID} on {storage.ID.FullDisplayID}; {before} cars in all");

            run.Invoke(null, new object[] { true, new List<StationController> { mf }, null });
            yield return Settle();
            var kept = ours.Count(c => c != null && registry.GetTrainCarByCarGuid(c.logicCar.carGuid) != null);
            var plainAsleep = plainGuid != null && registry.GetTrainCarByCarGuid(plainGuid) == null && PersistentJobs.IsSuspended(plainGuid);
            Line($"suspension: MF's cars suspended: {kept} of {ours.Count} contract car(s) still there; the plain car {(plainAsleep ? "suspended" : "not suspended")}");
            if (kept != ours.Count)
                Problem("suspension: Persistent Jobs suspended contract cars");
            if (!plainAsleep)
                Problem("suspension: Persistent Jobs did not suspend the plain car (the check proves nothing)");

            // The player's arrival: the station's generator runs; Persistent Jobs brings its cars back, and nothing spawns.
            mf.ProceduralJobsController.TryToGenerateJobs();
            yield return Settle();
            var plainBack = plainGuid != null && registry.GetTrainCarByCarGuid(plainGuid) != null;
            var after = SingletonBehaviour<CarSpawner>.Instance.AllCars.Count;
            Line($"suspension: arrival at MF: the plain car {(plainBack ? "back" : "still gone")}; {after} cars in all (were {before})");
            if (!plainBack)
                Problem("suspension: arriving at MF did not bring back the car Persistent Jobs suspended there");
            if (after != before)
                Problem("suspension: arriving at MF spawned or lost cars");

            // A contract car suspended all the same (the guard off): its bill stays, a service on, and it comes back with it.
            var one = ours.FirstOrDefault(c => c != null && c.trainset != null && c.trainset.cars.Count == 1) ?? ours[0];
            var set = one.trainset?.cars?.ToList() ?? new List<TrainCar> { one };
            var guids = set.Select(c => c.logicCar.carGuid).ToList();
            var oneId = one.ID;
            var stages = guids.Select(g => rt.State.Of(g)?.Stage.ToString() ?? "no bill").ToList();
            // Off until Persistent Jobs has done it: its coroutine suspends the cars a frame or more later.
            FarCarGuard.Guarding = false;
            try
            {
                run.Invoke(null, new object[] { true, new List<StationController>(), set });
                yield return Settle();
            }
            finally
            {
                FarCarGuard.Guarding = true;
            }
            var asleep = guids.Count(g => registry.GetTrainCarByCarGuid(g) == null && PersistentJobs.IsSuspended(g));
            var billed = guids.Count(g => rt.State.Of(g) != null);
            Line($"suspension: {set.Count} contract car(s) ({oneId}'s cut, {string.Join("/", stages)}) suspended with the guard off: {asleep} suspended, {billed} with their bills");
            if (asleep != set.Count)
                Problem("suspension: the contract cut was not suspended with the guard off (the check proves nothing)");
            if (billed != set.Count)
                Problem("suspension: a contract car suspended by Persistent Jobs lost its bill");
            serviceDay += 3;
            var summary = rt.Service(serviceDay);
            var billedAfter = guids.Count(g => rt.State.Of(g) != null);
            Line($"suspension: a service with them suspended: {summary}; {billedAfter} of {set.Count} still billed");
            if (billedAfter != set.Count || summary == null || !summary.Contains("suspended by Persistent Jobs (kept)"))
                Problem("suspension: the service counted the suspended contract cars as gone");
            resume.Invoke(null, new object[] { "MF", null });
            yield return Settle();
            var back = guids.Count(g => registry.GetTrainCarByCarGuid(g) != null && rt.State.Of(g) != null);
            Line($"suspension: brought back: {back} of {set.Count} contract car(s) there with their bills ({string.Join("/", guids.Select(g => rt.State.Of(g)?.Stage.ToString() ?? "no bill"))})");
            if (back != set.Count)
                Problem("suspension: the contract cars did not come back as contract cars");
        }

        private void SaveCheck(ContractRuntime rt)
        {
            var data = SaveGameManager.MakeEmptySave();
            rt.State.Write(data);
            var back = ContractState.Read(data, out var unreadable);
            Line($"save: {rt.State.Contracts.Count} contracts, {rt.State.Waybills.Count} waybills written; read back {back?.Contracts.Count ?? -1} and {back?.Waybills.Count ?? -1} (unreadable {unreadable}); guids in save {ContractState.GuidsIn(data).Count}");
            if (back == null || back.Waybills.Count != rt.State.Waybills.Count || back.Contracts.Count != rt.State.Contracts.Count)
                Problem("the saved state did not round-trip");
            if (back != null && back.ModVersion != Main.Version)
                Problem($"save: stamped '{back.ModVersion}', not this build's {Main.Version}");

            // Older saves, and ones that are not right (ContractState.Parse, Normalize).
            // 0.1.0's fields only: everything added since reads as empty.
            var old = ContractState.Parse(Newtonsoft.Json.Linq.JObject.Parse(
                "{\"version\":1,\"starter\":true,\"seed\":42,\"day\":6000,\"service\":6000," +
                "\"contracts\":[{\"id\":\"CME\",\"short\":\"CME Coal Mine East\",\"level\":2,\"requested\":2,\"since\":5990}]," +
                "\"waybills\":[{\"guid\":\"g1\",\"contract\":\"CME\",\"cargo\":\"Coal\",\"next\":\"CME\",\"then\":\"MF West\",\"stage\":1,\"at\":\"MF West\"}]}"), out var oldBad);
            var c0 = old?.Contracts.FirstOrDefault();
            Line($"save: 0.1.0's form: {(old == null ? "not read" : $"{old.Contracts.Count} contract(s), {old.Waybills.Count} waybill(s), stage {old.Waybills.FirstOrDefault()?.Stage}, history {c0?.History?.Count}, bought {old.Bought?.Count}, waiting {old.Waiting?.Count}, written by {old.ModVersion ?? "an older build"}")}");
            if (old == null || oldBad || c0 == null || c0.Level != 2 || c0.History == null || c0.Closed == null || c0.Due == null || c0.Running == null || c0.Ending
                || old.Bought == null || old.Waiting == null || old.RetryAt != null || old.Waybills.Count != 1 || old.Waybills[0].Stage != Stage.ToShipper || old.Of("g1") == null)
                Problem("save: a save with 0.1.0's fields only does not read whole, with what came later empty");
            // Lists null, a record with no id, a level out of range, a stage a newer build added.
            var odd = ContractState.Parse(Newtonsoft.Json.Linq.JObject.Parse(
                "{\"version\":1,\"contracts\":[{\"id\":\"CME\",\"level\":9,\"requested\":9,\"history\":null,\"closed\":null,\"due\":null,\"running\":null},{\"level\":1}]," +
                "\"waybills\":[{\"guid\":\"g1\",\"stage\":42,\"opened\":6000.5},{\"stage\":1}],\"bought\":null,\"waiting\":null,\"owed\":-5}"), out var oddBad);
            var c1 = odd?.Contracts.FirstOrDefault();
            Line($"save: an odd save: {(odd == null ? "not read" : $"{odd.Contracts.Count} contract(s) at level {c1?.Level}, {odd.Waybills.Count} waybill(s) at stage {odd.Waybills.FirstOrDefault()?.Stage}, owed {odd.Owed}")}");
            if (odd == null || oddBad || odd.Contracts.Count != 1 || c1.Level != 5 || c1.Requested != 5 || c1.History == null || c1.Running == null
                || odd.Waybills.Count != 1 || odd.Waybills[0].Stage != Stage.Idle || odd.Waybills[0].Opened != null || odd.Bought == null || odd.Waiting == null || odd.Owed != 0.0)
                Problem("save: a save with nulls, records without ids, a level of 9 and an unknown stage was not put right");
            // A newer format, and one that is not a state at all: left alone, contracts off.
            var newer = ContractState.Parse(Newtonsoft.Json.Linq.JObject.Parse("{\"version\":99,\"mod\":\"9.9.9\",\"contracts\":[]}"), out var newerBad);
            var junk = ContractState.Parse(Newtonsoft.Json.Linq.JObject.Parse("{\"version\":1,\"waybills\":\"none\"}"), out var junkBad);
            Line($"save: a newer format: read {newer != null}, unreadable {newerBad}; not a state: read {junk != null}, unreadable {junkBad} (each says so in the log, as an error)");
            if (newer != null || !newerBad || junk != null || !junkBad)
                Problem("save: a newer format or a broken state was read, or not marked unreadable");
        }

        /// <summary>
        /// The kiosk of a player who joined a Multiplayer game (owner, 2026-11-10: "Fix,
        /// multiplayer"; Kiosk.DeskNet, RemoteDesk), with this game as host and player at once:
        /// what the player's side sends goes straight to the host's side and back, as bytes, in
        /// place of the Multiplayer mod.
        ///
        /// - a hello with this build is answered with a snapshot; with another build, refused
        ///   with the words for the kiosk, and the kiosk says so;
        /// - every answer of the player's desk is the host's own: the offers in a kiosk's order,
        ///   the railroad's contracts with their standing, volumes, raise rows, penalties and
        ///   cars, what is owed, the notices, the shops' rows and the sales';
        /// - what the player asks is done on the host: a contract taken and given up, a level
        ///   lowered and kept, an end asked and taken back, a handcar bought and sold again;
        /// - a locked offer is refused with its reason, on the list's line.
        ///
        /// Not checked: the Multiplayer mod's own carrying of the bytes, and a second game.
        /// </summary>
        private void RemoteDeskCheck(ContractRuntime rt, string id)
        {
            var host = KioskMenu.Desk;
            var remote = DeskNet.Remote;
            var inventory = SingletonBehaviour<Inventory>.Instance;
            var player = new object();
            var place = new KioskPlace { Id = "MF", Name = "MF" };
            var office = new KioskPlace { Id = InterchangeYards.All().Keys.First(), IsInterchange = true, Name = "office" };
            var sent = new List<string>();
            if (!DeskNet.ClientReady(out var before) && before == null)
                Problem("remote: no word for the kiosk of a player whose game has no multiplayer part");
            DeskNet.SendToHost = (kind, data) => { sent.Add($"to host {kind} {data.Length}"); DeskNet.OnHostMessage(player, "the probe's player", kind, data); };
            DeskNet.SendToPlayer = (to, kind, data) => { sent.Add($"to player {kind} {data.Length}"); if (ReferenceEquals(to, player)) DeskNet.OnClientMessage(kind, data); };
            try
            {
                remote.Start(takeKiosk: false);
                if (DeskNet.ClientReady(out var waiting) || waiting == null || !waiting.StartsWith("Waiting for the host"))
                    Problem($"remote: before the host's snapshot the kiosk says '{waiting}'");

                // Another build: refused, with the words for the kiosk.
                DeskNet.SendToHost(Contracts.Core.Net.DeskWire.Hello, Contracts.Core.Net.DeskWire.Write(new Contracts.Core.Net.DeskHello { Version = "0.0.1", Protocol = Contracts.Core.Net.DeskWire.Protocol }));
                DeskNet.ClientReady(out var refused);
                Line($"remote: a hello from another build: ready {remote.Ready}; the kiosk says '{refused}'");
                if (remote.Ready || refused == null || !refused.Contains("0.0.1") || !refused.Contains(Main.Version))
                    Problem("remote: a player with another build was not refused with both versions named");
                DeskNet.OnHostMessage(player, "the probe's player", Contracts.Core.Net.DeskWire.Request, new byte[] { 1, 2, 3 });   // not a message: no answer, no throw

                // This build: a snapshot.
                remote.Tick(10f);
                Line($"remote: a hello from this build: ready {remote.Ready}, revision {remote.Model.Snapshot?.Revision}; {string.Join(", ", sent)}");
                if (!remote.Ready || !DeskNet.ClientReady(out _))
                {
                    Problem("remote: the host did not answer this build's hello with a snapshot");
                    return;
                }

                // Every answer is the host's.
                var wrong = new List<string>();
                void Same(string what, object a, object b)
                {
                    if (!Equals(a, b))
                        wrong.Add($"{what}: host '{a}', player '{b}'");
                }
                foreach (var at in new[] { place, office, null })
                    Same($"offers at {at?.Id ?? "no place"}", string.Join(",", host.Offers(at).Select(o => $"{o.Id}/{o.Short}/{o.Needs}")), string.Join(",", remote.Offers(at).Select(o => $"{o.Id}/{o.Short}/{o.Needs}")));
                var mine = host.Mine();
                Same("the railroad's contracts", string.Join(",", mine.Select(v => $"{v.Id} {ContractsScreen.Value(v)}")), string.Join(",", remote.Mine().Select(v => $"{v.Id} {ContractsScreen.Value(v)}")));
                foreach (var v in mine)
                {
                    var r = remote.Mine().FirstOrDefault(x => x.Id == v.Id);
                    if (r == null)
                        continue;
                    for (var level = 1; level <= 5; level++)
                        Same($"{v.Id} volume {level}", host.Volume(v, level), remote.Volume(r, level));
                    Same($"{v.Id} legend", host.Legend(v), remote.Legend(r));
                    Same($"{v.Id} raise", $"{host.RaiseBlocker(v)}|{host.RaiseValue(v)}", $"{remote.RaiseBlocker(r)}|{remote.RaiseValue(r)}");
                    for (var target = 0; target < v.Level; target++)
                        Same($"{v.Id} penalty to {target}", host.Penalty(v, target), remote.Penalty(r, target));
                    var (hc, rc) = (host.Cars(v), remote.Cars(r));
                    Same($"{v.Id} cars", $"{hc?.ToMove}|{hc?.Line}|{hc?.Page}", $"{rc?.ToMove}|{rc?.Line}|{rc?.Page}");
                }
                var anOffer = host.Offers(place).FirstOrDefault();
                if (anOffer != null)
                    Same("an offer's volume and legend", $"{host.Volume(anOffer, 1)}|{host.Legend(anOffer)}", $"{remote.Volume(anOffer, 1)}|{remote.Legend(anOffer)}");
                Same("owed", host.Owed, remote.Owed);
                Same("notice", host.Notice, remote.Notice);
                Same("hold", host.Hold, remote.Hold);
                Same("jobless", host.Jobless(), remote.Jobless());
                Same("waiting", host.Waiting(), remote.Waiting());
                Same("day length", host.DayLength, remote.DayLength);
                Same("money", host.Money, remote.Money);
                Same("starts at once", host.StartsAtOnce, remote.StartsAtOnce);
                foreach (var coaches in new[] { false, true })
                    Same($"the shop ({(coaches ? "coaches" : "locomotives")})", string.Join(",", host.Shop(coaches, place).Select(x => $"{x.Key}/{x.Name}/{x.Price}")), string.Join(",", remote.Shop(coaches, place).Select(x => $"{x.Key}/{x.Name}/{x.Price}")));
                Same("the sales", string.Join(",", host.Selling().Select(x => $"{x.Key}/{x.Label}/{x.Offer}/{x.Blocker}/{x.Ask}")), string.Join(",", remote.Selling().Select(x => $"{x.Key}/{x.Label}/{x.Offer}/{x.Blocker}/{x.Ask}")));
                Line($"remote: {mine.Count} contract(s), {host.Offers(place).Count} offer(s), {host.Shop(false, place).Count} + {host.Shop(true, place).Count} shop row(s), {host.Selling().Count} sale row(s) compared: {(wrong.Count == 0 ? "the player's desk answers as the host's" : string.Join("; ", wrong.Take(6)))}");
                if (wrong.Count > 0)
                    Problem($"remote: {wrong.Count} answer(s) of the player's desk differ from the host's");

                // What the player asks is done on the host.
                var c = rt.State.Contracts.FirstOrDefault(x => x.Id == id);
                var open = remote.Offers(place).FirstOrDefault(o => o.Needs == null);
                var locked = remote.Offers(place).FirstOrDefault(o => o.Needs != null);
                if (open != null)
                {
                    remote.Take(open, 1);
                    var took = rt.State.Contracts.Any(x => x.Id == open.Id);
                    var shown = remote.Mine().Any(x => x.Id == open.Id);
                    remote.End(remote.Mine().First(x => x.Id == open.Id));
                    Line($"remote: took {open.Id}: on the host {took}, at the player's kiosk {shown}; given up: on the host {!rt.State.Contracts.Any(x => x.Id == open.Id)}");
                    if (!took || !shown || rt.State.Contracts.Any(x => x.Id == open.Id) || remote.Mine().Any(x => x.Id == open.Id))
                        Problem("remote: a contract taken and given up at the player's kiosk was not on the host");
                }
                if (locked != null)
                {
                    KioskMenu.TakeMessage();
                    remote.Take(locked, 1);
                    var told = KioskMenu.TakeMessage();
                    Line($"remote: a locked offer ({locked.Id}, '{locked.Needs}') asked for: on the host {rt.State.Contracts.Any(x => x.Id == locked.Id)}; the kiosk tells '{told}'");
                    if (rt.State.Contracts.Any(x => x.Id == locked.Id) || told == null)
                        Problem("remote: a locked offer was taken, or its refusal not told");
                }
                if (c != null && c.Level >= 2)
                {
                    var view = remote.Mine().First(x => x.Id == id);
                    var level = c.Level;
                    remote.RequestLevel(view, level - 1);
                    var lowered = c.Requested;
                    remote.End(remote.Mine().First(x => x.Id == id));
                    var ending = c.Ending;
                    remote.RequestLevel(remote.Mine().First(x => x.Id == id), level);
                    Line($"remote: {id} at level {level}: lowering asked: the host has {lowered}; an end asked: {ending}; kept: asks {c.Requested}, ending {c.Ending}");
                    if (lowered != level - 1 || !ending || c.Requested != level || c.Ending)
                        Problem("remote: a level asked for, an end asked for or keeping the level at the player's kiosk was not on the host");
                }

                // A handcar bought with the railroad's cash, and sold again.
                var cheap = remote.Shop(false, place).OrderBy(x => x.Price).FirstOrDefault();
                if (cheap != null)
                {
                    inventory.AddMoney((float)cheap.Price + 1_000f);
                    var (money, owned) = (inventory.PlayerMoney, host.Selling().Count);
                    remote.Tick(120f);   // a hello again: the snapshot with the money
                    KioskMenu.TakeMessage();
                    var page = remote.Buy(false, cheap.Key, place);
                    var bought = KioskMenu.TakeMessage();
                    var after = host.Selling();
                    Line($"remote: bought {cheap.Name} (${cheap.Price:N0}): the kiosk first says '{page.Split('\n')[0]}', then tells '{bought}'; money {money:N0} -> {inventory.PlayerMoney:N0}; vehicles {owned} -> {after.Count}; at the player's kiosk {remote.Selling().Count}");
                    if (after.Count != owned + 1 || Math.Abs(money - inventory.PlayerMoney - cheap.Price) > 0.5 || bought == null || remote.Selling().Count != after.Count || Math.Abs(remote.Money - inventory.PlayerMoney) > 0.5)
                        Problem("remote: a purchase at the player's kiosk was not made on the host with its cash, or not told and shown");
                    var fresh = after.FirstOrDefault(x => Math.Abs(x.Offer - cheap.Price / 2) < 1 && x.Blocker == null);
                    if (fresh != null)
                    {
                        remote.Sell(fresh.Key);
                        var sold = KioskMenu.TakeMessage();
                        Line($"remote: sold it again: vehicles {host.Selling().Count}, money {inventory.PlayerMoney:N0}; the kiosk tells '{sold}'");
                        if (host.Selling().Count != owned || sold == null || remote.Selling().Count != owned)
                            Problem("remote: a sale at the player's kiosk was not made on the host, or not told and shown");
                    }
                    else
                        Problem("remote: the vehicle bought is not among the host's sales");
                }
                Line($"remote: {sent.Count} message(s), the longest {sent.Select(x => int.Parse(x.Split(' ').Last())).DefaultIfEmpty(0).Max()} bytes");
            }
            finally
            {
                remote.Stop();
                DeskNet.OnSessionEnded();
                if (!ReferenceEquals(KioskMenu.Desk, host))
                {
                    Problem("remote: the kiosk's desk was not the host's own after the check");
                    KioskMenu.Desk = host;
                }
            }
        }

        /// <summary>
        /// The contract day against the world's (Contracts.Core DayRoll; ContractRuntime.RollTo),
        /// and the hold (the review of 2026-11-10: days missed were each scored in one tick, and a
        /// Persistent Jobs the mod can't work with turned into penalties), on the taken contract
        /// with a bill open:
        ///
        /// - four days on at once: three are skipped unscored, the bill moved on by them, and the
        ///   last midnight alone is scored;
        /// - the clock set back two days: the contract day follows, the bill moved back, nothing
        ///   scored;
        /// - on hold: the kiosk says so and takes no contract, a midnight scores nothing, charges
        ///   nothing and moves the bill on a day; off hold, all is as before.
        /// </summary>
        private void RollCheck(ContractRuntime rt, string id)
        {
            var c = id == null ? null : rt.State.Contracts.FirstOrDefault(x => x.Id == id);
            var inventory = SingletonBehaviour<Inventory>.Instance;
            var desk = KioskMenu.Desk;
            if (c == null || c.Level < 1)
            {
                Problem("roll: no contract at work to check");
                return;
            }
            var bill = rt.State.Waybills.FirstOrDefault(w => w.Contract == id) ?? rt.State.Waybills.FirstOrDefault();
            if (bill == null)
            {
                Problem("roll: no waybill to check");
                return;
            }
            var day = rt.State.Day;
            bill.Opened = day + 0.5;
            var (scores, money) = (c.History.Count, inventory.PlayerMoney);
            KioskMenu.TakeMessage();

            rt.RollTo(day + 4);
            Line($"roll: four days on: day {day} -> {rt.State.Day}; scored {c.History.Count - scores} time(s); the bill opened {day + 0.5:F2} -> {bill.Opened:F2}; level {c.Level}");
            if (rt.State.Day != day + 4 || c.History.Count - scores != 1 || bill.Opened != day + 3.5)
                Problem("roll: four days at once were not three skipped and one scored, the bill moved on by three");
            scores = c.History.Count;

            rt.RollTo(day + 2);
            Line($"roll: the clock back two days: day {rt.State.Day}; scored {c.History.Count - scores} time(s); the bill opened {bill.Opened:F2}; service day {rt.State.ServiceDay}");
            var served = (ContractRuntime.Now?.Hour ?? 6) >= 6 ? day + 2 : day + 1;
            if (rt.State.Day != day + 2 || c.History.Count != scores || bill.Opened != day + 1.5 || rt.State.ServiceDay != served)
                Problem("roll: the clock set back was not followed, the bill moved back with it, nothing scored and no service on its account");

            rt.OnHold = "Persistent Jobs has changed: the probe's hold";
            var offer = desk.Offers(new KioskPlace { Id = "MF", Name = "MF" }).FirstOrDefault();
            var level = c.Level;
            c.Requested = Math.Max(1, c.Level - 1);   // a lowering asked for: on hold it is neither made nor charged
            rt.RollTo(day + 3);
            Line($"roll: on hold: the desk says '{desk.Hold}', the notice '{desk.Notice}', an offer needs '{offer?.Needs}'; a midnight: day {rt.State.Day}, " +
                 $"scored {c.History.Count - scores} time(s), level {c.Level}, the bill opened {bill.Opened:F2}, money {money:N0} -> {inventory.PlayerMoney:N0}, owed ${rt.State.Owed:N0}");
            if (desk.Hold == null || desk.Notice != ContractTerms.HoldLine || (offer != null && offer.Needs != ContractTerms.HoldLine))
                Problem("roll: on hold the kiosk does not say so, or still offers contracts");
            if (rt.State.Day != day + 3 || c.History.Count != scores || c.Level != level || bill.Opened != day + 2.5 || Math.Abs(inventory.PlayerMoney - money) > 0.5 || rt.State.Owed > 0.5)
                Problem("roll: on hold a midnight scored, changed a level or charged, or did not move the bill on");
            rt.OnHold = null;
            c.Requested = c.Level;
            if (desk.Hold != null || desk.Notice == ContractTerms.HoldLine)
                Problem("roll: off hold the kiosk still says on hold");
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
            // One scored day: the list says how far the raise is, and so does its row's value.
            view = KioskMenu.Desk.Mine().First(v => v.Id == id);
            Line($"standing: with one scored day the list shows '{ContractsScreen.Standing(view)}', the raise row '{KioskMenu.Desk.RaiseValue(view)}'");
            if (ContractsScreen.Standing(view) != $"L{level} 1/3d" || KioskMenu.Desk.RaiseValue(view) != "1/3 days")
                Problem("standing: the list or the raise row does not show the days toward a raise");

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
            KioskMenu.TakeMessage();
            rt.State.Day++;
            rt.Midnight();
            var refusal = KioskMenu.TakeMessage();
            Line($"standing: raise asked, then a slow car: level {c.Level}, requested {c.Requested}, history [{string.Join(", ", c.History)}]; the kiosk tells '{refusal}'");
            if (c.Level != level + 1 || c.Requested != c.Level)
                Problem("a raise went through after the average fell under 90%");
            if (refusal == null || !refusal.StartsWith($"{id} stays L{c.Level}: ") || refusal.Length > KioskMenu.InfoLineChars)
                Problem("standing: the kiosk was not told the raise was refused, on one line");
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

            // 1b. A joined player's turn-in is paid straight into the cash; any other comes out in banknotes, as stock.
            var printer = job == null || job.State != JobState.Completed ? null : UnityEngine.Object.FindObjectOfType<MoneyPrinterJobValidator>();
            if (job != null && before != null && printer == null)
                Problem("pay: no done job or no validator to check a joined player's turn-in");
            if (printer != null)
            {
                var notes = HarmonyLib.Traverse.Create(printer).Field("lastPaymentMoney");
                var wage = (double)job.GetWageForTheJob();
                var money = inventory.PlayerMoney;
                ContractPay.AsGuestTurnIn = true;
                try
                {
                    printer.PrintPayment(job);
                }
                catch (Exception e)
                {
                    Problem($"pay: a joined player's turn-in threw: {e}");
                }
                ContractPay.AsGuestTurnIn = false;
                var paidIn = inventory.PlayerMoney - money;
                yield return new WaitForSeconds(4.5f);
                var printed = notes.GetValue<GameObject>();
                Line($"pay: {job.ID} turned in as by a joined player: wage ${wage:N0}, cash {money:N0} -> {money + paidIn:N0} at once, banknotes printed: {printed != null}");
                if (wage < 1.0 || Math.Abs(paidIn - wage) > 0.5 || printed != null || Math.Abs(inventory.PlayerMoney - money - wage) > 0.5)
                    Problem("pay: a joined player's turn-in was not paid straight into the cash, once, without banknotes");

                money = inventory.PlayerMoney;
                var hosts = Kiosk.Multiplayer.HostsOthers;
                try
                {
                    printer.PrintPayment(job);
                }
                catch (Exception e)
                {
                    Problem($"pay: the game's own payment threw: {e}");
                }
                yield return new WaitForSeconds(4.5f);
                printed = notes.GetValue<GameObject>();
                Line($"pay: {job.ID} turned in here ({Kiosk.Multiplayer.Describe()}; hosts others: {hosts}): banknotes printed: {printed != null}, cash {money:N0} -> {inventory.PlayerMoney:N0}");
                if (hosts)
                    Problem("pay: this game is taken to host others");
                else if (printed == null || Math.Abs(inventory.PlayerMoney - money) > 0.5)
                    Problem("pay: a turn-in outside a hosted session was not left to the game's banknotes");
                if (printed != null)
                    UnityEngine.Object.Destroy(printed);
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

            // 4. Ending is final at midnight, and charged then (owner, 2026-11-10): asked for, the
            //    contract goes on and nothing is charged; kept, midnight leaves it alone at no cost;
            //    asked for again, midnight ends it with the penalty, and what the wallet can't cover
            //    is owed.
            var owed = 0.0;
            Try("pay: ending", () =>
            {
                c.Closed.Clear();
                foreach (var w in rt.State.Waybills)
                    w.Opened = null;
                var view = KioskMenu.Desk.Mine().First(v => v.Id == id);
                var (level, days) = (c.Level, c.History.Count);
                var expected = ContractTerms.Penalty(level, 0, days);
                var quoted = KioskMenu.Desk.Penalty(view, 0);
                inventory.SetMoney(1_000);
                KioskMenu.TakeMessage();
                KioskMenu.Desk.End(view);
                var asked = KioskMenu.Desk.Mine().FirstOrDefault(v => v.Id == id);
                Line($"pay: asked to end {id} at level {level} with {days} day(s) of history: quoted ${quoted:N0} (expected ${expected:N0}); " +
                     $"held {asked != null}, shown '{(asked == null ? null : ContractsScreen.Standing(asked))}', wallet ${inventory.PlayerMoney:N0}, owed ${rt.State.Owed:N0}");
                if (asked == null || !asked.Ending || ContractsScreen.Standing(asked) != $"L{level} ends" || Math.Abs(quoted - expected) > 0.5
                    || Math.Abs(inventory.PlayerMoney - 1_000) > 0.5 || rt.State.Owed > 0.5)
                    Problem("pay: asking to end a contract ended it or charged for it before midnight");
                if (asked == null)
                    return;
                // Kept: midnight changes nothing and charges nothing.
                KioskMenu.Desk.RequestLevel(asked, level);
                rt.State.Day++;
                rt.Midnight();
                var kept = KioskMenu.Desk.Mine().FirstOrDefault(v => v.Id == id);
                Line($"pay: kept before midnight: held {kept != null}, level {kept?.Level}, ending {kept?.Ending}, wallet ${inventory.PlayerMoney:N0}, owed ${rt.State.Owed:N0}");
                if (kept == null || kept.Ending || kept.Level != level || Math.Abs(inventory.PlayerMoney - 1_000) > 0.5 || rt.State.Owed > 0.5)
                    Problem("pay: a contract kept before midnight was ended or charged for");
                if (kept == null)
                    return;
                // Asked for again, and midnight comes.
                KioskMenu.Desk.End(kept);
                rt.State.Day++;
                rt.Midnight();
                owed = rt.State.Owed;
                var told = KioskMenu.TakeMessage();
                var data = SaveGameManager.MakeEmptySave();
                rt.State.Write(data);
                var back = ContractState.Read(data, out _);
                Line($"pay: ended {id} at midnight: wallet ${inventory.PlayerMoney:N0}, owed ${owed:N0} (the desk shows ${KioskMenu.Desk.Owed:N0}); " +
                     $"the save reads back owed ${back?.Owed ?? -1:N0}; the kiosk tells '{told}'");
                if (rt.State.Contracts.Any(x => x.Id == id) || inventory.PlayerMoney > 0.5
                    || Math.Abs(owed - (expected - 1_000)) > 0.5 || back == null || Math.Abs(back.Owed - owed) > 0.5)
                    Problem("pay: midnight did not end the contract, take the wallet and owe the rest of the penalty");
                if (told == null || !told.Contains($"{id} ended"))
                    Problem("pay: the kiosk was not told the contract ended");
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
        /// <summary>
        /// A contract's cars at the kiosk (Ace, Discord, 2026-11-08: "keep track of cars
        /// in/out/waste ... sort it by most remaining moves to least"):
        ///
        /// - the counts match the cars, read here apart from the counter: every waybill billed to
        ///   the contract or bound for its industry, the cars to move those billed for a move not
        ///   yet made, each car's job the game's, and each car's line its load's;
        /// - the list goes most cars to move first, with the count in its value column, even with a
        ///   contract taken later put first in the held list;
        /// - the contract's info line says "To move: ...", "Cars a day" is a row, "ordered" is gone,
        ///   and the Cars row opens the paged list of the cars, by number and job;
        /// - the lines fit: the shown ones, and the longest each can be (two-digit counts, a
        ///   24-character name, a level change and 100%), on one line, the value clear of the name.
        ///
        /// Views of each screen at the kiosk: probe-cars-*.png.
        /// </summary>
        private IEnumerator CarsCheck(ContractRuntime rt, string contract)
        {
            var desk = KioskMenu.Desk;
            var industry = contract != null && rt.Economy.Industries.TryGetValue(contract, out var ind) ? ind : null;
            var mains = Resources.FindObjectsOfTypeAll<CareerManagerMainScreen>().Where(m => m.gameObject.scene.IsValid() && KioskMenu.TryGet(m, out _)).ToList();
            var main = mains.FirstOrDefault(m => KioskPlaces.Of(m.screenSwitcher)?.IsInterchange == true) ?? mains.FirstOrDefault();
            if (desk == null || industry == null || main == null || !KioskMenu.TryGet(main, out var x))
            {
                Problem($"cars: no contract ({contract}) or no kiosk with the Contracts menu");
                yield break;
            }
            var kiosk = main.screenSwitcher;
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;

            // 1. The cars, read apart from the counter.
            var moves = new HashSet<Stage> { Stage.ToShipper, Stage.Loading, Stage.ToReceiver, Stage.Unloading, Stage.ToInterchange };
            var expected = rt.State.Waybills.Where(w => w.Contract == contract || w.Next == contract)
                .Select(w => (W: w, Car: registry.GetTrainCarByCarGuid(w.Guid))).Where(p => p.Car != null).ToList();
            var expectedToMove = expected.Count(p => moves.Contains(p.W.Stage));
            List<ContractCar> counted = null;
            Try("cars: the counter", () =>
            {
                counted = rt.CarsOfContracts().TryGetValue(contract, out var c) ? c : new List<ContractCar>();
                Line($"cars: {contract}: {expected.Count} car(s) billed to it or bound for it, {expectedToMove} to move; by stage: " +
                     string.Join(", ", expected.GroupBy(p => p.W.Stage).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
                Line($"cars: the counter: {counted.Count} car(s), {ContractCars.ToMove(counted)} to move; " +
                     string.Join(", ", counted.GroupBy(c2 => (c2.Line, c2.Step)).OrderBy(g => g.Key).Select(g => $"{g.Key.Line} {g.Key.Step} {g.Count()}")));
                if (expected.Count == 0 || expectedToMove == 0)
                    Problem($"cars: {contract} has no cars to move to check");
                var ids = new HashSet<string>(expected.Select(p => p.Car.ID));
                if (counted.Count != expected.Count || !ids.SetEquals(counted.Select(c2 => c2.Id)))
                    Problem($"cars: the counter lists {counted.Count} car(s), not the {expected.Count} billed to {contract} or bound for it");
                if (ContractCars.ToMove(counted) != expectedToMove)
                    Problem($"cars: the counter has {ContractCars.ToMove(counted)} to move, not {expectedToMove}");
                foreach (var (w, car) in expected)
                {
                    var c2 = counted.FirstOrDefault(c3 => c3.Id == car.ID);
                    if (c2 == null)
                        continue;
                    var job = jobs.GetJobOfCar(car.logicCar)?.ID;
                    if (c2.Job != job)
                        Problem($"cars: {car.ID}'s job is {job ?? "none"}, the counter says {c2.Job ?? "none"}");
                    // A loaded car's line is its load's at the industry: in for a material, out for a product, waste for waste.
                    var load = car.logicCar.CurrentCargoTypeInCar;
                    if (load != CargoType.None)
                    {
                        var role = industry.Lines.FirstOrDefault(l => l.Cargo.Contains(load.ToString()))?.Role;
                        var want = role == LineRole.Material ? CarLine.In : role == LineRole.Waste ? CarLine.Waste : role == LineRole.Product ? CarLine.Out : (CarLine?)null;
                        if (want.HasValue && c2.Line != want.Value && !(c2.Line == CarLine.In && w.Next == contract))
                            Problem($"cars: {car.ID} carries {load}, {role} at {contract}, but is counted {c2.Line}");
                    }
                }
            });
            if (counted == null)
                yield break;

            // 2. The list: a contract taken now, put first in the held list, still goes after the one with cars to move.
            var other = desk.Offers(new KioskPlace { Id = industry.Station, Name = industry.Station })
                .FirstOrDefault(o => o.Needs == null && o.Id != contract && !Runtime.RuntimeDesk.IsPassenger(o.Id) && o.Id != Economy.BridgeId);
            if (other != null)
            {
                desk.Take(other, 1);
                var taken = rt.State.Contracts.FirstOrDefault(c => c.Id == other.Id);
                if (taken != null)
                {
                    rt.State.Contracts.Remove(taken);
                    rt.State.Contracts.Insert(0, taken);
                }
            }
            var facing = kiosk.transform.TransformDirection(Vector3.left);
            facing.y = 0f;
            facing.Normalize();
            var screen = kiosk.transform.TransformPoint(new Vector3(-0.10f, 1.32f, -0.08f));
            var stand = screen + facing * 0.8f;
            stand.y = kiosk.transform.position.y + 0.15f;
            PlayerManager.TeleportPlayer(stand, Quaternion.LookRotation(-facing, Vector3.up), null, true);
            yield return new WaitForSeconds(10f);
            IEnumerator Shot(string what) => Capture($"probe-cars-{what}", screen + facing * 0.55f, screen);

            kiosk.SetActiveDisplay(main);
            yield return null;
            kiosk.SetActiveDisplay(x.List);
            yield return null;
            var listLines = HarmonyLib.Traverse.Create(x.List).Field("lines").GetValue<KioskLines>();
            var at = x.List.Entries.ToList().FindIndex(e => e.Id == contract);
            for (var i = 0; i < at; i++)
            {
                kiosk.HandleInput(InputAction.Down);
                yield return null;
            }
            var held = x.List.Entries.Where(e => !e.IsOffer && Runtime.RuntimeDesk.IsContract(e.Id)).ToList();
            Line($"cars: the list: {string.Join("; ", x.List.Shown())}");
            Line($"cars: held, in the list's order: {string.Join(", ", held.Select(e => $"{e.Id} {(e.ToMove.HasValue ? e.ToMove.Value.ToString() : "-")}"))}");
            for (var i = 1; i < held.Count; i++)
                if ((held[i].ToMove ?? 0) > (held[i - 1].ToMove ?? 0))
                    Problem($"cars: the list puts {held[i - 1].Id} ({held[i - 1].ToMove}) before {held[i].Id} ({held[i].ToMove})");
            var mine = held.FirstOrDefault(e => e.Id == contract);
            if (mine == null || mine.ToMove != expectedToMove)
                Problem($"cars: the list counts {mine?.ToMove} to move for {contract}, not {expectedToMove}");
            if (other != null && held.FindIndex(e => e.Id == contract) > held.FindIndex(e => e.Id == other.Id))
                Problem($"cars: {other.Id}, taken now with nothing to move, comes before {contract}");
            var row = listLines.Rows.FirstOrDefault(r => r.Id.text == mine?.Short);
            if (row.Id == null || !row.Value.text.StartsWith($"{expectedToMove} "))
                Problem($"cars: {contract}'s row in the list is '{row.Id?.text} [{row.Value?.text}]', not its count first");
            else
            {
                Fit("the list's row", row.Id, row.Value);
                yield return Shot("list");
                // The longest a row can be: a 24-character name, two-digit count, a level change and 100%.
                var (id, value) = (row.Id.text, row.Value.text);
                row.Id.text = "IME Iron Mine East Inter";
                row.Value.text = "99 L2>3 100%";
                Fit("the longest row", row.Id, row.Value);
                yield return Shot("list-longest");
                row.Id.text = id;
                row.Value.text = value;
            }

            // 3. The contract's page.
            x.Detail.Show(mine);
            kiosk.SetActiveDisplay(x.Detail);
            yield return null;
            var detailLines = HarmonyLib.Traverse.Create(x.Detail).Field("lines").GetValue<KioskLines>();
            var shown = x.Detail.Shown().ToList();
            var cars = desk.Cars(mine);
            Line($"cars: {contract}'s page: '{detailLines.Title.text}', info '{detailLines.Info.text}', rows: {string.Join("; ", shown)}");
            if (cars == null || detailLines.Info.text != cars.Line || !detailLines.Info.text.StartsWith("To move: "))
                Problem($"cars: the info line is '{detailLines.Info.text}', not the cars to move ({cars?.Line})");
            var numbers = System.Text.RegularExpressions.Regex.Matches(detailLines.Info.text, @"\d+").Cast<System.Text.RegularExpressions.Match>().Sum(m => int.Parse(m.Value));
            if (numbers != expectedToMove)
                Problem($"cars: the info line's counts add up to {numbers}, not {expectedToMove}");
            if (detailLines.Info.text.Contains("ordered") || shown.Any(s => s.Contains("ordered")))
                Problem("cars: the contract's page still says ordered");
            if (shown.Count < 2 || shown[0] != $"Cars [{expectedToMove} to move]" || shown[1] != $"Cars a day [{desk.Volume(mine, mine.Level)}]")
                Problem($"cars: the first rows are '{string.Join("; ", shown.Take(2))}', not Cars and Cars a day");
            Fit("the info line", detailLines.Info);
            Fit("the Cars row", detailLines.Rows[0].Id, detailLines.Rows[0].Value);
            yield return Shot("contract");
            var info0 = detailLines.Info.text;
            detailLines.Info.text = "To move: 99 out / 99 in / 99 waste";
            Fit("the longest info line", detailLines.Info);
            detailLines.Info.text = info0;

            // 3b. A raise not yet to be had: its row says how far it is, and the info line why while
            //     the row is highlighted, then the screen's own line again.
            var rows = x.Detail.Shown().ToList();
            var raiseAt = rows.FindIndex(r => r.StartsWith("Raise to level"));
            var why = desk.RaiseBlocker(mine);
            if (raiseAt > 0 && why != null)
            {
                for (var i = 0; i < raiseAt; i++)
                {
                    kiosk.HandleInput(InputAction.Down);
                    yield return null;
                }
                var onRaise = detailLines.Info.text;
                Fit("the raise row", detailLines.Rows[raiseAt].Id, detailLines.Rows[raiseAt].Value);
                Fit("the raise row's reason", detailLines.Info);
                yield return Shot("raise");
                for (var i = 0; i < raiseAt; i++)
                {
                    kiosk.HandleInput(InputAction.Up);
                    yield return null;
                }
                Line($"cars: the raise row '{rows[raiseAt]}': the info line on it '{onRaise}', back on Cars '{detailLines.Info.text}'");
                if (onRaise != why || rows[raiseAt].EndsWith("[locked]") || detailLines.Info.text != info0)
                    Problem("cars: the raise row does not say how far it is, or the info line not why while it is highlighted");
            }
            else
                Line($"cars: no blocked raise row to check ({(why == null ? "the raise is allowed" : "no row")})");

            // 4. The Cars page: confirm on the Cars row.
            var infoScreen = kiosk.GetComponentInChildren<CareerManagerInfoScreen>(true);
            kiosk.HandleInput(InputAction.Confirm);
            yield return null;
            yield return null;
            var (page, pages) = InfoPages.State(infoScreen);
            var text = infoScreen.paragraph.text ?? "";
            Line($"cars: the Cars page: '{infoScreen.title.text}', page {page} of {pages}:\n{text}");
            if (!ReferenceEquals(kiosk.CurrentScreen, infoScreen) || page != 1 || !infoScreen.title.text.StartsWith(ContractScreen.PageTitle(mine)))
                Problem($"cars: confirm on Cars opened {kiosk.CurrentScreen?.GetType().Name} titled '{infoScreen.title.text}', not the Cars page");
            var missing = counted.Where(c => !text.Contains(c.Id) || (c.ToMove && c.Job != null && !text.Contains(c.Job))).Select(c => c.Id).ToList();
            if (missing.Count > 0)
                Problem($"cars: the Cars page leaves out {missing.Count} car(s) or their jobs: {string.Join(", ", missing.Take(10))}");
            if (!text.StartsWith("Out: ") || !text.Contains("\n\nIn: ") || !text.Contains("\n\nWaste: "))
                Problem("cars: the Cars page is not out, in and waste");
            Fit("the Cars page's title", infoScreen.title);
            yield return Shot("page");
            var title0 = infoScreen.title.text;
            infoScreen.title.text = "IME Iron Mine East Inter cars 9/9";
            Fit("the longest Cars page title", infoScreen.title);
            infoScreen.title.text = title0;
            if (pages > 1)
            {
                kiosk.HandleInput(InputAction.Down);
                yield return null;
                yield return Shot("page-2");
            }
            kiosk.HandleInput(InputAction.Confirm);
            yield return null;
            if (!ReferenceEquals(kiosk.CurrentScreen, x.Detail))
                Problem($"cars: confirm on the Cars page went to {kiosk.CurrentScreen?.GetType().Name}, not back to the contract");
            // 4b. Penalties owed: a row of their own on the list, with the sum, and a page.
            // With no cash, or the debt is paid from it within a tick.
            var owedBefore = rt.State.Owed;
            var wallet = SingletonBehaviour<Inventory>.Instance;
            var cashBefore = wallet.PlayerMoney;
            wallet.SetMoney(0);
            rt.State.Owed = 44_000;
            kiosk.SetActiveDisplay(main);
            yield return null;
            kiosk.SetActiveDisplay(x.List);
            yield return null;
            var owedAt = x.List.Entries.ToList().FindIndex(e => e.Id == RuntimeDesk.OwedId);
            Line($"owed: row {(owedAt < 0 ? "none" : $"{owedAt + 1} '{x.List.Entries[owedAt].Short}' [{ContractsScreen.Value(x.List.Entries[owedAt])}]")}");
            if (owedAt < 0 || ContractsScreen.Value(x.List.Entries[owedAt]) != "$44,000")
                Problem("owed: no Penalties owed row with the sum");
            else
            {
                for (var i = 0; i < owedAt; i++)
                {
                    kiosk.HandleInput(InputAction.Down);
                    yield return null;
                }
                yield return Shot("owed");
                kiosk.HandleInput(InputAction.Confirm);
                yield return null;
                yield return null;
                var owedText = infoScreen.paragraph.text ?? "";
                Line($"owed: page '{infoScreen.title.text}':\n{owedText}");
                if (!ReferenceEquals(kiosk.CurrentScreen, infoScreen) || !owedText.StartsWith("You owe $44,000"))
                    Problem("owed: confirm on the row did not open its page");
                kiosk.HandleInput(InputAction.Confirm);
                yield return null;
            }
            rt.State.Owed = owedBefore;
            wallet.SetMoney(cashBefore);

            // 5. The day length's row and page (Vertigo75, Nexus, 2026-11-09): the clone's days are the presets' 2 h.
            kiosk.SetActiveDisplay(main);
            yield return null;
            kiosk.SetActiveDisplay(x.List);
            yield return null;
            var day = desk.DayLength;
            var dayAt = x.List.Entries.ToList().FindIndex(e => e.Id == RuntimeDesk.DayLengthId);
            Line($"day length: {DV.Globals.G?.GameParams?.DayLengthInMinutes} min; row {(dayAt < 0 ? "none" : $"{dayAt + 1} [{ContractsScreen.Value(x.List.Entries[dayAt])}]")}; notice '{desk.Notice}'");
            if (day == null || dayAt < 0)
                Problem("day length: no Day length row with days shorter than 24 hours");
            else
            {
                for (var i = 0; i < dayAt; i++)
                {
                    kiosk.HandleInput(InputAction.Down);
                    yield return null;
                }
                kiosk.HandleInput(InputAction.Confirm);
                yield return null;
                yield return null;
                var dayText = infoScreen.paragraph.text ?? "";
                Line($"day length: page '{infoScreen.title.text}', {InfoPages.State(infoScreen).Count} page(s):\n{dayText}");
                if (!ReferenceEquals(kiosk.CurrentScreen, infoScreen) || !infoScreen.title.text.StartsWith(ContractsScreen.DayLengthTitle) || dayText != day.Value.Page)
                    Problem($"day length: confirm opened {kiosk.CurrentScreen?.GetType().Name} titled '{infoScreen.title.text}', not its page");
                yield return Shot("daylength");
                kiosk.HandleInput(InputAction.Confirm);
                yield return null;
                if (!ReferenceEquals(kiosk.CurrentScreen, x.List))
                    Problem($"day length: confirm on its page went to {kiosk.CurrentScreen?.GetType().Name}, not back to the list");
            }
            kiosk.SetActiveDisplay(x.List);
            kiosk.SetActiveDisplay(main);
            if (other != null)
                desk.End(held.First(e => e.Id == other.Id));
            Line($"cars: done; held: {string.Join(", ", rt.State.Contracts.Select(c => c.Id))}");
        }

        /// <summary>
        /// Whether a kiosk text fits: on one line and as wide as its box at most; with a second (a
        /// row's value), the value's text clear of the first's. Logs the measures.
        /// </summary>
        private void Fit(string what, TMPro.TextMeshPro text, TMPro.TextMeshPro value = null)
        {
            try
            {
                string Measure(TMPro.TextMeshPro t)
                {
                    t.ForceMeshUpdate();
                    var lines = t.textInfo?.lineCount ?? 0;
                    var box = t.rectTransform.rect.width;
                    var wants = t.GetPreferredValues(t.text).x;
                    if (lines > 1 || (box > 0f && wants > box + 0.001f))
                        Problem($"cars: {what}: '{t.text}' does not fit: {lines} line(s), {wants:F3} wide in {box:F3}");
                    return $"'{t.text}' {lines} line(s), {wants:F3} of {box:F3} (wrap {t.textWrappingMode}, {t.overflowMode})";
                }
                var said = Measure(text);
                if (value != null)
                {
                    said += "; value " + Measure(value);
                    var axis = text.transform.right;
                    float Edge(TMPro.TextMeshPro t, bool max)
                    {
                        var b = t.textBounds;
                        var a = Vector3.Dot(t.transform.TransformPoint(b.min), axis);
                        var c = Vector3.Dot(t.transform.TransformPoint(b.max), axis);
                        return max ? Mathf.Max(a, c) : Mathf.Min(a, c);
                    }
                    var gap = Edge(value, false) - Edge(text, true);
                    said += $"; gap {gap * 1000f:F1} mm";
                    if (gap <= 0f)
                        Problem($"cars: {what}: the value '{value.text}' runs into '{text.text}' ({gap * 1000f:F1} mm)");
                }
                Line($"cars: fit, {what}: {said}");
            }
            catch (Exception e)
            {
                Problem($"cars: measuring {what} failed: {e.Message}");
            }
        }

        /// <summary>A view from a point toward another, with the player's camera held there (as the kiosk spike takes them).</summary>
        private IEnumerator Capture(string file, Vector3 eye, Vector3 target)
        {
            var cam = PlayerManager.PlayerCamera;
            if (cam == null)
            {
                Problem("no player camera for a view");
                yield break;
            }
            var t = cam.transform;
            var parent = t.parent;
            var lp = t.localPosition;
            var lr = t.localRotation;
            var fov = cam.fieldOfView;
            var rot = Quaternion.LookRotation(target - eye, Vector3.up);
            Camera.CameraCallback hold = c =>
            {
                if (c == cam)
                    c.transform.SetPositionAndRotation(eye, rot);
            };
            t.SetParent(null, true);
            cam.fieldOfView = 50f;
            Camera.onPreCull += hold;
            for (var start = UnityEngine.Time.unscaledTime; UnityEngine.Time.unscaledTime - start < 1.5f;)
            {
                t.SetPositionAndRotation(eye, rot);
                yield return null;
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, file + ".png"));
            for (var start = UnityEngine.Time.unscaledTime; UnityEngine.Time.unscaledTime - start < 1f;)
            {
                t.SetPositionAndRotation(eye, rot);
                yield return null;
            }
            Camera.onPreCull -= hold;
            t.SetParent(parent, false);
            t.localPosition = lp;
            t.localRotation = lr;
            cam.fieldOfView = fov;
            Line($"view {file}.png");
        }

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
                if (n == 0)
                {
                    // The question, with how to sleep through the wait: all of it on the confirm screen.
                    // The confirm screen writes on the kiosk's own text lines, not on children of its own.
                    var asked = kiosk.GetComponentsInChildren<TMPro.TMP_Text>(true)
                        .FirstOrDefault(m => m.gameObject.activeInHierarchy && (m.text ?? "").Contains("Sleep Cooldown"));
                    asked?.ForceMeshUpdate();
                    Line($"kiosk load: the take question: {(asked == null ? "not found" : $"'{asked.text}', {asked.textInfo?.lineCount} line(s), overflowing {asked.isTextOverflowing}, truncated {asked.isTextTruncated}")}");
                    if (asked == null || asked.isTextTruncated || !asked.text.Contains(ContractTerms.SleepHint) || !asked.text.EndsWith("ending it later costs up to $45,000."))
                        Problem("kiosk load: the take question does not show how to sleep through the wait and what backing out costs, in full");
                    var shotAt = kiosk.transform.TransformPoint(new Vector3(-0.10f, 1.32f, -0.08f));
                    var toward = kiosk.transform.TransformDirection(Vector3.left);
                    toward.y = 0f;
                    yield return Capture("probe-take-question", shotAt + toward.normalized * 0.55f, shotAt);
                }
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
                // The bridge is a later contract: offered once one of the player's is at level 3.
                var lead = rt.State.Contracts.FirstOrDefault(c => c.Level >= 1 && c.Id != Economy.BridgeId);
                if (lead != null && rt.State.Contracts.All(c => c.Id != Economy.BridgeId))
                {
                    lead.Level = lead.Requested = 3;
                    var bridge = desk.Offers(place).FirstOrDefault(o => o.Id == Economy.BridgeId);
                    Line($"kiosk load: with {lead.Id} at level 3 the bridge offer {(bridge == null ? "is gone" : bridge.Needs == null ? "can be taken" : $"needs '{bridge.Needs}'")}");
                    if (bridge == null || bridge.Needs != null)
                        Problem("kiosk load: the bridge is not offered with a contract at level 3");
                    else
                    {
                        desk.Take(bridge, 1);
                        var b = rt.State.Contracts.FirstOrDefault(c => c.Id == Economy.BridgeId);
                        if (b != null)
                            b.Level = b.Requested = 1;
                    }
                    lead.Level = lead.Requested = 1;
                }
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
                var page = HarmonyLib.Traverse.Create(x.Detail).Field("lines").GetValue<KioskLines>();
                Line($"kiosk load: {name}: '{page?.Title.text}', info '{page?.Info.text}', rows: {string.Join("; ", x.Detail.Shown())}");
                if (entry.Id == Economy.BridgeId && !System.Text.RegularExpressions.Regex.IsMatch(page?.Info.text ?? "", @"^To move: \d+ to NEI / \d+ to WI$"))
                    Problem($"kiosk load: the bridge's info line is '{page?.Info.text}', not its cars to move each way");
                if (entry.Id == Economy.BridgeId && x.Detail.Shown().FirstOrDefault()?.StartsWith("Cars [") == true)
                    Timed("the bridge's Cars page", Ms(() => Line($"kiosk load: the bridge's Cars page:\n{desk.Cars(entry)?.Page}")));
                Timed($"back from {name} to the list", Ms(() => kiosk.HandleInput(InputAction.Cancel)));
                yield return null;
            }
            yield return Frames("with the list open, the cursor moving", 15f, true);
            // Every contract held: the list goes most cars to move first.
            kiosk.SetActiveDisplay(main);
            yield return null;
            kiosk.SetActiveDisplay(x.List);
            yield return null;
            var order = x.List.Entries.Where(e => !e.IsOffer && Runtime.RuntimeDesk.IsContract(e.Id)).ToList();
            Line($"kiosk load: held, in the list's order: {string.Join(", ", order.Select(e => $"{e.Id} {(e.ToMove.HasValue ? e.ToMove.Value.ToString() : "-")}"))}");
            for (var i = 1; i < order.Count; i++)
                if ((order[i].ToMove ?? 0) > (order[i - 1].ToMove ?? 0))
                    Problem($"kiosk load: the list puts {order[i - 1].Id} ({order[i - 1].ToMove}) before {order[i].Id} ({order[i].ToMove})");
            for (var i = 0; i < 3; i++)
            {
                kiosk.HandleInput(InputAction.Down);
                yield return null;
            }
            {
                var at = kiosk.transform.TransformPoint(new Vector3(-0.10f, 1.32f, -0.08f));
                yield return Capture("probe-cars-list-all", at + facing * 0.55f, at);
            }
            var listed = x.List.Entries.ToList();
            kiosk.SetActiveDisplay(main);
            Line($"kiosk load: {(slow.Count == 0 ? "nothing slow" : "slow: " + string.Join("; ", slow))}");
            if (slow.Count > 0)
                Problem("kiosk load: " + string.Join("; ", slow));
            Try("retry", () => RetryCheck(rt, listed));
            yield return new WaitForSeconds(2f);
        }

        /// <summary>
        /// The service again for the cars it had no room for (owner, 2026-11-10: "if an interchange
        /// is full it needs to attempt serving again later"; Contracts.Core ServiceRetry), after
        /// the service with every contract held, which the interchanges can't take whole:
        ///
        /// - what it was short of waits, with a time for the next try no more than 2.5 game hours
        ///   off; the kiosk's list has a "Waiting for room" row with the count, and its page;
        /// - a save holds the waiting orders and the time;
        /// - with one interchange track's cars set out for pickup, the retry takes them away,
        ///   places waiting cars on the track, bills them and writes jobs, and what is left waits
        ///   for the next try.
        ///
        /// Not checked here: the retry coming by itself when its time comes (Tick), and the next
        /// day's service dropping what still waits.
        /// </summary>
        private void RetryCheck(ContractRuntime rt, List<ContractView> listed)
        {
            var desk = KioskMenu.Desk;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var last = rt.LastService;
            var shortBefore = last?.Orders.Sum(r => r.Order.Count - r.Cars.Count) ?? -1;
            var before = rt.State.Waiting.Sum(w => w.Count);
            var clock = ContractRuntime.Clock;
            var (cars, page) = desk.Waiting();
            var row = listed.FirstOrDefault(e => e.Id == Runtime.RuntimeDesk.WaitingId);
            Line($"retry: the last service was {shortBefore} car(s) short; {before} wait ({string.Join(", ", rt.State.Waiting.GroupBy(w => w.Interchange).Select(g => $"{g.Sum(w => w.Count)} at {g.Key}"))}); " +
                 $"clock {clock:F4}, again at {rt.State.RetryAt:F4}; the list's row: {(row == null ? "none" : $"'{row.Short}' [{ContractsScreen.Value(row)}]")}; the page:\n{page}");
            if (before == 0)
            {
                Problem("retry: the service with every contract held left no car waiting, so the retry was not checked");
                return;
            }
            if (before > shortBefore)
                Problem($"retry: {before} car(s) wait, more than the {shortBefore} the service was short of");
            if (rt.State.RetryAt == null || clock == null || rt.State.RetryAt.Value - clock.Value > ServiceRetry.Hours / 24.0 + 1e-6)
                Problem("retry: no time for the next try within 2.5 game hours");
            if (cars != before || page == null || !page.StartsWith("New cars wait off the map"))
                Problem("retry: the desk does not say how many cars wait");
            if (row == null || ContractsScreen.Value(row) != before.ToString())
                Problem("retry: the kiosk's list has no Waiting for room row with the count");

            var data = SaveGameManager.MakeEmptySave();
            rt.State.Write(data);
            var back = ContractState.Read(data, out _);
            Line($"retry: the save reads back {back?.Waiting.Sum(w => w.Count) ?? -1} waiting, again at {back?.RetryAt:F4}");
            if (back == null || back.Waiting.Sum(w => w.Count) != before || back.RetryAt != rt.State.RetryAt)
                Problem("retry: the save does not hold the waiting cars and the next try's time");

            // Room: one interchange track's cars, all contract cars, set out for pickup (their jobs withdrawn).
            var (freed, standing) = MakeRoom(rt, rt.State.Waiting.Select(w => w.Interchange).Distinct());
            if (freed == null)
            {
                Problem("retry: no interchange track with only contract cars on it to make room on");
                return;
            }
            var summary = rt.Retry();
            var run = rt.LastService;
            var after = rt.State.Waiting.Sum(w => w.Count);
            var fresh = run?.Orders.SelectMany(r => r.Cars).Where(c => c != null).ToList() ?? new List<TrainCar>();
            var unbilled = fresh.Count(c => rt.State.Of(c.logicCar.carGuid) == null);
            var jobless = fresh.Count(c => jobs.GetJobOfCar(c.logicCar) == null);
            var onFreed = fresh.Count(c => c.logicCar.FrontBogieTrack == freed);
            Line($"retry: {standing.Count} car(s) on {freed.ID.FullDisplayID} set out; {summary}");
            Line($"retry: {fresh.Count} new car(s), {onFreed} on {freed.ID.FullDisplayID}, {unbilled} without a bill, {jobless} without a job; " +
                 $"{after} still wait, again at {rt.State.RetryAt:F4}; cuts: {string.Join(", ", run?.Cuts ?? new List<string>())}");
            if (run == null || !run.Retry || run.Taken < standing.Count)
                Problem("retry: the cars set out for pickup were not taken away");
            if (fresh.Count == 0 || onFreed == 0)
                Problem("retry: no waiting car was placed on the track made free");
            if (after != before - fresh.Count)
                Problem($"retry: {after} car(s) wait after {fresh.Count} of {before} were placed");
            if (unbilled > 0 || (fresh.Count > 0 && run.Made == 0))
                Problem("retry: the new cars were not all billed, or got no job");
            if (after > 0 && rt.State.RetryAt == null)
                Problem("retry: cars still wait with no next try");

            // A cut that throws (the probe's fault) does not end the service: the cars it could not
            // place wait, the jobs are written and the summary comes, saying a part failed.
            var (again, _) = MakeRoom(rt, rt.State.Waiting.Select(w => w.Interchange).Distinct());
            if (again == null)
            {
                Problem("fault: no interchange track to make room on");
                return;
            }
            var waitingBefore = rt.State.Waiting.Sum(w => w.Count);
            ContractRuntime.FailNextCut = true;
            var faulted = rt.Retry();
            var faultRun = rt.LastService;
            Line($"fault: a cut that throws, on {again.ID.FullDisplayID}: {faulted}");
            Line($"fault: the service ended {!rt.Servicing}, {faultRun?.Failed} part(s) failed, {faultRun?.Placed} placed; {rt.State.Waiting.Sum(w => w.Count)} of {waitingBefore} still wait, again at {rt.State.RetryAt:F4}");
            if (ContractRuntime.FailNextCut || rt.Servicing || faultRun == null || !faultRun.Retry || faultRun.Failed < 1 || faulted == null || !faulted.Contains("failed"))
                Problem("fault: a cut that throws was not survived with a summary that says so");
            if (rt.State.Waiting.Sum(w => w.Count) == 0 || rt.State.RetryAt == null)
                Problem("fault: the cars the failed cut left were not kept waiting for the next try");
            ContractRuntime.FailNextCut = false;
        }

        /// <summary>Makes room at one of the interchanges: a track with only contract cars on it, their jobs withdrawn and the cars set out for pickup. The track and its cars, or nulls.</summary>
        private static (Track Track, List<Car> Cars) MakeRoom(ContractRuntime rt, IEnumerable<string> interchanges)
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            foreach (var name in interchanges)
                foreach (var track in InterchangeYards.Tracks(name))
                {
                    var on = track.GetCarsFullyOnTrack().Concat(track.GetCarsPartiallyOnTrack()).Distinct().ToList();
                    if (on.Count == 0 || on.Any(c => rt.State.Of(c.carGuid) == null))
                        continue;
                    foreach (var job in on.Select(c => jobs.GetJobOfCar(c)).Where(j => j != null).Distinct().ToList())
                        ContractRuntime.Withdraw(job);
                    foreach (var c in on)
                        rt.State.Of(c.carGuid).Stage = Stage.OffMap;
                    return (track, on);
                }
            return (null, null);
        }

        /// <summary>
        /// A player's saved career, loaded on this build (probe.txt "save"; run_spike.py --career):
        /// nothing lost and nothing thrown (RF audit, 2026-11-06: the owner's Railroad 2 career,
        /// last played with 23c6bc0, on the release with the interchanges as stations and their
        /// tracks' new IDs). What loads: the contracts, every waybill's car, the jobs of contract
        /// cars (their papers: shunting moves, and interchange jobs, written before or after),
        /// the cars being worked on their industry's track; then the next 06:00 service, after
        /// which no car being worked has gone idle and none is lost.
        /// </summary>
        private IEnumerator SaveRun()
        {
            log = new StreamWriter(Path.Combine(folder, "probe.log"), false);
            Line($"probe (save) {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            for (var waited = 0f; !(ContractRuntime.Active && AStartGameData.carsAndJobsLoadingFinished) && waited < 240f; waited += 1f)
                yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(10f);
            var rt = ContractRuntime.Instance;
            if (rt == null || !ContractRuntime.Active)
            {
                Problem("the runtime never started");
                yield return Finish();
                yield break;
            }
            foreach (var n in rt.Notes)
                Line("note: " + n);
            var loadErrors = errors.Where(e => e.Contains("Skipping load") || e.Contains("Couldn't find corresponding") || e.StartsWith("Exception")).ToList();
            foreach (var e in loadErrors)
                Problem("save: loading: " + e);
            HashSet<string> worked = null;
            Try("save: what loaded", () => worked = SaveLoaded(rt, "loaded"));
            yield return new WaitForSeconds(2f);
            Try("save: the next service", () =>
            {
                Line($"save: service day {rt.LastServiceDay + 1}: {rt.Service(rt.LastServiceDay + 1)}");
            });
            yield return new WaitForSeconds(8f);
            Try("save: after the service", () =>
            {
                SaveLoaded(rt, "after the service");
                // A car with a stage but no job, not being worked and not waiting for room, goes back
                // to idle at a service (moved off its industry track, say): none should here.
                var idled = rt.LastService?.Idled ?? -1;
                if (idled != 0)
                    Problem($"save: the service sent {idled} car(s) back to idle");
                var stillWorked = rt.State.Waybills.Count(w => worked != null && worked.Contains(w.Guid) && ContractRuntime.Worked(w));
                Line($"save: of the {worked?.Count ?? 0} car(s) being worked before the service, {stillWorked} still are (the others finished)");
            });
            yield return Finish();
        }

        /// <summary>What the save holds now; the guids of the cars being worked on their industry's track.</summary>
        private HashSet<string> SaveLoaded(ContractRuntime rt, string when)
        {
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var st = rt.State;
            Line($"save ({when}): {st.Contracts.Count} contract(s): {string.Join(", ", st.Contracts.Select(c => $"{c.Id} L{c.Level}"))}");
            Line($"save ({when}): {st.Waybills.Count} waybill(s): {string.Join(", ", st.Waybills.GroupBy(w => w.Stage).Select(g => $"{g.Key} {g.Count()}"))}");
            var missing = st.Waybills.Where(w => w.Stage != Stage.OffMap && registry.GetTrainCarByCarGuid(w.Guid) == null && !PersistentJobs.IsSuspended(w.Guid)).ToList();
            if (missing.Count > 0)
                Problem($"save ({when}): {missing.Count} car(s) not in the world: {string.Join(", ", missing.Take(12).Select(w => $"{w.Guid.Substring(0, 8)} {w.Stage} to {w.Next}"))}");
            var cars = st.Waybills.Select(w => (W: w, Car: registry.GetTrainCarByCarGuid(w.Guid))).Where(x => x.Car != null).ToList();
            var held = cars.Select(x => jobs.GetJobOfCar(x.Car.logicCar)).Where(j => j != null).Distinct().ToList();
            Line($"save ({when}): {cars.Count} car(s) in the world, {cars.Count(x => InterchangeYards.IsInterchangeTrack(x.Car.logicCar.FrontBogieTrack))} on interchange tracks; {held.Count} job(s) of theirs");
            foreach (var job in held)
            {
                var start = job.tasks.FirstOrDefault()?.GetTaskData().startTrack;
                var end = ContractRuntime.JobDestination(job);
                var holder = StationController.allStations.FirstOrDefault(s => s?.logicStation != null
                    && (s.logicStation.availableJobs.Contains(job) || s.logicStation.takenJobs.Contains(job)))?.stationInfo.YardID;
                Line($"save ({when}): job {job.ID} {job.jobType} {job.State} of {holder ?? "no station"}, {start?.ID.FullDisplayID ?? "?"} -> {end?.ID.FullDisplayID ?? "?"}");
                if (start == null || end == null)
                    Problem($"save ({when}): job {job.ID} has no start or end track");
                else if (InterchangeYards.IsInterchangeTrack(start) || InterchangeYards.IsInterchangeTrack(end))
                    InterchangePapersCheck(job, start, end, writtenBefore: true);
                else if (start.ID.yardId == end.ID.yardId)
                    Papers(job);
            }
            var worked = new HashSet<string>();
            var off = new List<string>();
            foreach (var (w, car) in cars.Where(x => ContractRuntime.Worked(x.W)))
            {
                if (rt.OnIndustryTrack(car, w))
                    worked.Add(w.Guid);
                else
                    off.Add($"{car.ID} {w.Stage} at {w.Next} on {car.logicCar.FrontBogieTrack?.ID?.FullDisplayID ?? "no track"}");
            }
            Line($"save ({when}): {worked.Count} car(s) being worked on their industry's track" +
                 $" ({string.Join(", ", cars.Where(x => worked.Contains(x.W.Guid)).GroupBy(x => x.Car.logicCar.FrontBogieTrack?.ID?.FullDisplayID).Select(g => $"{g.Key} {g.Count()}"))})");
            if (off.Count > 0)
                Problem($"save ({when}): {off.Count} car(s) being worked off their industry's track: {string.Join("; ", off.Take(12))}");
            var jobless = rt.Jobless();
            Line($"save ({when}): {jobless.Count} car(s) without a job: {string.Join(", ", jobless.GroupBy(c => c.Reason).Select(g => $"{g.Key} {g.Count()}"))}");
            return worked;
        }

        private IEnumerator Finish()
        {
            foreach (var e in errors.Distinct().Take(80))
                Line("log: " + e);
            // A job ID registered twice, or let go unregistered (StationMoves forces its IDs).
            var ids = errors.Count(e => e.Contains("jobId: "));
            if (ids > 0)
                Problem($"{ids} job ID error(s) logged");
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
