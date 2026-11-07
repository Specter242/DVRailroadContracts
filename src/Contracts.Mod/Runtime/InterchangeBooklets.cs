using System;
using System.Collections.Generic;
using System.Linq;
using DV.Booklets;
using DV.Logic.Job;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// Booklets at the interchange offices (design D10, spike S7; owner, 2026-11-02: "I don't see
    /// any waybills for these cars at the office"). An interchange is a station of its own
    /// (InterchangeStations), and lays out its jobs' booklets on its office's table itself. A job
    /// written before, or with Interchange Yards 0.1.4 or before, whose cars start on an
    /// interchange's tracks belongs to the station beside it (MF for MF West, IME for IME West),
    /// whose office is far off. Its overview booklet goes on the interchange office's table instead:
    ///
    /// - laid out as the stock station does it (StationController.Update), one a frame, once a
    ///   player is within 150 m of the interchange office, and entered in that station's own
    ///   lists, so the station treats it as its own (taking it, expiring it). On a Multiplayer
    ///   host any player counts, as the Multiplayer mod counts any player at a stock station's
    ///   office (Blaze221, Nexus, 2026-11-05: contracts "spawn only for the host"; up to 0.1.6
    ///   only the host's own player counted, so a client alone at the office saw no booklets);
    /// - and when the station lays one out itself (the player at its own office), it goes on the
    ///   interchange office's table all the same.
    ///
    /// The office's validator and job bin are the stock ones and take any station's job.
    /// </summary>
    internal sealed class InterchangeBooklets : MonoBehaviour
    {
        private const string OfficePrefix = "[interchange office] ";
        private const float RangeSqr = 150f * 150f;   // StationJobGenerationRange.jobOverviewBookletGenerationSqrDistance

        private static readonly Dictionary<string, PointOnPlane> Tables = new Dictionary<string, PointOnPlane>(StringComparer.Ordinal);
        private static bool laying;
        private float timer;

        public static void Install()
        {
            var go = new GameObject("[contracts interchange booklets]");
            DontDestroyOnLoad(go);
            go.AddComponent<InterchangeBooklets>();
        }

        /// <summary>The interchange whose track a job starts on (its first task's start, else its first car's track), or null.</summary>
        internal static string InterchangeOf(Job job)
        {
            var start = Start(job?.tasks);
            if (start == null)
                start = job?.tasks?.SelectMany(t => t.GetTaskData().cars ?? new List<Car>()).FirstOrDefault()?.FrontBogieTrack;
            return InterchangeYards.InterchangeOf(start);
        }

        private static Track Start(IEnumerable<Task> tasks)
        {
            foreach (var t in tasks ?? Enumerable.Empty<Task>())
            {
                var data = t.GetTaskData();
                if (data.nestedTasks != null && data.nestedTasks.Count > 0)
                    return Start(data.nestedTasks);
                if (data.startTrack != null)
                    return data.startTrack;
            }
            return null;
        }

        /// <summary>
        /// Whether a player is within booklet range (150 m) of a point: the local player, or on a
        /// Multiplayer host any loaded player.
        /// </summary>
        internal static bool AnyPlayerNear(Vector3 at)
        {
            if (PlayerManager.PlayerTransform != null && (PlayerManager.PlayerTransform.position - at).sqrMagnitude <= RangeSqr)
                return true;
            if (Kiosk.Multiplayer.PlayerPositions().Any(p => (p - at).sqrMagnitude <= RangeSqr))
                return true;
#if DEVTOOLS
            if (StandIns.Any(p => (p - at).sqrMagnitude <= RangeSqr))
                return true;
#endif
            return false;
        }

#if DEVTOOLS
        /// <summary>Where the probe stands other players (a Multiplayer client), in the scene's frame.</summary>
        internal static readonly List<Vector3> StandIns = new List<Vector3>();
#endif

        /// <summary>The booklet table of an interchange's office, found by its name ("[interchange office] MF West").</summary>
        internal static PointOnPlane Table(string interchange)
        {
            if (Tables.TryGetValue(interchange, out var table) && table != null)
                return table;
            Tables.Clear();
            foreach (var p in FindObjectsOfType<PointOnPlane>())
            {
                for (var t = p.transform; t != null; t = t.parent)
                    if (t.name.StartsWith(OfficePrefix, StringComparison.Ordinal))
                    {
                        Tables[t.name.Substring(OfficePrefix.Length)] = p;
                        break;
                    }
            }
            return Tables.TryGetValue(interchange, out table) ? table : null;
        }

        private void Update()
        {
            timer -= Time.unscaledDeltaTime;
            if (timer > 0f)
                return;
            timer = 0.2f;
            try
            {
                LayOut();
            }
            catch (Exception e)
            {
                Main.Error($"interchange booklets: {e}");
                timer = 30f;
            }
        }

        private static void LayOut()
        {
            if (!ContractRuntime.Active || !AStartGameData.carsAndJobsLoadingFinished || PlayerManager.PlayerTransform == null)
                return;
            foreach (var interchange in InterchangeYards.All().Keys)
            {
                var table = Table(interchange);
                if (table == null || !AnyPlayerNear(table.transform.position))
                    continue;
                var station = Economy.Station(InterchangeYards.NeighborOf(interchange));
                if (station?.logicStation == null)
                    continue;
                var processed = Traverse.Create(station).Field("processedNewJobs").GetValue<HashSet<Job>>();
                var spawned = Traverse.Create(station).Field("spawnedJobOverviews").GetValue<List<JobOverview>>();
                if (processed == null || spawned == null)
                    continue;
                var job = station.logicStation.availableJobs.FirstOrDefault(j => !processed.Contains(j) && InterchangeOf(j) == interchange);
                if (job == null)
                    continue;
                var (position, rotation) = table.GetRandomPointWithRotationOnPlane();
                laying = true;
                try
                {
                    spawned.Add(BookletCreator.CreateJobOverview(job, position, rotation, WorldMover.OriginShiftParent));
                }
                finally
                {
                    laying = false;
                }
                processed.Add(job);
                Main.Log($"interchange booklets: {job.ID} laid out at {interchange}");
            }
        }

        /// <summary>
        /// Persistent Jobs expires a job taken at the validator when none of its cars stand within
        /// its station's job range (600 m of the station's centre); an interchange's tracks are
        /// farther out, and a passenger train's coaches may be anywhere (the player brings them).
        /// A task whose cars stand on an interchange track, or are the player's coaches, counts as
        /// in range.
        /// </summary>
        public static void GuardJobRange(Harmony harmony)
        {
            var patch = AccessTools.TypeByName("PersistentJobsMod.HarmonyPatches.JobValidators.JobValidator_ProcessJobOverview_Patch");
            var check = patch == null ? null : AccessTools.Method(patch, "AreTaskCarsInRange");
            if (check == null)
            {
                Main.Warn("Persistent Jobs: its validator's range check is not where it was; interchange jobs may expire when taken");
                return;
            }
            harmony.Patch(check, postfix: new HarmonyMethod(AccessTools.Method(typeof(InterchangeBooklets), nameof(InRangeAtInterchange))));
            Main.Log("Persistent Jobs: jobs whose cars stand at an interchange count as in their station's range");
        }

        private static void InRangeAtInterchange(Task task, ref bool __result)
        {
            try
            {
                if (!__result && ContractRuntime.Active && Traverse.Create(task).Field("cars").GetValue<List<Car>>() is List<Car> cars)
                {
                    var registry = DV.Utils.SingletonBehaviour<TrainCarRegistry>.Instance;
                    __result = cars.Any(c => InterchangeYards.IsInterchangeTrack(c?.FrontBogieTrack)
                                             || c != null && registry != null && registry.logicCarToTrainCar.TryGetValue(c, out var t) && Passengers.IsCoach(t));
                }
            }
            catch (Exception e)
            {
                Main.Error($"interchange booklets: the range check failed: {e.Message}");
            }
        }

        /// <summary>A station laying out an interchange job's booklet itself puts it on the interchange office's table.</summary>
        [HarmonyPatch(typeof(BookletCreator), nameof(BookletCreator.CreateJobOverview))]
        private static class Redirect
        {
            private static void Prefix(Job job, ref Vector3 position, ref Quaternion rotation)
            {
                try
                {
                    if (laying || !ContractRuntime.Active)
                        return;
                    var interchange = InterchangeOf(job);
                    // The interchange's own station lays its jobs' booklets on this table itself.
                    if (interchange == null || job.chainData?.chainOriginYardId == InterchangeYards.CodeOf(interchange))
                        return;
                    var table = Table(interchange);
                    if (table == null)
                        return;
                    (position, rotation) = table.GetRandomPointWithRotationOnPlane();
                    Main.Log($"interchange booklets: {job.ID} goes on the {interchange} office's table");
                }
                catch (Exception e)
                {
                    Main.Error($"interchange booklets: redirecting {job?.ID} failed: {e.Message}");
                }
            }
        }
    }
}
