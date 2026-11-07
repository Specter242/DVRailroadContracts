using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Booklets;
using DV.Localization;
using DV.RenderTextureSystem.BookletRender;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// A station move's papers say Shunting (StationMoves; owner, 2026-11-05). The game draws a
    /// job's type, color and description from its stock type, a transport or empty haul here,
    /// on four papers: the overview at the office, the booklet, the expired report and the
    /// missing-license report. Each is changed as its pages are made:
    ///
    /// - the type is Shunting, in the shunting color, on the cover too;
    /// - the description says what the move is for ("Spot empty cars for loading.");
    /// - the booklet has no haul page, as the cars stay in the station: couple, uncouple,
    ///   validate.
    ///
    /// Stock shunting booklets of one track have the same pages bar the warehouse's.
    /// </summary>
    internal static class MovePapers
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
                    AccessTools.Method(typeof(BookletCreator_JobExpiredReport), "GetJobExpiredTemplateData", new[] { typeof(Job_data) }),
                    AccessTools.Method(typeof(BookletCreator_JobMissingLicense), "GetMissingLicenseTemplateData", new[] { typeof(Job_data), typeof(bool) }),
                };
                if (methods.Any(m => m == null))
                    Main.Warn("station moves: a job paper's maker is not where it was; some of their papers say haul, not shunting");
                return methods.Where(m => m != null);
            }

            private static void Postfix(Job_data job, List<TemplatePaperData> __result)
            {
                try
                {
                    if (job != null && __result != null && StationMoves.KindOf(job.ID, job.type) is StationMoves.Kind kind)
                        Relabel(kind, __result);
                }
                catch (Exception e)
                {
                    Main.Error($"station moves: relabeling {job?.ID}'s papers failed: {e.Message}");
                }
            }
        }

        /// <summary>A station move's pages, as made for its stock type, made to read as shunting.</summary>
        internal static void Relabel(StationMoves.Kind kind, List<TemplatePaperData> pages)
        {
            var name = C.SHUNTING_JOB_TYPE_NAME;
            var color = kind == StationMoves.Kind.SpotEmpty ? C.SHUNTING_LOAD_JOB_TYPE_COLOR : C.SHUNTING_UNLOAD_JOB_TYPE_COLOR;
            var haul = LocalizationAPI.L("job/task_type_haul");
            var removed = pages.RemoveAll(p => p is TaskTemplatePaperData t && t.taskType == haul);
            foreach (var page in pages)
                switch (page)
                {
                    case CoverPageTemplatePaperData cover:
                        cover.jobType = LocalizationAPI.L("job/shunting_job_cover_page");
                        break;
                    case FrontPageTemplatePaperData front:
                        front.jobType = name;
                        front.jobTypeColor = color;
                        front.jobDescription = Description(kind, front.distinctCargoTypes);
                        break;
                    case JobExpiredTemplatePaperData expired:
                        expired.jobType = name;
                        expired.jobTypeColor = color;
                        break;
                    case MissingLicensesPageTemplatePaperData missing:
                        missing.jobType = name;
                        missing.jobTypeColor = color;
                        break;
                }
            if (removed > 0)
                Renumber(pages);
        }

        /// <summary>What the move is for, in the stock descriptions' manner ("Deliver Coal.", "Haul empty cars.").</summary>
        internal static string Description(StationMoves.Kind kind, List<CargoType> cargo)
        {
            switch (kind)
            {
                case StationMoves.Kind.SpotLoad:
                    var names = (cargo ?? new List<CargoType>()).Where(c => c != CargoType.None).Select(c => LocalizationAPI.L(c.ToV2().localizationKeyFull)).ToList();
                    return names.Count == 0 ? "Spot the cars for unloading." : $"Spot {Join(names)} for unloading.";
                case StationMoves.Kind.SpotEmpty:
                    return "Spot empty cars for loading.";
                default:
                    return "Pull empty cars into storage.";
            }
        }

        private static string Join(List<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];

        /// <summary>The booklet's pages and steps numbered again, a page gone.</summary>
        private static void Renumber(List<TemplatePaperData> pages)
        {
            var total = pages.Count.ToString();
            var step = 0;
            for (var i = 0; i < pages.Count; i++)
            {
                var number = (i + 1).ToString();
                switch (pages[i])
                {
                    case CoverPageTemplatePaperData cover:
                        cover.pageNumber = number;
                        cover.totalPages = total;
                        break;
                    case FrontPageTemplatePaperData front:
                        front.pageNumber = number;
                        front.totalPages = total;
                        break;
                    case TaskTemplatePaperData task:
                        task.stepNum = (++step).ToString();
                        task.pageNumber = number;
                        task.totalPages = total;
                        break;
                    case ValidateJobTaskTemplatePaperData validate:
                        validate.stepNum = (++step).ToString();
                        validate.pageNumber = number;
                        validate.totalPages = total;
                        break;
                }
            }
        }
    }
}
