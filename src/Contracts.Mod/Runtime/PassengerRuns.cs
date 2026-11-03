using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using DV.Logic.Job;
using DV.Utils;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The passenger contracts' day (Passengers; Contracts.Core PassengerService), a regular daily
    /// service (owner, 2026-11-03: "thats dumb to only have passengers if coaches are sitting at
    /// the station. it should be regular daily service"):
    ///
    /// - Each service writes the day's timetable: the level's trains, from 06:00 to 22:00.
    /// - When a train's time comes, its job is posted at the city with the player's coaches: an
    ///   idle train of them already on one of the city's platforms, else elsewhere at the city,
    ///   else the nearest anywhere. The job starts on that platform, or a free one; the player
    ///   brings the coaches there to board. Without idle coaches the train waits, and its bill
    ///   ages, until some are free.
    /// - A train's bill closes when its job is done.
    /// </summary>
    internal static class PassengerRuns
    {
        private const float RetrySeconds = 60f;
        private static readonly Dictionary<string, float> noRouteUntil = new Dictionary<string, float>(StringComparer.Ordinal);

        public static IEnumerable<HeldContract> Held(ContractState state) => state.Contracts.Where(c => PassengerService.CityOf(c.Id) != null);

        private static HashSet<string> LiveJobIds()
        {
            var jobs = SingletonBehaviour<JobsManager>.Instance;
            var all = jobs == null ? null : Traverse.Create(jobs).Field("allJobs").GetValue<List<Job>>();
            return new HashSet<string>((all ?? new List<Job>()).Where(j => j != null).Select(j => j.ID), StringComparer.Ordinal);
        }

        /// <summary>
        /// At the service: a train whose job is gone unfinished (abandoned, expired) is due again;
        /// then each running contract's timetable for the day is written.
        /// </summary>
        public static string Service(ContractState state, int day)
        {
            var live = LiveJobIds();
            int made = 0, again = 0, dropped = 0;
            foreach (var c in Held(state))
            {
                foreach (var r in c.Running.Where(r => !live.Contains(r.Job)).ToList())
                {
                    c.Running.Remove(r);
                    c.Due.Add(r.Opened);
                    again++;
                }
                c.Due.Sort();
                if (c.Level < 1)
                    continue;
                made += PassengerService.Trains(c.Level);
                dropped += PassengerService.MakeDue(c.Due, c.Level, day, c.Closed);
            }
            return made + again + dropped == 0 ? null : $"passengers: {made} train(s) timetabled, {again} due again, {dropped} dropped unrun";
        }

        /// <summary>A train's job is done: its bill closes at its age.</summary>
        public static void Done(ContractState state, Job job, double? now)
        {
            foreach (var c in Held(state))
            {
                var r = c.Running.FirstOrDefault(d => d.Job == job.ID);
                if (r == null)
                    continue;
                c.Running.Remove(r);
                if (now.HasValue)
                    c.Closed.Add(Math.Round(Math.Max(0.0, now.Value - r.Opened), 4));
                Main.Log($"passengers: {c.Id}'s train {job.ID} done");
            }
        }

        /// <summary>The ages at midnight of a passenger contract's trains still open (due, or running).</summary>
        public static List<double> OpenAges(HeldContract c, double midnight) =>
            c.Due.Concat(c.Running.Select(r => r.Opened)).Where(o => o < midnight).Select(o => midnight - o).ToList();

        /// <summary>Trains whose time has come get their jobs, oldest first, with the player's idle coaches.</summary>
        public static void Run(ContractState state)
        {
            var now = ContractRuntime.Clock;
            if (!Passengers.Available || now == null)
                return;
            var express = LicenseRules.HeldIds().Contains(Passengers.ExpressLicense);
            var trains = Held(state).Where(c => c.Level >= 1)
                .SelectMany(c => c.Due.Where(t => t <= now.Value).Select(t => (Contract: c, Time: t)))
                .OrderBy(t => t.Time).ToList();
            if (trains.Count == 0)
                return;
            var idle = Passengers.IdleConsists();
            foreach (var (c, time) in trains)
            {
                if (idle.Count == 0)
                    return;
                var city = PassengerService.CityOf(c.Id);
                var station = Economy.Station(city);
                if (station == null)
                    continue;
                var terminus = Passengers.Terminus(city);
                var choices = idle.Where(x => !noRouteUntil.TryGetValue(Key(x), out var until) || Time.realtimeSinceStartup >= until)
                    .OrderBy(x => terminus.Contains(Passengers.TrackOf(x)) ? 0 : Passengers.TrackOf(x)?.ID?.yardId == city ? 1 : 2)
                    .ThenBy(x => (x[0].transform.position - station.transform.position).sqrMagnitude)
                    .ToList();
                foreach (var coaches in choices)
                {
                    var start = terminus.Contains(Passengers.TrackOf(coaches)) ? Passengers.TrackOf(coaches) : Passengers.FreePlatform(city, coaches);
                    if (start == null)
                        continue;
                    var (job, type) = Post(city, start, coaches, express && c.NextExpress, express);
                    if (job == null)
                    {
                        noRouteUntil[Key(coaches)] = Time.realtimeSinceStartup + RetrySeconds;
                        Main.Warn($"passengers: no route from {start.ID.FullDisplayID} for {coaches.Count} coach(es); tried again in a minute");
                        continue;
                    }
                    idle.Remove(coaches);
                    c.Due.Remove(time);
                    c.Running.Add(new Departure { Job = job.ID, Opened = time });
                    if (express)
                        c.NextExpress = type != Passengers.Express;
                    Main.Log($"passengers: {c.Id}'s {Clock(time)} train is {job.ID} ({(type == Passengers.Express ? "express" : "local")}, " +
                             $"{coaches.Count} coach(es) at {Passengers.TrackOf(coaches)?.ID?.FullDisplayID ?? "?"}, boarding on {start.ID.FullDisplayID})");
                    break;
                }
            }
        }

        /// <summary>A train's job, express or local as asked, else the other kind; with its kind, or null if no route was found.</summary>
        private static (Job Job, DV.ThingTypes.JobType Type) Post(string city, Track start, List<TrainCar> coaches, bool expressFirst, bool expressAllowed)
        {
            var types = !expressAllowed ? new[] { Passengers.Local }
                : expressFirst ? new[] { Passengers.Express, Passengers.Local } : new[] { Passengers.Local, Passengers.Express };
            foreach (var t in types)
                if (Passengers.Departure(city, start, coaches, t)?.currentJobInChain is Job job)
                    return (job, t);
            return (null, types[0]);
        }

        private static string Key(List<TrainCar> coaches) => string.Join(",", coaches.Select(c => c.logicCar.carGuid));

        private static string Clock(double time)
        {
            var minutes = (int)Math.Round((time - Math.Floor(time)) * 24 * 60);
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }
    }
}
