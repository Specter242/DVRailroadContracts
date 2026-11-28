using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Core.Sim;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using DV.WeatherSystem;
using UnityEngine;
using static System.FormattableString;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The contracts at work in the game (design phase 3), on the host.
    ///
    /// - **Days.** A contract day is a game day on the world clock (D6). At midnight, levels
    ///   asked for take effect, and a contract taken that day starts. At 06:00 the interchange
    ///   service runs (design, "Interchange service").
    /// - **The service.** Cars left on an interchange track to go off the map are taken away;
    ///   contract cars whose job ended without finishing (abandoned or expired) go back to
    ///   idle. The core's planner plans the day for the contracts held (Industries/IndustryPlanner):
    ///   bills for idle contract cars, and orders of cars placed now on free interchange tracks,
    ///   empty or loaded. The placement and the jobs go a step a frame (BeginService); a save
    ///   finishes them first.
    /// - **Work is stock jobs** (design, "Waybills and work"), built with Persistent Jobs'
    ///   generators for the cars' next leg: an empty haul to a shipper, a transport to the
    ///   receiver's station or to an interchange track. When a chain of contract cars completes,
    ///   the mod writes the next leg at once and takes the cars out of the finished chain, so
    ///   neither the game nor Persistent Jobs sees them jobless.
    /// - **Industry work** (design, "Industry work"): at its industry's station a car gets a short
    ///   job to spot it on the industry's track (StationMoves); the industry then loads or unloads
    ///   it over time, a car at a time per line at its contract's rate (Contracts.Core
    ///   IndustryWork), and it goes out at once, joining an outbound job not yet taken.
    /// - **Standing.** A contract car's bill is open from when it is billed for a move until the
    ///   move is done (an empty until spotted at its shipper, a load until spotted at its receiver
    ///   or gone at an interchange); the time an industry works a car counts for no bill.
    ///   Each midnight scores every contract's day from its bills' ages, and a raise needs
    ///   consistent good days (Contracts.Core ContractStanding).
    /// - **Pay on top** (D7, ContractPay): quick delivery earns a bonus at levels 2–5; lowering
    ///   or ending a contract, or three poor days, costs a penalty.
    /// - **Saves.** The state goes under its own key on every save.
    /// </summary>
    internal sealed partial class ContractRuntime : MonoBehaviour
    {
        public static ContractRuntime Instance { get; private set; }

        public ContractState State;
        public Economy Economy;
        public readonly List<string> Notes = new List<string>();
        public int LastServiceDay = -1;

        private bool newCareer, setUp;
        private float timer, passengerTimer, spotTimer;
        private SplitMix64 jobRng;
        private readonly HashSet<string> removing = new HashSet<string>(StringComparer.Ordinal);
        // Cars waiting for room on their industry track, said once in the log.
        private readonly HashSet<string> waitingSaid = new HashSet<string>(StringComparer.Ordinal);

        public static bool Active => Instance != null && Instance.setUp;

        // ------------------------------------------------------------ life

        public static void OnWorldLoaded()
        {
            try
            {
                var role = Kiosk.Multiplayer.Now;
                if (role == Kiosk.Multiplayer.Role.Client)
                {
                    Main.Log("contracts: a Multiplayer client; the host runs the contracts");
                    return;
                }
                if (role == Kiosk.Multiplayer.Role.Unknown)
                {
                    Main.Error("contracts: Multiplayer is loaded and its API can't be read; contracts stay off, so a player who joined doesn't run them too");
                    return;
                }
                Main.Log($"contracts: Multiplayer: {Kiosk.Multiplayer.Describe()}");
                var state = ContractState.Read(ContractState.CurrentSave, out var unreadable);
                if (unreadable)
                    return;
                var go = new GameObject("[contracts runtime]");
                var rt = go.AddComponent<ContractRuntime>();
                rt.State = state ?? new ContractState();
                // A career this build starts with never got the wrecks' licences free (#45).
                if (state == null)
                    rt.State.WreckLicencesChecked = true;
#if DEVTOOLS
                if (RestorationWrecks.SimulateOldBuild)
                    rt.State.WreckLicencesChecked = false;
#endif
                // A new career, or one whose starter could not be delivered at its first load.
                rt.newCareer = state == null ? IsNewCareer() : state.StarterDue && !state.StarterGiven;
                Instance = rt;
                CareerRules.IsContractCar = g => g != null && Instance != null && Instance.State.Of(g) != null;
                var save = SingletonBehaviour<SaveGameManager>.Instance;
                if (save != null)
                    save.OnInternalDataUpdate += rt.OnSaving;
                var spawner = SingletonBehaviour<CarSpawner>.Instance;
                if (spawner != null)
                    spawner.CarAboutToBeDeleted += rt.OnCarDeleted;
                Main.Log($"contracts: runtime started ({(state == null ? "new state" : $"{state.Contracts.Count} contracts, {state.Waybills.Count} cars")}, new career {rt.newCareer})");
            }
            catch (Exception e)
            {
                Main.Error($"contracts: starting the runtime failed; contracts stay off: {e}");
            }
        }

#if DEVTOOLS
        private static bool OldBuild => RestorationWrecks.SimulateOldBuild;
#else
        private const bool OldBuild = false;
#endif

        private static bool IsNewCareer()
        {
            var start = FindObjectOfType<AStartGameData>();
            return start is StartGameData_NewCareer;
        }

        public static void OnUnload()
        {
            CareerRules.IsContractCar = g => false;
            PassengerRuns.Clear();
            Kiosk.InfoPages.Clear();
            InterchangeBooklets.Clear();
        }

        private void OnDestroy()
        {
            ClearStationTrackCache();
            var save = SingletonBehaviour<SaveGameManager>.Instance;
            if (save != null)
                save.OnInternalDataUpdate -= OnSaving;
            var spawner = SingletonBehaviour<CarSpawner>.Instance;
            if (spawner != null)
                spawner.CarAboutToBeDeleted -= OnCarDeleted;
            if (Instance == this)
            {
                Instance = null;
                CareerRules.IsContractCar = g => false;
            }
            OnUnload();
        }

        private void OnSaving(SaveGameData data)
        {
            try
            {
                State.Write(data);
            }
            catch (Exception e)
            {
                Main.Error($"contracts: saving failed; the save goes on without them: {e.Message}");
            }
        }

        private void OnCarDeleted(TrainCar car)
        {
            // The world unloading deletes every car; the saved state keeps them.
            if (UnloadWatcher.isUnloading)
                return;
            var guid = car != null && car.logicCar != null ? car.logicCar.carGuid : null;
            if (guid == null || State.Of(guid) == null)
                return;
            // Persistent Jobs suspending it (FarCarGuard keeps contract cars out; this is in case
            // one gets through): it comes back, so its bill stays.
            if (PersistentJobs.Suspending(car))
            {
                Main.Warn($"contracts: contract car {car.ID} suspended by Persistent Jobs; its bill kept for when it comes back");
                return;
            }
            if (!removing.Remove(guid))
                Main.Warn($"contracts: contract car {car.ID} was deleted by the game");
            State.Remove(guid);
        }

        private void Update()
        {
            if (service != null)
                StepService();
            timer -= Time.unscaledDeltaTime;
            if (timer > 0f)
                return;
            timer = 2f;
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Main.Error($"contracts: {e}");
                timer = 30f;
            }
        }

        public static DateTime? Now => SingletonBehaviour<WeatherDriver>.Instance?.manager?.DateTime;

        public static int DayOf(DateTime t) => (int)(t.Date - new DateTime(2000, 1, 1)).TotalDays;

        /// <summary>The world clock in contract days: the day number plus the time of day (midnight of day d is d).</summary>
        public static double? Clock => Now is DateTime t ? DayOf(t) + t.TimeOfDay.TotalDays : (double?)null;

        private void Tick()
        {
            if (!AStartGameData.carsAndJobsLoadingFinished)
                return;
            if (!setUp)
            {
                var flagged = CareerRules.FlagAllStations();
                Notes.Add($"setup: {flagged} station(s) flagged as spawned");
                Economy = Economy.Build(Notes);
                if (newCareer && !State.StarterGiven)
                {
                    State.StarterGiven = CareerRules.GiveStarter(Notes);
                    State.StarterDue = !State.StarterGiven;
                }
                CheckBindings();
                LicenseRules.GiveJobTypes(Notes);
                LicenseRules.LicenseOwned(SingletonBehaviour<TrainCarRegistry>.Instance?.logicCarToTrainCar.Values, Notes);
                Notes.Add($"setup: {VehicleSales.Protect()} owned vehicle(s) kept from the comms radio's Clear");
                var withdrawn = WithdrawUncarried(LicenseRules.Carrying());
                if (withdrawn > 0)
                    Notes.Add($"licenses: {withdrawn} bill(s) for cargo without its licenses withdrawn");
                setUp = true;
                foreach (var n in Notes)
                    Main.Log("contracts: " + n);
                LogJobless("after load");
            }
            // Once, in a career played before this build: the licences it got free with the wrecks (#45).
            if (!State.WreckLicencesChecked && (SingletonBehaviour<StartingItemsController>.Instance?.itemsLoaded ?? true) && !OldBuild)
            {
                State.WreckLicencesChecked = true;
                try
                {
                    foreach (var n in RestorationWrecks.TakeBackLicences())
                        Main.Log("contracts: " + n);
                }
                catch (Exception e)
                {
                    Main.Error($"contracts: checking the wrecks' licences failed; none were taken back: {e.Message}");
                }
            }
            // The day moves on once the service under way is done (a second or so).
            if (service != null)
                return;
            var now = Now;
            if (now == null)
                return;
            var day = DayOf(now.Value);
            // Each part on its own: one that fails says so and the others still run (a fault in the
            // industries' work used to stop the cars being spotted and the passenger trains too).
            Part("the day's roll", () =>
            {
                if (Settled(day))
                    RollTo(day);
            });
            Part("what is owed", () => ContractPay.Collect(State));
            // On hold: the clock keeps the day, and nothing else moves (CheckBindings).
            if (OnHold != null)
                return;
            Part("the interchange service", () =>
            {
                if (now.Value.Hour >= 6 && State.ServiceDay < day)
                {
                    // The day is the service's whether it comes through or not: a day planned twice doubles its cars.
                    State.ServiceDay = day;
                    BeginService(day);
                }
                // The service again, for the cars it had no room for (Contracts.Core ServiceRetry).
                else if (ServiceRetry.Due(State.Waiting.Sum(w => w.Count), State.RetryAt, Clock))
                    BeginRetry();
            });
            Part("the industries' work", () => WorkIndustries(Clock ?? 0.0));
            spotTimer -= 2f;
            if (spotTimer <= 0f)
            {
                spotTimer = 15f;
                Part("spotting the cars that wait", SpotWaiting);
            }
            passengerTimer -= 2f;
            if (passengerTimer <= 0f)
            {
                passengerTimer = 10f;
                Part("the passenger trains", () => PassengerRuns.Run(State));
            }
        }

        // The world's day as last read, and for how many ticks running.
        private int dayRead = int.MinValue, dayReadTicks;

        /// <summary>
        /// Whether the world's day can be acted on: the contract day or the next at once; any
        /// other (days skipped, or the clock set back) only once it has read the same for ten
        /// seconds, so a date the game shows for a moment while it sets up is not taken for a
        /// jump, which can't be undone.
        /// </summary>
        private bool Settled(int day)
        {
            if (State.Day < 0 || day == State.Day || day == State.Day + 1)
            {
                dayRead = day;
                dayReadTicks = 0;
                return true;
            }
            dayReadTicks = day == dayRead ? dayReadTicks + 1 : 1;
            dayRead = day;
            return dayReadTicks >= 5;
        }

        // A part of the tick that failed, said once a minute at most.
        private readonly Dictionary<string, float> partFailed = new Dictionary<string, float>(StringComparer.Ordinal);

        private void Part(string what, Action part)
        {
            try
            {
                part();
            }
            catch (Exception e)
            {
                var at = Time.unscaledTime;
                if (partFailed.TryGetValue(what, out var last) && at - last < 60f)
                    return;
                partFailed[what] = at;
                Main.Error($"contracts: {what} failed; the rest goes on: {e}");
            }
        }

        /// <summary>
        /// Why the contracts are on hold, or null. Without Persistent Jobs' job generators and
        /// spawn flags no job can be written, and bills that can't be moved used to age into
        /// penalties with nothing said but a line in the log. On hold no service comes, nothing is
        /// scored or charged, the open bills keep their ages (<see cref="RollTo"/>), and the kiosk
        /// says why.
        /// </summary>
        public string OnHold { get; internal set; }

        private void CheckBindings()
        {
            var missing = PersistentJobs.CheckCritical();
            if (missing.Count > 0)
            {
                OnHold = "Persistent Jobs has changed: " + string.Join(", ", missing) + " not found";
                Main.Error($"contracts: on hold: {OnHold}. No cars come and nothing is scored or charged until Railroad Contracts is updated for this Persistent Jobs (or Persistent Jobs 4.0 is put back)");
            }
            var far = PersistentJobs.CheckFarCars();
            if (far != null)
                Main.Warn($"contracts: Persistent Jobs' far-car suspension has changed ({far} not found), so contract cars far from the player are not kept out of it; their bills are kept while they are away");
            Notes.Add(OnHold == null ? "setup: Persistent Jobs' generators and flags found" : "setup: ON HOLD: " + OnHold);
        }

        /// <summary>
        /// Brings the contract day to the world's (Contracts.Core DayRoll): a day on, its midnight;
        /// several at once, the days between skipped unscored with the open bills moved on by
        /// them; the clock set back, the bills moved back with it. On hold a midnight scores
        /// nothing and moves the bills on a day.
        /// </summary>
        internal void RollTo(int day)
        {
            if (State.Day < 0)
                State.Day = day;
            var (skipped, back) = DayRoll.Plan(State.Day, day);
            if (back > 0)
            {
                Main.Warn($"contracts: the world's clock went back {back} day(s) (day {State.Day} to {day}); the contracts follow it, their open bills keeping their ages");
                MoveBills(-back);
                // No service on account of the clock: with 06:00 past, today has had its own as far
                // as anyone can tell, and the next is tomorrow's; before 06:00 it comes at 06:00.
                State.ServiceDay = (Now?.Hour ?? 6) >= 6 ? day : day - 1;
                State.Day = day;
                return;
            }
            if (skipped > 0)
            {
                Main.Warn($"contracts: {skipped} day(s) went by with the contracts off (day {State.Day} to {day}); they are not scored, and the open bills are moved on by them");
                MoveBills(skipped);
                State.Day += skipped;
            }
            while (State.Day < day)
            {
                State.Day++;
                if (OnHold != null)
                    MoveBills(1);
                else
                    Midnight();
            }
        }

        /// <summary>Moves every time the standing is reckoned from by whole days: the open bills, the day each contract was taken, the passenger trains due and running, and the next retry.</summary>
        private void MoveBills(int days)
        {
            foreach (var w in State.Waybills)
                w.Opened = DayRoll.Moved(w.Opened, days);
            foreach (var c in State.Contracts)
            {
                c.SinceDay += days;
                for (var i = 0; i < c.Due.Count; i++)
                    c.Due[i] += days;
                foreach (var r in c.Running)
                    r.Opened += days;
            }
            State.RetryAt = DayRoll.Moved(State.RetryAt, days);
        }

        /// <summary>
        /// The day just ended is scored for each running contract; then asked-for levels take
        /// effect (a raise only with the standing for it), and contracts taken today start. Three
        /// poor days force a contract down two levels; a drop costs the penalty, and a contract
        /// dropped to nothing ends (D7, ContractTerms).
        /// </summary>
        internal void Midnight()
        {
            var midnight = (double)State.Day;
            foreach (var c in State.Contracts.Where(c => c.Level >= 1))
            {
                var open = PassengerService.CityOf(c.Id) != null ? PassengerRuns.OpenAges(c, midnight)
                    : State.Waybills.Where(w => w.Opened.HasValue && w.Opened.Value < midnight && Involves(w, c))
                        .Select(w => midnight - w.Opened.Value).ToList();
                var score = ContractStanding.DayScore(open, c.Closed);
                var closed = c.Closed.Count;
                c.Closed.Clear();
                if (score == null)
                    continue;
                ContractStanding.Record(c.History, score.Value);
                Main.Log(Invariant($"contracts: {c.Id} scored {score.Value * 100:0}% on day {State.Day - 1} ({open.Count} open, {closed} closed); ") +
                         Invariant($"{ContractStanding.Average(c.History) * 100:0}% over {c.History.Count} day(s)"));
            }
            var changes = new List<string>();
            var refused = new List<string>();
            var charged = 0.0;
            foreach (var c in State.Contracts.ToList())
            {
                // Midnight makes every change final, and only midnight charges for one (Contracts.Core
                // ContractTerms.AtMidnight; owner, 2026-11-10): the level asked for, the end asked
                // for, or the two levels down that three poor days force (D7).
                var change = ContractTerms.AtMidnight(c.Level, c.Requested, c.Ending, c.History);
                var target = change.Target;
                var reason = change.Forced ? " (low performance)" : "";
                var asked = c.Ending;
                c.Ending = false;
                // A raise asked for and not given: the kiosk says so, with the drops (captain75tur, Nexus,
                // 2026-11-10; until 0.1.11 only the log did, and the list went from "L1>2" back to "L1").
                if (change.RaiseRefused != null)
                {
                    Main.Log($"contracts: {c.Id} stays at level {c.Level}, not raised: {change.RaiseRefused}");
                    refused.Add(ContractStanding.RaiseRefused(c.Id, c.Level, c.History));
                }
                if (target == c.Level)
                {
                    c.Requested = c.Level;
                    continue;
                }
                // Given up before its first midnight (asked to end the day it was taken), it costs nothing.
                var free = asked && target < 1 && ContractTerms.FreeToGiveUp(c.SinceDay, State.Day - 1);
                var penalty = free ? 0.0 : ContractTerms.Penalty(c.Level, target, c.History.Count);
                if (penalty > 0.0)
                {
                    ContractPay.Charge(State, penalty, $"{c.Id} from level {c.Level} to {target}{reason}");
                    charged += penalty;
                }
                if (target < 1)
                {
                    State.Contracts.Remove(c);
                    var setOut = SetOutEnded(c.Id);
                    Main.Log($"contracts: {c.Id} ended at midnight{reason}{(free ? ", free on its first day" : "")}; " +
                             $"{setOut} of its car(s) on interchange tracks set out for pickup, the rest finish their legs and drain");
                    changes.Add($"{c.Id} ended");
                    continue;
                }
                Main.Log($"contracts: {c.Id} from level {c.Level} to {target} at midnight{reason}");
                if (target < c.Level)
                    changes.Add($"{c.Id} L{c.Level}>{target}");
                c.Level = c.Requested = target;
                c.History.Clear();
                c.Closed.Clear();
            }
            // The kiosk says what dropped and what was not raised, the next time its Contracts list
            // opens, on its one line: by name if that fits, else how many.
            if (ContractTerms.MidnightNotice(changes, charged, refused, Kiosk.KioskMenu.InfoLineChars) is string notice)
                Kiosk.KioskMenu.Tell(notice);
        }

        /// <summary>
        /// An ended contract's cars standing on interchange tracks with no job taken: nobody will
        /// move them now, and they kept the track from new cuts (a cut goes only on a track with no
        /// car on it). Their jobs are withdrawn and they are set out for the pickup; the next
        /// service, or its retry, takes them away. A job the player has taken finishes, and a job
        /// shared with another contract's cars stays. Returns how many were set out.
        /// </summary>
        private int SetOutEnded(string contract)
        {
            var registry = SingletonBehaviour<TrainCarRegistry>.Instance;
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            if (registry == null || jobs == null)
                return 0;
            var n = 0;
            var withdrawn = new HashSet<Job>();
            foreach (var w in State.Waybills.Where(w => w.Contract == contract && w.Stage != Stage.OffMap).ToList())
            {
                var car = registry.GetTrainCarByCarGuid(w.Guid);
                if (car == null || car.logicCar == null || !InterchangeYards.IsInterchangeTrack(car.logicCar.FrontBogieTrack))
                    continue;
                var job = jobs.GetJobOfCar(car.logicCar);
                if (job != null)
                {
                    if (job.State != JobState.Available)
                        continue;
                    if (!withdrawn.Contains(job) && CarsOf(job.tasks).Any(c => State.Of(c?.carGuid)?.Contract != contract))
                        continue;
                    if (withdrawn.Add(job))
                        Withdraw(job);
                }
                w.Opened = null;
                w.Stage = Stage.OffMap;
                w.At = PlaceOf(car, w);
                n++;
            }
            return n;
        }

        /// <summary>
        /// A career's first contract starts at once (RuntimeDesk.StartsAtOnce): the service comes
        /// now for it, as the day's own. A day whose 06:00 has not come yet gets no second service;
        /// the next is tomorrow's.
        /// </summary>
        internal void OpenFirstContract()
        {
            var now = Now;
            if (now == null)
                return;
            var day = DayOf(now.Value);
            State.ServiceDay = day;
            if (BeginService(day))
                Main.Log($"contracts: the service comes now for the career's first contract (day {day})");
        }

        /// <summary>Whether a car's bill counts for a contract: the one it is billed to, or the industry it goes to.</summary>
        private static bool Involves(Waybill w, HeldContract c) => c.Id == w.Contract || c.Id == w.Next;

        /// <summary>A bill opens when a car is billed for a move, unless one is still open for it.</summary>
        private static void Open(Waybill w)
        {
            if (w.Opened == null)
                w.Opened = Clock;
        }

        /// <summary>The move is done: the bill's final age counts on the next midnight's score of the contracts it involves.</summary>
        private void Close(Waybill w)
        {
            var now = Clock;
            if (w.Opened.HasValue && now.HasValue)
            {
                var age = Math.Round(Math.Max(0.0, now.Value - w.Opened.Value), 4);
                foreach (var c in State.Contracts.Where(c => Involves(w, c)))
                    c.Closed.Add(age);
            }
            w.Opened = null;
        }
    }
}
