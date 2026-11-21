using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Booklets;
using DV.Localization;
using DV.Logic.Job;
using DV.RenderTextureSystem.BookletRender;
using HarmonyLib;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// A job to or from an interchange names it on its papers (owner, 2026-11-05: "The job bills
    /// don't have the interchanges as a destination or source"). An interchange is a station of
    /// its own (InterchangeStations), and its jobs' papers name it as the game makes them. A job
    /// written before, or with Interchange Yards 0.1.4 or before, belongs to the station beside
    /// the interchange (MF for West Interchange, IME for Northeast), and the game prints that
    /// station's name and color. Its papers are changed as they are made, the overview and the
    /// booklet; the job and its stations stay as they are:
    ///
    /// - the front page names the interchange as the start or end, in its color (the type under
    ///   the name stays the stock one, blank, as for every station);
    /// - the haul page names it as where to haul;
    /// - the couple and uncouple pages give its code, in its color, as the yard of its tracks
    ///   ("WI", track "WI1X").
    ///
    /// The names, codes and colors are the interchange mod's, as on its maps and books.
    /// </summary>
    internal static class InterchangePapers
    {
        [HarmonyPatch]
        private static class Pages
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var methods = new[]
                {
                    AccessTools.Method(typeof(BookletCreator_JobOverview), nameof(BookletCreator_JobOverview.GetJobOverviewTemplateData), new[] { typeof(Job_data) }),
                    AccessTools.Method(typeof(BookletCreator_Job), "GetBookletTemplateData", new[] { typeof(Job_data) }),
                };
                if (methods.Any(m => m == null))
                    Main.Warn("interchange papers: a job paper's maker is not where it was; some papers name the interchange's station, not the interchange");
                return methods.Where(m => m != null);
            }

            private static void Postfix(Job_data job, List<TemplatePaperData> __result)
            {
                try
                {
                    if (job != null && __result != null)
                        Relabel(job, __result);
                }
                catch (Exception e)
                {
                    Main.Error($"interchange papers: relabeling {job?.ID}'s papers failed: {e.Message}");
                }
            }
        }

        /// <summary>The interchange a job's cars start from, or null: its first task's start track's.</summary>
        internal static string From(Job_data job) => InterchangeYards.InterchangeOf(Leaves(job.tasksData).Select(t => t.startTrackID).FirstOrDefault(id => id != null));

        /// <summary>The interchange a job's cars go to, or null: its last task's destination track's.</summary>
        internal static string To(Job_data job) => InterchangeYards.InterchangeOf(Leaves(job.tasksData).Select(t => t.destinationTrackID).LastOrDefault(id => id != null));

        /// <summary>A job's pages, as made from its stock stations, made to name the interchanges it starts or ends at.</summary>
        internal static void Relabel(Job_data job, List<TemplatePaperData> pages)
        {
            var from = From(job);
            var to = To(job);
            // The interchange tracks the job's tasks use, as its task pages show them: track "A1X"
            // (in yard "WI"; "MF" for a job of the station beside it), or "WI1X" (Interchange Yards 0.1.4).
            // Starts and ends apart: a bridge job from NEI A3X to WI A3X names "A3X" at both ends, and
            // one map took the end's interchange for both, so its couple page said WI (the release
            // round's career check, 2026-11-21).
            var starts = new Dictionary<string, string>(StringComparer.Ordinal);
            var ends = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in Leaves(job.tasksData))
            {
                if (InterchangeYards.InterchangeOf(t.startTrackID) is string from0)
                    starts[t.startTrackID.TrackPartOnly] = from0;
                if (InterchangeYards.InterchangeOf(t.destinationTrackID) is string to0)
                    ends[t.destinationTrackID.TrackPartOnly] = to0;
            }
            if (from == null && to == null && starts.Count == 0 && ends.Count == 0)
                return;
            var haul = LocalizationAPI.L("job/task_type_haul");
            var uncouple = LocalizationAPI.L("job/task_type_uncouple");
            foreach (var page in pages)
                switch (page)
                {
                    case FrontPageTemplatePaperData front:
                        if (from != null)
                        {
                            front.startStationName = InterchangeYards.TitleOf(from);
                            front.startStationBgColor = InterchangeYards.ColorOf(from);
                        }
                        if (to != null)
                        {
                            front.endStationName = InterchangeYards.TitleOf(to);
                            front.endStationBgColor = InterchangeYards.ColorOf(to);
                        }
                        break;
                    case TaskTemplatePaperData task when task.taskType == haul:
                        if (to != null)
                        {
                            task.stationName = InterchangeYards.TitleOf(to);
                            task.stationColor = InterchangeYards.ColorOf(to);
                        }
                        break;
                    case TaskTemplatePaperData task:
                        // A couple page is where the cars are picked up, an uncouple page where they are
                        // left: each looks at its own end first, then the other (as all pages once did).
                        var first = task.taskType == uncouple ? ends : starts;
                        var second = ReferenceEquals(first, starts) ? ends : starts;
                        if (task.trackId != null && (first.TryGetValue(task.trackId, out var interchange)
                            || second.TryGetValue(task.trackId, out interchange)))
                        {
                            task.yardId = InterchangeYards.CodeOf(interchange);
                            task.yardColor = InterchangeYards.ColorOf(interchange);
                        }
                        break;
                }
        }

        private static IEnumerable<Task_data> Leaves(IEnumerable<Task_data> tasks)
        {
            foreach (var t in tasks ?? Enumerable.Empty<Task_data>())
            {
                if (t == null)
                    continue;
                if (t.nestedTasks != null && t.nestedTasks.Length > 0)
                {
                    foreach (var leaf in Leaves(t.nestedTasks))
                        yield return leaf;
                }
                else
                    yield return t;
            }
        }
    }
}
