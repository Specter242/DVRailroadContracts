using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Contracts.Core.Json;

namespace Contracts.Core.Data
{
    /// <summary>Thrown with every problem found, so one run of the tool or the mod lists them all.</summary>
    public sealed class OpsDataException : Exception
    {
        public OpsDataException(IReadOnlyList<string> errors)
            : base("murphy-ops.json is invalid:\n  " + string.Join("\n  ", errors))
        {
            Errors = errors;
        }

        public IReadOnlyList<string> Errors { get; }
    }

    /// <summary>
    /// Reads and validates murphy-ops.json (docs/build-format.md, "Operations").
    /// The mod stays inert on any error (operations-design.md, 6.4), so the
    /// checks here are the runtime's half of the contract with tools/murphy/ops.py.
    /// </summary>
    public static class OpsDataLoader
    {
        public static OpsData Load(string json, string expectedMap = null)
        {
            JsonValue root;
            try
            {
                root = JsonValue.Parse(json);
            }
            catch (FormatException e)
            {
                throw new OpsDataException(new[] { e.Message });
            }
            var errors = new List<string>();
            var data = Read(root, errors);
            if (data != null)
            {
                data.Sha256 = Sha256(json);
                data.Freeze();
                if (expectedMap != null && data.Map != expectedMap)
                    errors.Add($"map is '{data.Map}', but the loaded map is '{expectedMap}'");
                errors.AddRange(Validate(data));
            }
            if (errors.Count > 0)
                throw new OpsDataException(errors);
            return data;
        }

        public static string Sha256(string text)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------ reading

        private static OpsData Read(JsonValue root, List<string> errors)
        {
            var r = new Reader(errors);
            if (root.Kind != JsonKind.Object)
            {
                errors.Add("the document must be an object");
                return null;
            }
            var data = new OpsData();
            data.Format = r.Int(root, "format", "format") ?? 0;
            if (data.Format != OpsData.CurrentFormat)
            {
                errors.Add($"format is {data.Format}; this mod reads format {OpsData.CurrentFormat}");
                return null;
            }
            data.Map = r.String(root, "map", "map");
            ReadSettings(root["settings"], data.Settings, r);

            foreach (var pair in r.ObjectMembers(root, "carClasses", "carClasses"))
            {
                var path = $"carClasses.{pair.Key}";
                var c = new CarClass { Id = pair.Key, Name = r.String(pair.Value, "name", path) ?? pair.Key, LengthM = r.Double(pair.Value, "lengthM", path) ?? 0.0 };
                c.Liveries.AddRange(r.Strings(pair.Value, "liveries", path));
                data.CarClasses[pair.Key] = c;
            }
            foreach (var pair in r.ObjectMembers(root, "loads", "loads"))
            {
                var path = $"loads.{pair.Key}";
                data.Loads[pair.Key] = new LoadDef
                {
                    Id = pair.Key,
                    Name = r.String(pair.Value, "name", path) ?? pair.Key,
                    DvCargo = r.String(pair.Value, "dvCargo", path),
                    TonsPerCar = r.Double(pair.Value, "tonsPerCar", path) ?? 0.0,
                    Importable = r.OptionalBool(pair.Value, "importable", path) ?? true,
                };
            }
            var i = 0;
            foreach (var a in r.Array(root, "areas", "areas"))
            {
                var path = $"areas[{i++}]";
                var area = new Area
                {
                    Id = r.String(a, "id", path),
                    Name = r.String(a, "name", path),
                    Station = r.OptionalString(a, "station", path),
                    Milepost = r.Double(a, "milepost", path) ?? 0.0,
                    Color = r.OptionalString(a, "color", path),
                };
                var office = a["office"];
                if (office != null && !office.IsNull)
                {
                    area.Office = new OfficeRef
                    {
                        Station = r.OptionalString(office, "station", path + ".office"),
                        Position = r.Numbers(office, "position", path + ".office"),
                        Facing = r.Numbers(office, "facing", path + ".office"),
                    };
                }
                data.Areas.Add(area);
            }
            i = 0;
            foreach (var ind in r.Array(root, "industries", "industries"))
            {
                var path = $"industries[{i++}]";
                var industry = new Industry
                {
                    Id = r.String(ind, "id", path),
                    Name = r.String(ind, "name", path),
                    AreaId = r.String(ind, "area", path),
                    UsesContract = r.OptionalBool(ind, "usesContract", path) ?? true,
                    ContractLevel = r.OptionalInt(ind, "contractLevel", path),
                };
                path = $"industries.{industry.Id}";
                foreach (var s in r.ObjectMembers(ind, "storage", path))
                    industry.Storage[s.Key] = r.AsDouble(s.Value, $"{path}.storage.{s.Key}") ?? 0.0;
                var j = 0;
                foreach (var comp in r.Array(ind, "components", path))
                {
                    var cpath = $"{path}.components[{j++}]";
                    var component = new IndustryComponent
                    {
                        LocalId = r.String(comp, "id", cpath),
                        Code = r.String(comp, "code", cpath),
                        LoadId = r.String(comp, "load", cpath),
                        Orders = r.OptionalBool(comp, "orders", cpath) ?? true,
                    };
                    var type = r.String(comp, "type", cpath);
                    if (type == "unloader")
                    {
                        component.Type = ComponentType.Unloader;
                        component.ConsumePerDay = r.Double(comp, "consumePerDay", cpath) ?? 0.0;
                        component.UnloadPerDay = r.Double(comp, "unloadPerDay", cpath) ?? 0.0;
                    }
                    else if (type == "loader")
                    {
                        component.Type = ComponentType.Loader;
                        component.ProducePerDay = r.Double(comp, "producePerDay", cpath) ?? 0.0;
                        component.LoadPerCarPerDay = r.Double(comp, "loadPerCarPerDay", cpath) ?? 0.0;
                    }
                    else if (type != null)
                        errors.Add($"{cpath}.type is '{type}', not unloader or loader");
                    component.CarClasses.AddRange(r.Strings(comp, "carClasses", cpath));
                    component.SpotList.AddRange(ReadSpots(comp, cpath, r));
                    industry.Components.Add(component);
                }
                data.Industries.Add(industry);
            }
            i = 0;
            foreach (var x in r.Array(root, "interchanges", "interchanges"))
            {
                var path = $"interchanges[{i++}]";
                var interchange = new Interchange
                {
                    Id = r.String(x, "id", path),
                    Name = r.String(x, "name", path),
                    AreaId = r.String(x, "area", path),
                    Code = r.String(x, "code", path),
                    CapacityFactor = r.Double(x, "capacityFactor", path) ?? 0.0,
                    Milepost = r.Double(x, "milepost", path) ?? 0.0,
                };
                interchange.Roads.AddRange(r.Strings(x, "roads", path));
                interchange.SpotList.AddRange(ReadSpots(x, path, r));
                data.Interchanges.Add(interchange);
            }
            return data;
        }

        private static void ReadSettings(JsonValue s, OpsSettings settings, Reader r)
        {
            if (s == null)
                return;
            const string path = "settings";
            settings.ServeHour = r.OptionalInt(s, "serveHour", path) ?? settings.ServeHour;
            settings.ExtraServiceDelayHours = r.OptionalDouble(s, "extraServiceDelayHours", path) ?? settings.ExtraServiceDelayHours;
            settings.SpotSpeed = r.OptionalDouble(s, "spotSpeed", path) ?? settings.SpotSpeed;
            settings.SpotDwellSeconds = r.OptionalDouble(s, "spotDwellSeconds", path) ?? settings.SpotDwellSeconds;
            settings.ServiceClearance = r.OptionalDouble(s, "serviceClearance", path) ?? settings.ServiceClearance;
            settings.PayScale = r.OptionalDouble(s, "payScale", path) ?? settings.PayScale;
            settings.MaxCatchUpDays = r.OptionalDouble(s, "maxCatchUpDays", path) ?? settings.MaxCatchUpDays;
            settings.ServeOnNewSave = r.OptionalBool(s, "serveOnNewSave", path) ?? settings.ServeOnNewSave;
            settings.LedgerLimit = r.OptionalInt(s, "ledgerLimit", path) ?? settings.LedgerLimit;
            if (s.Has("disableProceduralJobsAt"))
                settings.DisableProceduralJobsAt.AddRange(r.Strings(s, "disableProceduralJobsAt", path));
            var c = s["contracts"];
            if (c == null || c.IsNull)
                return;
            const string cpath = "settings.contracts";
            var contracts = settings.Contracts;
            contracts.Enabled = r.OptionalBool(c, "enabled", cpath) ?? true;
            contracts.StartLevel = r.OptionalInt(c, "startLevel", cpath) ?? contracts.StartLevel;
            contracts.MaxStartLevel = r.OptionalInt(c, "maxStartLevel", cpath) ?? contracts.MaxStartLevel;
            contracts.DueHours = r.OptionalDouble(c, "dueHours", cpath) ?? contracts.DueHours;
            contracts.LatePayFactor = r.OptionalDouble(c, "latePayFactor", cpath) ?? contracts.LatePayFactor;
            contracts.LevelScalesPay = r.OptionalBool(c, "levelScalesPay", cpath) ?? contracts.LevelScalesPay;
            contracts.PenaltyBase = r.OptionalDouble(c, "penaltyBase", cpath) ?? contracts.PenaltyBase;
        }

        private static IEnumerable<Spot> ReadSpots(JsonValue owner, string path, Reader r)
        {
            var k = 0;
            foreach (var s in r.Array(owner, "spots", path))
            {
                var spath = $"{path}.spots[{k++}]";
                yield return new Spot(
                    r.String(s, "track", spath),
                    r.String(s, "runtimeName", spath),
                    r.Double(s, "trackLength", spath) ?? 0.0,
                    r.Double(s, "from", spath) ?? 0.0,
                    r.Double(s, "to", spath) ?? 0.0);
            }
        }

        // ------------------------------------------------------------ validating

        /// <summary>Semantic checks on a frozen data set; empty when valid.</summary>
        public static List<string> Validate(OpsData data)
        {
            var errors = new List<string>();
            var s = data.Settings;
            if (s.ServeHour < 0 || s.ServeHour > 23)
                errors.Add($"settings.serveHour {s.ServeHour} is not 0–23");
            if (s.ExtraServiceDelayHours <= 0)
                errors.Add("settings.extraServiceDelayHours must be positive");
            if (s.SpotSpeed < 0 || s.SpotDwellSeconds < 0 || s.ServiceClearance < 0 || s.PayScale < 0)
                errors.Add("settings: spotSpeed, spotDwellSeconds, serviceClearance and payScale must not be negative");
            if (s.MaxCatchUpDays <= 0)
                errors.Add("settings.maxCatchUpDays must be positive");
            if (s.LedgerLimit < 1)
                errors.Add("settings.ledgerLimit must be at least 1");
            var contracts = s.Contracts;
            if (contracts.StartLevel < 0 || contracts.StartLevel > 5)
                errors.Add("settings.contracts.startLevel must be 0–5");
            if (contracts.MaxStartLevel < 1 || contracts.MaxStartLevel > 5)
                errors.Add("settings.contracts.maxStartLevel must be 1–5");
            if (!(contracts.DueHours > 0))
                errors.Add("settings.contracts.dueHours must be positive");
            if (contracts.LatePayFactor < 0 || contracts.LatePayFactor > 1)
                errors.Add("settings.contracts.latePayFactor must be within 0–1");
            if (contracts.PenaltyBase < 0)
                errors.Add("settings.contracts.penaltyBase must not be negative");

            foreach (var c in data.CarClasses.Values)
            {
                if (c.Liveries.Count == 0)
                    errors.Add($"carClasses.{c.Id} has no liveries");
                if (!(c.LengthM > 0))
                    errors.Add($"carClasses.{c.Id}.lengthM must be positive");
            }
            foreach (var l in data.Loads.Values)
            {
                if (string.IsNullOrEmpty(l.DvCargo))
                    errors.Add($"loads.{l.Id} has no dvCargo");
                if (l.TonsPerCar < 0)
                    errors.Add($"loads.{l.Id}.tonsPerCar is negative");
            }
            var areaIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in data.Areas)
            {
                if (a.Id != null && !areaIds.Add(a.Id))
                    errors.Add($"area '{a.Id}' is defined twice");
                if (a.Office != null && a.Office.Station == null && (a.Office.Position?.Length != 3 || a.Office.Facing?.Length != 2))
                    errors.Add($"area '{a.Id}' office needs a station, or a position [x, y, z] and a facing [x, z]");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var industry in data.Industries)
            {
                if (industry.Id == null)
                    continue;
                if (!ids.Add(industry.Id))
                    errors.Add($"ID '{industry.Id}' is used twice (industries and interchanges share IDs)");
                if (data.Area(industry.AreaId) == null)
                    errors.Add($"industry '{industry.Id}' names unknown area '{industry.AreaId}'");
                foreach (var cap in industry.Storage)
                {
                    if (!data.Loads.ContainsKey(cap.Key))
                        errors.Add($"industry '{industry.Id}' stores unknown load '{cap.Key}'");
                    if (!(cap.Value > 0))
                        errors.Add($"industry '{industry.Id}' storage for '{cap.Key}' must be positive");
                }
                if (industry.Components.Count == 0)
                    errors.Add($"industry '{industry.Id}' has no components");
                if (industry.ContractLevel is int level && (level < 0 || level > 5))
                    errors.Add($"industry '{industry.Id}' contractLevel {level} is not 0–5");
                var local = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in industry.Components)
                {
                    var name = c.FullId;
                    if (c.LocalId != null && !local.Add(c.LocalId))
                        errors.Add($"component '{name}' is defined twice");
                    CheckCode(c.Code, name, errors);
                    if (c.LoadId == null || !data.Loads.ContainsKey(c.LoadId))
                        errors.Add($"component '{name}' names unknown load '{c.LoadId}'");
                    else if (!industry.Storage.ContainsKey(c.LoadId))
                        errors.Add($"component '{name}': industry '{industry.Id}' has no storage cap for '{c.LoadId}'");
                    if (c.CarClasses.Count == 0)
                        errors.Add($"component '{name}' has no car classes");
                    foreach (var cc in c.CarClasses.Where(cc => !data.CarClasses.ContainsKey(cc)))
                        errors.Add($"component '{name}' names unknown car class '{cc}'");
                    if (c.ConsumePerDay < 0 || c.UnloadPerDay < 0 || c.ProducePerDay < 0 || c.LoadPerCarPerDay < 0)
                        errors.Add($"component '{name}' has a negative rate");
                    var shortest = c.CarClasses.Where(data.CarClasses.ContainsKey).Select(cc => data.CarClasses[cc].LengthM).DefaultIfEmpty(0).Min();
                    CheckSpots(c.SpotList, name, shortest, errors);
                }
            }
            var shortestOverall = data.CarClasses.Values.Select(c => c.LengthM).DefaultIfEmpty(0).Min();
            foreach (var x in data.Interchanges)
            {
                if (x.Id == null)
                    continue;
                if (!ids.Add(x.Id))
                    errors.Add($"ID '{x.Id}' is used twice (industries and interchanges share IDs)");
                if (data.Area(x.AreaId) == null)
                    errors.Add($"interchange '{x.Id}' names unknown area '{x.AreaId}'");
                CheckCode(x.Code, x.Id, errors);
                if (x.Roads.Count == 0)
                    errors.Add($"interchange '{x.Id}' has no roads");
                if (!(x.CapacityFactor > 0))
                    errors.Add($"interchange '{x.Id}' needs a positive capacityFactor");
                CheckSpots(x.SpotList, x.Id, shortestOverall, errors);
            }
            if (data.Interchanges.Count == 0 && data.Industries.Count > 0)
                errors.Add("industries need at least one interchange");
            return errors;
        }

        private static void CheckCode(string code, string owner, List<string> errors)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 8)
                errors.Add($"'{owner}' needs a plate code of 1–8 characters");
        }

        private static void CheckSpots(IReadOnlyList<Spot> spots, string owner, double shortestCar, List<string> errors)
        {
            if (spots.Count == 0)
                errors.Add($"'{owner}' has no spots");
            foreach (var s in spots)
            {
                if (string.IsNullOrEmpty(s.RuntimeName))
                    errors.Add($"'{owner}' has a spot without a runtimeName");
                if (!(s.From >= 0 && s.From < s.To && s.To <= s.TrackLength + 0.05))
                    errors.Add($"'{owner}' spot {s.From}–{s.To} m does not lie within track '{s.Track}' ({s.TrackLength} m)");
                else if (s.Length < shortestCar)
                    errors.Add($"'{owner}' spot on '{s.Track}' is {s.Length} m, shorter than its shortest car ({shortestCar} m)");
            }
        }

        /// <summary>Field access that records a path-qualified error instead of throwing.</summary>
        private sealed class Reader
        {
            private readonly List<string> errors;

            public Reader(List<string> errors) => this.errors = errors;

            private JsonValue Member(JsonValue owner, string key, string path, bool required)
            {
                if (owner == null || owner.Kind != JsonKind.Object)
                {
                    if (required)
                        errors.Add($"{path} must be an object");
                    return null;
                }
                var v = owner[key];
                if ((v == null || v.IsNull) && required)
                    errors.Add($"{path}.{key} is missing");
                return v == null || v.IsNull ? null : v;
            }

            private T Convert<T>(JsonValue v, string path, Func<JsonValue, T> convert, T fallback)
            {
                if (v == null)
                    return fallback;
                try
                {
                    return convert(v);
                }
                catch (FormatException e)
                {
                    errors.Add($"{path}: {e.Message}");
                    return fallback;
                }
            }

            public string String(JsonValue o, string key, string path) => Convert(Member(o, key, path, true), $"{path}.{key}", v => v.AsString(), null);

            public string OptionalString(JsonValue o, string key, string path) => Convert(Member(o, key, path, false), $"{path}.{key}", v => v.AsString(), null);

            public double? Double(JsonValue o, string key, string path) => Convert<double?>(Member(o, key, path, true), $"{path}.{key}", v => v.AsDouble(), null);

            public double? OptionalDouble(JsonValue o, string key, string path) => Convert<double?>(Member(o, key, path, false), $"{path}.{key}", v => v.AsDouble(), null);

            public double? AsDouble(JsonValue v, string path) => Convert<double?>(v, path, x => x.AsDouble(), null);

            public int? Int(JsonValue o, string key, string path) => Convert<int?>(Member(o, key, path, true), $"{path}.{key}", v => v.AsInt(), null);

            public int? OptionalInt(JsonValue o, string key, string path) => Convert<int?>(Member(o, key, path, false), $"{path}.{key}", v => v.AsInt(), null);

            public bool? OptionalBool(JsonValue o, string key, string path) => Convert<bool?>(Member(o, key, path, false), $"{path}.{key}", v => v.AsBool(), null);

            public IEnumerable<JsonValue> Array(JsonValue o, string key, string path)
            {
                var v = Member(o, key, path, true);
                if (v == null)
                    return Enumerable.Empty<JsonValue>();
                if (v.Kind != JsonKind.Array)
                {
                    errors.Add($"{path}.{key} must be an array");
                    return Enumerable.Empty<JsonValue>();
                }
                return v.Items;
            }

            public IEnumerable<KeyValuePair<string, JsonValue>> ObjectMembers(JsonValue o, string key, string path)
            {
                var v = Member(o, key, path, true);
                if (v == null)
                    return Enumerable.Empty<KeyValuePair<string, JsonValue>>();
                if (v.Kind != JsonKind.Object)
                {
                    errors.Add($"{path}.{key} must be an object");
                    return Enumerable.Empty<KeyValuePair<string, JsonValue>>();
                }
                return v.Members;
            }

            /// <summary>An optional array of numbers, or null when absent.</summary>
            public double[] Numbers(JsonValue o, string key, string path)
            {
                var v = Member(o, key, path, false);
                if (v == null)
                    return null;
                if (v.Kind != JsonKind.Array)
                {
                    errors.Add($"{path}.{key} must be an array of numbers");
                    return null;
                }
                var k = 0;
                return v.Items.Select(item => Convert(item, $"{path}.{key}[{k++}]", x => x.AsDouble(), 0.0)).ToArray();
            }

            public IEnumerable<string> Strings(JsonValue o, string key, string path)
            {
                var k = 0;
                var list = new List<string>();
                foreach (var item in Array(o, key, path))
                {
                    var s = Convert(item, $"{path}.{key}[{k++}]", v => v.AsString(), null);
                    if (s != null)
                        list.Add(s);
                }
                return list;
            }
        }
    }
}
