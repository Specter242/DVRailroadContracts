using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Contracts.Core.Json;

namespace Contracts.Core.State
{
    /// <summary>A saved state newer than this mod understands; the mod stays inert rather than overwrite it.</summary>
    public sealed class UnsupportedSchemaException : Exception
    {
        public UnsupportedSchemaException(int schema)
            : base($"the saved operations state has schema {schema}; this mod reads up to {OpsState.CurrentSchema}")
        {
            Schema = schema;
        }

        public int Schema { get; }
    }

    /// <summary>
    /// The "murphy_ops" save key (operations-design.md, 7.2 and 14.5). The
    /// runtime stores the JSON text as a JObject in Derail Valley's save;
    /// vanilla ignores the key, so the save stays loadable without the mod.
    /// Older schemas migrate through pure functions in <see cref="Migrations"/>.
    /// </summary>
    public static class StateSerializer
    {
        /// <summary>Upgrades a document from the keyed schema to the next one. Empty until schema 2 exists.</summary>
        internal static readonly Dictionary<int, Func<JsonValue, JsonValue>> Migrations = new Dictionary<int, Func<JsonValue, JsonValue>>
        {
            [1] = FromSchema1,
        };

        /// <summary>Marks a level or midnight a migration could not know; resume fills it from the data.</summary>
        public const int Unset = -1;

        /// <summary>
        /// Schema 1 → 2 (pre-release, 2026-10-12): the daily offers became
        /// standing contracts. Offers, standings and contract links are dropped;
        /// contract levels and the next midnight are left for resume to set from
        /// the data. Waybills, cars, storage and the ledger carry over.
        /// </summary>
        internal static JsonValue FromSchema1(JsonValue old)
        {
            var root = JsonValue.NewObject();
            foreach (var member in old.Members)
            {
                switch (member.Key)
                {
                    case "schema":
                        root.Set("schema", 2);
                        break;
                    case "nextContract":
                    case "nextOfferAt":
                    case "contractDay":
                    case "contracts":
                        break;
                    case "industries":
                        var industries = JsonValue.NewObject();
                        foreach (var i in member.Value.Members)
                            industries.Set(i.Key, JsonValue.NewObject().Set("storage", i.Value["storage"]).Set("level", Unset));
                        root.Set("industries", industries);
                        break;
                    case "waybills":
                        var waybills = JsonValue.NewObject();
                        foreach (var w in member.Value.Members)
                        {
                            var copy = JsonValue.NewObject();
                            foreach (var field in w.Value.Members.Where(f => f.Key != "contract" && f.Key != "dueAt"))
                                copy.Set(field.Key, field.Value);
                            waybills.Set(w.Key, copy.Set("graceDays", 0));
                        }
                        root.Set("waybills", waybills);
                        break;
                    default:
                        root.Set(member.Key, member.Value);
                        break;
                }
            }
            if (!root.Has("day"))
                root.Set("day", 0).Set("nextMidnightAt", Unset);
            return root;
        }

        public static string ToJson(OpsState state, bool indented = false) => ToJsonValue(state).ToJson(indented);

        public static OpsState FromJson(string json) => FromJsonValue(JsonValue.Parse(json));

        public static JsonValue ToJsonValue(OpsState s)
        {
            var root = JsonValue.NewObject()
                .Set("schema", OpsState.CurrentSchema)
                .Set("map", s.Map)
                .Set("opsDataSha256", s.OpsDataSha256)
                .Set("tracksHash", s.TracksHash)
                .Set("clock", JsonValue.NewObject()
                    .Set("opsMinutes", s.Clock.OpsMinutes)
                    .Set("lastWorldMinutes", s.Clock.LastWorldMinutes)
                    .Set("dayOffsetMinutes", s.Clock.DayOffsetMinutes))
                .Set("rng", s.Rng.ToString("x16", CultureInfo.InvariantCulture))
                .Set("nextWaybill", s.NextWaybill)
                .Set("nextService", s.NextService)
                .Set("day", s.Day)
                .Set("nextMidnightAt", s.NextMidnightAt);

            var industries = JsonValue.NewObject();
            foreach (var pair in s.Industries)
            {
                var storage = JsonValue.NewObject();
                foreach (var level in pair.Value.Storage)
                    storage.Set(level.Key, level.Value);
                var i = pair.Value;
                var history = JsonValue.NewArray();
                foreach (var entry in i.History)
                    history.Add(JsonValue.NewObject().Set("day", entry.Day).Set("value", entry.Value));
                var late = JsonValue.NewArray();
                foreach (var t in i.LateSpots)
                    late.Add(JsonValue.Number(t));
                industries.Set(pair.Key, JsonValue.NewObject()
                    .Set("storage", storage)
                    .Set("level", i.Level)
                    .Set("requestedLevel", JsonValue.NumberOrNull(i.RequestedLevel))
                    .Set("history", history)
                    .Set("receivedCars", i.ReceivedCars)
                    .Set("lateSpots", late));
            }
            root.Set("industries", industries);

            var interchanges = JsonValue.NewObject();
            foreach (var pair in s.Interchanges)
            {
                var x = pair.Value;
                interchanges.Set(pair.Key, JsonValue.NewObject()
                    .Set("lastService", JsonValue.NumberOrNull(x.LastService))
                    .Set("nextServiceAt", x.NextServiceAt)
                    .Set("pending", x.Pending == null ? JsonValue.Null : Write(x.Pending)));
            }
            root.Set("interchanges", interchanges);

            var cars = JsonValue.NewObject();
            foreach (var pair in s.Cars)
            {
                var c = pair.Value;
                cars.Set(pair.Key, JsonValue.NewObject()
                    .Set("class", c.ClassId)
                    .Set("livery", c.Livery)
                    .Set("mark", c.Mark)
                    .Set("owner", c.Owner)
                    .Set("home", c.Home)
                    .Set("load", c.LoadId)
                    .Set("waybill", c.WaybillId)
                    .Set("progress", c.Progress)
                    .Set("lastSeen", c.LastSeen == null
                        ? JsonValue.Null
                        : JsonValue.NewObject().Set("track", c.LastSeen.Track).Set("span", c.LastSeen.Span).Set("t", c.LastSeen.T)));
            }
            root.Set("cars", cars);

            var waybills = JsonValue.NewObject();
            foreach (var pair in s.Waybills)
            {
                var w = pair.Value;
                waybills.Set(pair.Key, JsonValue.NewObject()
                    .Set("car", w.CarGuid)
                    .Set("kind", KindName(w.Kind))
                    .Set("load", w.LoadId)
                    .Set("origin", w.Origin)
                    .Set("dest", w.Dest)
                    .Set("created", w.Created)
                    .Set("pay", w.Pay)
                    .Set("state", StateName(w.State))
                    .Set("since", w.Since)
                    .Set("paidAt", JsonValue.NumberOrNull(w.PaidAt))
                    .Set("graceDays", w.GraceDays)
                    .Set("late", w.Late)
                    .Set("bonus", w.Bonus));
            }
            root.Set("waybills", waybills);


            var ledger = JsonValue.NewArray();
            foreach (var e in s.Ledger)
            {
                ledger.Add(JsonValue.NewObject()
                    .Set("t", e.T)
                    .Set("kind", e.Kind)
                    .Set("waybill", e.WaybillId)
                    .Set("car", e.CarGuid)
                    .Set("amount", e.Amount)
                    .Set("memo", e.Memo));
            }
            root.Set("ledger", ledger);

            root.Set("stats", JsonValue.NewObject()
                .Set("delivered", s.Stats.Delivered)
                .Set("voided", s.Stats.Voided)
                .Set("interchanged", s.Stats.Interchanged)
                .Set("services", s.Stats.Services)
                .Set("revenue", s.Stats.Revenue));
            return root;
        }

        private static JsonValue Write(PlannedService p)
        {
            var cars = JsonValue.NewArray();
            foreach (var c in p.Cars)
            {
                cars.Add(JsonValue.NewObject()
                    .Set("token", c.Token)
                    .Set("class", c.ClassId)
                    .Set("livery", c.Livery)
                    .Set("mark", c.Mark)
                    .Set("load", c.LoadId)
                    .Set("kind", KindName(c.Kind))
                    .Set("dest", c.Dest)
                    .Set("pay", c.Pay));
            }
            return JsonValue.NewObject()
                .Set("id", p.Id)
                .Set("dueAt", p.DueAt)
                .Set("removed", p.Removed)
                .Set("unfilled", p.Unfilled)
                .Set("attempts", p.Attempts)
                .Set("lastDeferral", p.LastDeferral)
                .Set("cars", cars);
        }

        public static OpsState FromJsonValue(JsonValue root)
        {
            var schema = root["schema"]?.AsInt() ?? throw new FormatException("the operations state has no schema");
            if (schema > OpsState.CurrentSchema)
                throw new UnsupportedSchemaException(schema);
            if (schema < 1)
                throw new FormatException($"the operations state has an invalid schema {schema}");
            while (schema < OpsState.CurrentSchema)
            {
                if (!Migrations.TryGetValue(schema, out var migrate))
                    throw new FormatException($"no migration from operations state schema {schema}");
                root = migrate(root);
                schema = root["schema"].AsInt();
            }

            var s = new OpsState
            {
                Schema = schema,
                Map = Str(root, "map"),
                OpsDataSha256 = Str(root, "opsDataSha256") ?? "",
                TracksHash = Str(root, "tracksHash") ?? "",
                NextWaybill = Req(root, "nextWaybill").AsInt(),
                NextService = Req(root, "nextService").AsInt(),
                Day = Req(root, "day").AsInt(),
                NextMidnightAt = Req(root, "nextMidnightAt").AsDouble(),
            };
            var clock = Req(root, "clock");
            s.Clock = new ClockState
            {
                OpsMinutes = Req(clock, "opsMinutes").AsDouble(),
                LastWorldMinutes = Req(clock, "lastWorldMinutes").AsDouble(),
                DayOffsetMinutes = Req(clock, "dayOffsetMinutes").AsDouble(),
            };
            if (!ulong.TryParse(Req(root, "rng").AsString(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rngState))
                throw new FormatException("rng is not a hexadecimal number");
            s.Rng = rngState;

            foreach (var pair in Req(root, "industries").Members)
            {
                var x = pair.Value;
                var industry = new IndustryState
                {
                    Level = Req(x, "level").AsInt(),
                    RequestedLevel = x["requestedLevel"] is JsonValue requested && !requested.IsNull ? requested.AsInt() : (int?)null,
                    ReceivedCars = x["receivedCars"]?.AsInt() ?? 0,
                };
                foreach (var level in Req(x, "storage").Members)
                    industry.Storage[level.Key] = level.Value.AsDouble();
                if (x["history"] is JsonValue history && !history.IsNull)
                    foreach (var e in history.Items)
                        industry.History.Add(new PerformanceEntry { Day = Req(e, "day").AsInt(), Value = Req(e, "value").AsDouble() });
                if (x["lateSpots"] is JsonValue lateSpots && !lateSpots.IsNull)
                    foreach (var t in lateSpots.Items)
                        industry.LateSpots.Add(t.AsDouble());
                s.Industries[pair.Key] = industry;
            }
            foreach (var pair in Req(root, "interchanges").Members)
            {
                var x = pair.Value;
                s.Interchanges[pair.Key] = new InterchangeState
                {
                    LastService = Num(x, "lastService"),
                    NextServiceAt = Req(x, "nextServiceAt").AsDouble(),
                    Pending = x["pending"] == null || x["pending"].IsNull ? null : ReadPending(x["pending"]),
                };
            }
            foreach (var pair in Req(root, "cars").Members)
            {
                var c = pair.Value;
                var seen = c["lastSeen"];
                s.Cars[pair.Key] = new CarRecord
                {
                    Guid = pair.Key,
                    ClassId = Str(c, "class"),
                    Livery = Str(c, "livery"),
                    Mark = Str(c, "mark"),
                    Owner = Str(c, "owner") ?? "foreign",
                    Home = Str(c, "home"),
                    LoadId = Str(c, "load"),
                    WaybillId = Str(c, "waybill"),
                    Progress = Req(c, "progress").AsDouble(),
                    LastSeen = seen == null || seen.IsNull ? null : new CarSighting
                    {
                        Track = Str(seen, "track"),
                        Span = Req(seen, "span").AsDouble(),
                        T = Req(seen, "t").AsDouble(),
                    },
                };
            }
            foreach (var pair in Req(root, "waybills").Members)
            {
                var w = pair.Value;
                s.Waybills[pair.Key] = new Waybill
                {
                    Id = pair.Key,
                    CarGuid = Str(w, "car"),
                    Kind = ParseKind(Str(w, "kind")),
                    LoadId = Str(w, "load"),
                    Origin = Str(w, "origin"),
                    Dest = Str(w, "dest"),
                    Created = Req(w, "created").AsDouble(),
                    Pay = Req(w, "pay").AsDouble(),
                    State = ParseState(Str(w, "state")),
                    Since = Req(w, "since").AsDouble(),
                    PaidAt = Num(w, "paidAt"),
                    GraceDays = w["graceDays"]?.AsInt() ?? 0,
                    Late = Flag(w, "late"),
                    Bonus = Num(w, "bonus") ?? 0.0,
                };
            }
            foreach (var e in Req(root, "ledger").Items)
            {
                s.Ledger.Add(new LedgerEntry
                {
                    T = Req(e, "t").AsDouble(),
                    Kind = Str(e, "kind"),
                    WaybillId = Str(e, "waybill"),
                    CarGuid = Str(e, "car"),
                    Amount = Req(e, "amount").AsDouble(),
                    Memo = Str(e, "memo"),
                });
            }
            var stats = Req(root, "stats");
            s.Stats = new OpsStats
            {
                Delivered = Req(stats, "delivered").AsInt(),
                Voided = Req(stats, "voided").AsInt(),
                Interchanged = Req(stats, "interchanged").AsInt(),
                Services = Req(stats, "services").AsInt(),
                Revenue = Req(stats, "revenue").AsDouble(),
            };
            return s;
        }

        private static PlannedService ReadPending(JsonValue p)
        {
            var service = new PlannedService
            {
                Id = Str(p, "id"),
                DueAt = Req(p, "dueAt").AsDouble(),
                Removed = Req(p, "removed").AsBool(),
                Unfilled = Req(p, "unfilled").AsInt(),
                Attempts = Req(p, "attempts").AsInt(),
                LastDeferral = Str(p, "lastDeferral"),
            };
            foreach (var c in Req(p, "cars").Items)
            {
                service.Cars.Add(new PlannedCar
                {
                    Token = Str(c, "token"),
                    ClassId = Str(c, "class"),
                    Livery = Str(c, "livery"),
                    Mark = Str(c, "mark"),
                    LoadId = Str(c, "load"),
                    Kind = ParseKind(Str(c, "kind")),
                    Dest = Str(c, "dest"),
                    Pay = Req(c, "pay").AsDouble(),
                });
            }
            return service;
        }

        private static JsonValue Req(JsonValue o, string key) =>
            o[key] is JsonValue v && !v.IsNull ? v : throw new FormatException($"the operations state lacks '{key}'");

        private static string Str(JsonValue o, string key) => o[key] is JsonValue v && !v.IsNull ? v.AsString() : null;

        private static double? Num(JsonValue o, string key) => o[key] is JsonValue v && !v.IsNull ? v.AsDouble() : (double?)null;

        private static bool Flag(JsonValue o, string key) => o[key] is JsonValue v && !v.IsNull && v.AsBool();


        internal static string KindName(WaybillKind kind)
        {
            switch (kind)
            {
                case WaybillKind.InboundLoad: return "inboundLoad";
                case WaybillKind.InboundEmpty: return "inboundEmpty";
                case WaybillKind.OutboundLoad: return "outboundLoad";
                default: return "outboundEmpty";
            }
        }

        internal static WaybillKind ParseKind(string name)
        {
            switch (name)
            {
                case "inboundLoad": return WaybillKind.InboundLoad;
                case "inboundEmpty": return WaybillKind.InboundEmpty;
                case "outboundLoad": return WaybillKind.OutboundLoad;
                case "outboundEmpty": return WaybillKind.OutboundEmpty;
                default: throw new FormatException($"unknown waybill kind '{name}'");
            }
        }

        internal static string StateName(WaybillState state)
        {
            switch (state)
            {
                case WaybillState.Open: return "open";
                case WaybillState.Servicing: return "servicing";
                default: return "awaitingPickup";
            }
        }

        internal static WaybillState ParseState(string name)
        {
            switch (name)
            {
                case "open": return WaybillState.Open;
                case "servicing": return WaybillState.Servicing;
                case "awaitingPickup": return WaybillState.AwaitingPickup;
                default: throw new FormatException($"unknown waybill state '{name}'");
            }
        }
    }
}
