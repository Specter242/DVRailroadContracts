using System;
using System.Collections.Generic;
using System.Linq;
using DV.Localization;
using DV.Logic.Job;
using DV.ThingTypes;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// Each interchange a station of its own (owner, 2026-11-05: "Let's get these working as
    /// their own industry locations"): West Interchange, WI, and Northeast Interchange, NEI, with
    /// their names and colors as on the interchange mod's maps and books. Their tracks are in
    /// their own yards (Interchange Yards' WI-A-01-X), so the jobs to and from them are theirs:
    /// their IDs (WI-FH-12), their papers, their office's booklet table, and a haul between an
    /// interchange and the station beside it.
    ///
    /// A station is a StationController the game finds by its yard ID. One is made at each
    /// interchange office as the game content loads (after the interchange mod builds the
    /// offices, before saved jobs load), as the stock stations are set up:
    ///
    /// - its range at the office: booklets are laid out on the office's table within 150 m;
    /// - under an anchor named as a stock station's, so the Multiplayer mod pairs it with the
    ///   office's job validator;
    /// - no cargo groups, warehouse machines or yard tracks of its own lists, so no generation
    ///   (stock, Persistent Jobs') sends cars there; and the career's stations make no jobs;
    /// - its name a translation key of its own, read here.
    ///
    /// With Interchange Yards 0.1.4 or before the tracks are in the yards of MF and IME: the
    /// stations are made, but no job uses them.
    /// </summary>
    internal static class InterchangeStations
    {
        private const string OfficePrefix = "[interchange office] ";
        private const string KeyPrefix = "contracts/station_";
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<StationController> Made = new List<StationController>();

        public static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>Whether a station is an interchange's, made here.</summary>
        public static bool Is(StationController station) => station != null && Made.Contains(station);

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Transform offices;
            try
            {
                var content = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "[origin shift content]");
                offices = content != null ? content.transform.Find("Offices/Offices_w3") : null;
            }
            catch (Exception)
            {
                return;
            }
            if (offices == null)
                return;
            Made.RemoveAll(s => s == null);
            var made = new List<string>();
            foreach (Transform office in offices)
            {
                if (!office.name.StartsWith(OfficePrefix, StringComparison.Ordinal))
                    continue;
                var interchange = office.name.Substring(OfficePrefix.Length);
                try
                {
                    var station = Make(interchange, office);
                    if (station != null)
                        made.Add($"{station.stationInfo.YardID} ({station.stationInfo.Name})");
                }
                catch (Exception e)
                {
                    Main.Error($"interchange stations: making {interchange}'s failed: {e}");
                }
            }
            Main.Log($"interchange stations: {(made.Count == 0 ? "none made" : string.Join(", ", made))}");
        }

        private static StationController Make(string interchange, Transform office)
        {
            var code = InterchangeYards.CodeOf(interchange);
            if (code == interchange)
                return null;
            if (StationController.allStations?.Any(s => s != null && s.stationInfo?.YardID == code) == true)
                return null;
            var key = KeyPrefix + code;
            Names[key] = InterchangeYards.TitleOf(interchange);
            // Made inactive, so the components wake (Awake) with their settings in place. It stands
            // under an anchor in its office named as a stock station's ("OilWellCentral_office_anchor"
            // for the validator under "OilWellCentral"): the Multiplayer mod pairs a station with the
            // job validator whose parent's name, with "_office_anchor", is its parent's, and through
            // that pair the players other than the host take and hand in jobs at the office
            // (results/probe-mp3-before, 2026-11-09: the interchanges' validators were paired with none).
            var validator = office.GetComponentsInChildren<JobValidator>(true).FirstOrDefault();
            var anchor = new GameObject($"{(validator != null ? validator.transform.parent.name : office.name)}_office_anchor");
            anchor.transform.SetParent(office, false);
            var go = new GameObject($"[interchange station] {code}");
            go.SetActive(false);
            go.transform.SetParent(anchor.transform, false);
            var range = go.AddComponent<StationJobGenerationRange>();
            range.stationCenterAnchor = go.transform;
            var rules = go.AddComponent<StationProceduralJobsRuleset>();
            rules.inputCargoGroups = new List<CargoGroup>();
            rules.outputCargoGroups = new List<CargoGroup>();
            rules.loadStartingJobSupported = rules.haulStartingJobSupported = rules.unloadStartingJobSupported = rules.emptyHaulStartingJobSupported = false;
            var station = go.AddComponent<StationController>();
            // Its type is blank, as the stock stations' are (" ").
            station.stationInfo = new StationInfo(Names[key], " ", code, InterchangeYards.ColorOf(interchange), key);
            station.storageRailtracksGONames = new List<string>();
            station.transferInRailtracksGONames = new List<string>();
            station.transferOutRailtracksGONames = new List<string>();
            station.warehouseMachineControllers = new List<WarehouseMachineController>();
            station.proceduralJobsRuleset = rules;
            var table = office.GetComponentsInChildren<PointOnPlane>(true).FirstOrDefault();
            if (table == null)
                Main.Warn($"interchange stations: {interchange}'s office has no booklet table; its booklets are laid out at the office");
            Traverse.Create(station).Field("jobBookletSpawnSurface").SetValue(table);
            go.SetActive(true);
            Made.Add(station);
            Register(Object.FindObjectOfType<LogicController>());
            return station;
        }

        /// <summary>
        /// The game's yard ID to station map has the interchanges' stations: it is made as the
        /// game wakes, from the stations then awake, whichever came first.
        /// </summary>
        private static void Register(LogicController logic)
        {
            if (logic?.YardIdToStationController == null)
                return;
            var supported = Traverse.Create(logic).Field("stationToSupportedCarTypes").GetValue<Dictionary<StationController, HashSet<TrainCarType_v2>>>();
            foreach (var station in Made.Where(s => s != null))
            {
                logic.YardIdToStationController[station.stationInfo.YardID] = station;
                if (supported != null && !supported.ContainsKey(station))
                    supported[station] = new HashSet<TrainCarType_v2>();
            }
        }

        [HarmonyPatch(typeof(LogicController), "Awake")]
        private static class Registered
        {
            private static void Postfix(LogicController __instance)
            {
                try
                {
                    Register(__instance);
                }
                catch (Exception e)
                {
                    Main.Error($"interchange stations: registering them failed: {e.Message}");
                }
            }
        }

        private static Type emptyDestination;

        /// <summary>
        /// Persistent Jobs reckons, as the world loads, where each station takes empty cars: the
        /// longest of its storage tracks, and fails for a station without one (stopping every load
        /// step after its own). An interchange station has none, and takes no empties (it has no
        /// cargo): it is given its longest track. Patched by hand: the type is another mod's.
        /// </summary>
        public static void GuardPersistentJobs(Harmony harmony)
        {
            var destinations = AccessTools.TypeByName("PersistentJobsMod.Model.EmptyTrainCarTypeDestinations");
            var method = destinations == null ? null : AccessTools.Method(destinations, "GetEmptyTrainCarTypeDestination", new[] { typeof(StationController) });
            emptyDestination = AccessTools.TypeByName("PersistentJobsMod.Model.EmptyTrainCarTypeDestination");
            if (method == null || emptyDestination == null || AccessTools.Constructor(emptyDestination, new[] { typeof(StationController), typeof(double) }) == null)
            {
                Main.Warn("Persistent Jobs: its empty car destinations are not where they were; the interchange stations may stop it loading");
                return;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(InterchangeStations), nameof(NoEmpties))));
        }

        private static bool NoEmpties(StationController destination, ref object __result)
        {
            if (!Is(destination))
                return true;
            var longest = InterchangeYards.Tracks(InterchangeYards.NameOf(destination.stationInfo.YardID)).Select(t => (double)t.length).DefaultIfEmpty(0).Max();
            __result = Activator.CreateInstance(emptyDestination, destination, longest);
            return false;
        }

        /// <summary>An interchange station's name, by its own translation key (stations print their names by theirs).</summary>
        [HarmonyPatch(typeof(LocalizationAPI), nameof(LocalizationAPI.L), typeof(string), typeof(string[]))]
        private static class Named
        {
            private static bool Prefix(string translationKey, ref string __result)
            {
                if (translationKey == null || !Names.TryGetValue(translationKey, out var name))
                    return true;
                __result = name;
                return false;
            }
        }
    }
}
