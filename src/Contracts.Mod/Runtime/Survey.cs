using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DV.Logic.Job;
using DV.ThingTypes;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// A survey of the stations' tracks, to find each industry's own spots (owner, 2026-11-05: at
    /// the Steel Mill "the S-B tracks are under the dock crane. The A5S is at the factory dock",
    /// while the stock jobs, and so the mod's spots, use one loading track for the whole
    /// industry). survey.txt in the mod's folder (never shipped; scripts/run_spike.py --survey)
    /// names the stations, or "all", and "quit". For each station, after the player is taken
    /// there and the world has streamed in, survey.log gets:
    ///
    /// - its warehouse machines, their tracks and cargo, and where the machine stands;
    /// - its stock cargo groups, and the mod's industry lines there;
    /// - each of its tracks: ID, kind (storage, in, out, the warehouse's), length, and the larger
    ///   objects within 12 m of it (cranes, docks, silos and the like, by name);
    ///
    /// and views from above, of the whole station and of each sub-yard, straight down and from
    /// the south, with each track's line in the picture written to survey.json for the runner to
    /// draw the track IDs on.
    /// </summary>
    internal sealed class Survey : MonoBehaviour
    {
        private const float Fov = 50f;
        private const float Near = 12f;
        private static string folder;
        private static bool quitAfter;
        private static List<string> wanted;
        private StreamWriter log;
        private StreamWriter json;
        private bool daylightNoted;

        public static void Install(string modFolder)
        {
            folder = modFolder;
            var trigger = Path.Combine(folder, "survey.txt");
            if (!File.Exists(trigger))
                return;
            var words = File.ReadAllText(trigger).Split(new[] { ' ', '\n', '\r', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            quitAfter = words.Any(w => w.Equals("quit", StringComparison.OrdinalIgnoreCase));
            wanted = words.Where(w => !w.Equals("quit", StringComparison.OrdinalIgnoreCase)).Select(w => w.ToUpperInvariant()).ToList();
            var go = new GameObject("[contracts survey]");
            DontDestroyOnLoad(go);
            var survey = go.AddComponent<Survey>();
            var started = false;
            WorldStreamingInit.LoadingFinished += () =>
            {
                if (started)
                    return;
                started = true;
                survey.StartCoroutine(survey.Run());
            };
            Main.Log("survey requested: " + string.Join(" ", words));
        }

        private void Line(string text)
        {
            log?.WriteLine(text);
            log?.Flush();
        }

        private IEnumerator Run()
        {
            log = new StreamWriter(Path.Combine(folder, "survey.log"), false);
            json = new StreamWriter(Path.Combine(folder, "survey.json"), false);
            Line($"survey {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            for (var waited = 0f; !AStartGameData.carsAndJobsLoadingFinished && waited < 240f; waited += 1f)
                yield return new WaitForSeconds(1f);
            for (var waited = 0f; ContractRuntime.Instance?.Economy?.Industries == null && waited < 60f; waited += 1f)
                yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(5f);
            var stations = StationController.allStations.Where(s => s != null && s.logicStation?.yard != null)
                .Where(s => wanted.Count == 0 || wanted.Contains("ALL") || wanted.Contains(s.stationInfo.YardID))
                .Where(s => !wanted.Contains("ALL") || s.logicStation.yard.WarehouseMachines.Count > 0)
                .OrderBy(s => s.stationInfo.YardID, StringComparer.Ordinal).ToList();
            Line($"stations: {string.Join(", ", stations.Select(s => s.stationInfo.YardID))}");
            foreach (var station in stations)
            {
                IEnumerator visit = null;
                try
                {
                    visit = Visit(station);
                }
                catch (Exception e)
                {
                    Line($"{station.stationInfo.YardID}: failed: {e}");
                }
                if (visit != null)
                    yield return visit;
            }
            Line("SURVEY DONE");
            log.Close();
            json.Close();
            if (quitAfter)
            {
                yield return new WaitForSeconds(2f);
                Application.Quit();
            }
        }

        private sealed class Rail
        {
            public Track Track;
            public RailTrack RailTrack;
            public string Kind;
            public List<Vector3> Points;
        }

        private IEnumerator Visit(StationController station)
        {
            var yard = station.stationInfo.YardID;
            PlayerManager.TeleportPlayer(station.transform.position + Vector3.up * 2f, Quaternion.identity, null, false);
            yield return new WaitForSeconds(40f);
            List<Rail> rails = null;
            try
            {
                rails = Describe(station);
            }
            catch (Exception e)
            {
                Line($"{yard}: describing failed: {e}");
            }
            if (rails == null || rails.Count == 0)
                yield break;
            yield return View(yard, "all", rails, rails, top: true);
            foreach (var group in rails.GroupBy(r => r.Track.ID.SignIDSubYardPart).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                yield return View(yard, group.Key, group.ToList(), rails, top: true);
                yield return View(yard, group.Key, group.ToList(), rails, top: false);
            }
        }

        /// <summary>The station's machines, cargo and tracks, to the log; its tracks' lines, for the views.</summary>
        private List<Rail> Describe(StationController station)
        {
            var yard = station.stationInfo.YardID;
            var logic = station.logicStation.yard;
            Line("");
            Line($"== {yard} {station.stationInfo.Name}");
            var controllers = FindObjectsOfType<WarehouseMachineController>();
            foreach (var m in logic.WarehouseMachines)
            {
                var at = controllers.FirstOrDefault(c => c.warehouseMachine == m);
                Line($"machine {m.ID} on {m.WarehouseTrack?.ID?.FullDisplayID}: {string.Join(", ", m.SupportedCargoTypes)}" +
                     (at != null ? $"; the machine stands {Along(at.transform.position, m.WarehouseTrack)}" : ""));
            }
            foreach (var g in station.proceduralJobsRuleset?.outputCargoGroups ?? new List<CargoGroup>())
                Line($"stock out: {string.Join(", ", g.cargoTypes)} to {string.Join(", ", g.stations.Where(s => s != null).Select(s => s.stationInfo.YardID))}");
            foreach (var g in station.proceduralJobsRuleset?.inputCargoGroups ?? new List<CargoGroup>())
                Line($"stock in: {string.Join(", ", g.cargoTypes)} from {string.Join(", ", g.stations.Where(s => s != null).Select(s => s.stationInfo.YardID))}");
            var economy = ContractRuntime.Instance?.Economy;
            foreach (var industry in economy?.Industries?.Values.Where(i => i.Station == yard) ?? Enumerable.Empty<Core.Industries.Industry>())
                Line($"industry {industry.Id} {industry.Name}: {string.Join("; ", industry.Lines.Select(l => $"{(l.Ships ? "ships" : "takes")} {l.Role} {string.Join("/", l.Cargo)}"))}");

            string KindOf(Track t)
            {
                var kinds = new List<string>();
                if (logic.StorageTracks.Contains(t)) kinds.Add("storage");
                if (logic.TransferInTracks.Contains(t)) kinds.Add("in");
                if (logic.TransferOutTracks.Contains(t)) kinds.Add("out");
                foreach (var m in logic.WarehouseMachines.Where(m => m.WarehouseTrack == t))
                    kinds.Add($"warehouse {m.ID}");
                if (InterchangeYards.IsInterchangeTrack(t)) kinds.Add("interchange");
                return kinds.Count == 0 ? "-" : string.Join("+", kinds);
            }

            var rails = new List<Rail>();
            foreach (var kv in RailTrackRegistry.LogicToRailTrack)
            {
                var t = kv.Key;
                if (t?.ID == null || t.ID.IsGeneric() || t.ID.yardId != yard || kv.Value?.curve == null)
                    continue;
                var n = Math.Max(2, (int)Math.Ceiling(t.length / 8.0) + 1);
                var points = Enumerable.Range(0, n).Select(i => kv.Value.curve.GetPointAt(i / (float)(n - 1))).ToList();
                rails.Add(new Rail { Track = t, RailTrack = kv.Value, Kind = KindOf(t), Points = points });
            }
            rails = rails.OrderBy(r => r.Track.ID.FullDisplayID, StringComparer.Ordinal).ToList();

            // The larger objects beside each track, by name: their bounds within 12 m of it, across.
            // Not the site-wide meshes (a whole works, the sky, the water) or the track's own parts.
            string[] noise = { "TrackChunk", "anchors", "buffer_stop", "TerrainImposter", "Sky Dome", "DistantLights", "water", "Far_", "Flat_", "Road_", "ParkingLight", "Fence" };
            var renderers = FindObjectsOfType<MeshRenderer>().Where(r => r != null && r.enabled && r.bounds.size.magnitude > 4f
                && r.bounds.extents.x < 60f && r.bounds.extents.z < 60f && !noise.Any(n => Name(r.transform).Contains(n))).ToList();
            foreach (var r in rails)
            {
                var near = new Dictionary<string, float>();
                foreach (var mr in renderers)
                {
                    var b = mr.bounds;
                    var best = float.MaxValue;
                    foreach (var p in r.Points)
                    {
                        if (Mathf.Abs(p.x - b.center.x) > b.extents.x + 40f || Mathf.Abs(p.z - b.center.z) > b.extents.z + 40f)
                            continue;
                        best = Mathf.Min(best, b.SqrDistance(new Vector3(p.x, Mathf.Clamp(p.y + 3f, b.min.y, b.max.y), p.z)));
                    }
                    if (best > Near * Near)
                        continue;
                    var name = Name(mr.transform);
                    var d = Mathf.Sqrt(best);
                    if (!near.TryGetValue(name, out var was) || d < was)
                        near[name] = d;
                }
                var id = r.Track.ID;
                Line($"track {id.FullDisplayID} {r.Kind} {r.Track.length:F0} m: " +
                     string.Join(", ", near.OrderBy(x => x.Value).Take(14).Select(x => $"{x.Key} {x.Value:F0}")));
            }
            return rails;
        }

        /// <summary>An object's name with its parent's, which names the prefab more often than the mesh does.</summary>
        private static string Name(Transform t)
        {
            var own = t.name;
            var parent = t.parent != null ? t.parent.name : null;
            return parent == null || parent == own ? own : $"{parent}/{own}";
        }

        /// <summary>Where a point stands from a track: how far along it, and how far off it.</summary>
        private static string Along(Vector3 at, Track track)
        {
            if (track == null || !RailTrackRegistry.LogicToRailTrack.TryGetValue(track, out var rail) || rail?.curve == null)
                return "?";
            var best = (D: float.MaxValue, T: 0f);
            for (var i = 0; i <= 100; i++)
            {
                var p = rail.curve.GetPointAt(i / 100f);
                var d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(at.x, at.z));
                if (d < best.D)
                    best = (d, i / 100f);
            }
            return $"{best.T * track.length:F0} m along {track.ID.FullDisplayID}, {best.D:F0} m off it";
        }

        /// <summary>
        /// A view of some of the station's tracks from above: straight down, the picture's top
        /// north, or from the south looking down at 40°. Every station track's line in the picture
        /// goes to survey.json.
        /// </summary>
        private IEnumerator View(string yard, string part, List<Rail> framed, List<Rail> all, bool top)
        {
            var points = framed.SelectMany(r => r.Points).ToList();
            var min = new Vector3(points.Min(p => p.x), points.Min(p => p.y), points.Min(p => p.z));
            var max = new Vector3(points.Max(p => p.x), points.Max(p => p.y), points.Max(p => p.z));
            var center = (min + max) / 2f;
            var cam = PlayerManager.PlayerCamera;
            if (cam == null)
            {
                Line("no player camera");
                yield break;
            }
            var aspect = Screen.height > 0 ? Screen.width / (float)Screen.height : 16f / 9f;
            var half = Mathf.Tan(Fov * Mathf.Deg2Rad / 2f);
            var spanX = Mathf.Max(60f, max.x - min.x + 40f);
            var spanZ = Mathf.Max(60f, max.z - min.z + 40f);
            var distance = Mathf.Max(spanZ / 2f / half, spanX / 2f / (half * aspect));
            Vector3 eye;
            Quaternion rot;
            if (top)
            {
                eye = center + Vector3.up * distance;
                rot = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            }
            else
            {
                var pitch = 40f * Mathf.Deg2Rad;
                eye = center + new Vector3(0f, Mathf.Sin(pitch), -Mathf.Cos(pitch)) * distance * 0.8f;
                rot = Quaternion.LookRotation(center - eye, Vector3.up);
            }
            var name = $"survey-{yard}-{part}-{(top ? "top" : "south")}";
            SetDaylight();
            var t = cam.transform;
            var parent = t.parent;
            var lp = t.localPosition;
            var lr = t.localRotation;
            var fov = cam.fieldOfView;
            var far = cam.farClipPlane;
            Camera.CameraCallback hold = c =>
            {
                if (c == cam)
                {
                    c.transform.SetPositionAndRotation(eye, rot);
                    c.fieldOfView = Fov;
                    c.farClipPlane = Mathf.Max(far, distance * 3f);
                }
            };
            t.SetParent(null, true);
            Camera.onPreCull += hold;
            // Moved every frame, not only as it renders: the terrain shows the tiles in Camera.main's view (see Interchange Yards' Inspector).
            for (var start = Time.unscaledTime; Time.unscaledTime - start < 3f;)
            {
                t.SetPositionAndRotation(eye, rot);
                cam.fieldOfView = Fov;
                yield return null;
            }
            t.SetPositionAndRotation(eye, rot);
            cam.fieldOfView = Fov;
            var width = Screen.width;
            var height = Screen.height;
            var lines = all.Select(r => new
            {
                id = r.Track.ID.FullDisplayID,
                kind = r.Kind,
                pts = r.Points.Select(p => cam.WorldToScreenPoint(p)).Where(s => s.z > 0f)
                    .Select(s => new[] { (int)s.x, height - (int)s.y }).ToList(),
            }).Where(l => l.pts.Count > 1).ToList();
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, name + ".png"));
            for (var start = Time.unscaledTime; Time.unscaledTime - start < 1.5f;)
            {
                t.SetPositionAndRotation(eye, rot);
                yield return null;
            }
            Camera.onPreCull -= hold;
            t.SetParent(parent, false);
            t.localPosition = lp;
            t.localRotation = lr;
            cam.fieldOfView = fov;
            cam.farClipPlane = far;
            json.WriteLine(Newtonsoft.Json.JsonConvert.SerializeObject(new { station = yard, view = name, width, height, top, tracks = lines }));
            json.Flush();
            Line($"view {name}.png: {framed.Count} track(s), from {distance.ToString("F0", CultureInfo.InvariantCulture)} m");
        }

        private static object Member(object o, string name)
        {
            if (o == null)
                return null;
            var t = Traverse.Create(o);
            return t.Field(name).FieldExists() ? t.Field(name).GetValue() : t.Property(name).PropertyExists() ? t.Property(name).GetValue() : null;
        }

        /// <summary>Late morning, as Interchange Yards' Inspector sets it.</summary>
        private void SetDaylight()
        {
            try
            {
                var type = AccessTools.TypeByName("DV.WeatherSystem.WeatherDriver");
                var driver = type != null ? FindObjectOfType(type) : null;
                var manager = Member(driver, "manager");
                var cycle = Member(Member(manager, "todSky"), "Cycle");
                if (!(Member(cycle, "DateTime") is DateTime now))
                    return;
                var tc = Traverse.Create(cycle);
                var member = tc.Field("DateTime").FieldExists() ? tc.Field("DateTime") : tc.Property("DateTime");
                member.SetValue(now.Date.AddHours(11));
                AccessTools.Method(manager.GetType(), "RefreshTimeOfDay")?.Invoke(manager, null);
                if (!daylightNoted)
                    Line($"views: time of day set from {now:HH:mm} to 11:00");
                daylightNoted = true;
            }
            catch (Exception e)
            {
                if (!daylightNoted)
                    Line($"views: setting the time of day failed: {e.Message}");
                daylightNoted = true;
            }
        }
    }
}
