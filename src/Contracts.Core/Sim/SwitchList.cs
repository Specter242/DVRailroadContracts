using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Contracts.Core.Data;
using Contracts.Core.State;

namespace Contracts.Core.Sim
{
    public sealed class SwitchListRow
    {
        public string WaybillId { get; set; }

        public string CarGuid { get; set; }

        public string Mark { get; set; }

        public string ClassId { get; set; }

        public string Load { get; set; }

        public string DestCode { get; set; }

        public string DestName { get; set; }

        /// <summary>Last seen track and span, e.g. "[Y]_[BC]_[B-04-S] 37 m".</summary>
        public string Location { get; set; }

        public string Status { get; set; }

        public double Pay { get; set; }

        public bool Held { get; set; }
    }

    /// <summary>
    /// The player's work (operations-design.md, 11): cars to move, cars at
    /// industries, cars waiting at an interchange, the next services and the
    /// industries' storage. Built from the state; the runtime only draws it.
    /// </summary>
    public sealed class SwitchList
    {
        public List<SwitchListRow> ToMove { get; } = new List<SwitchListRow>();

        public List<SwitchListRow> AtIndustries { get; } = new List<SwitchListRow>();

        public List<SwitchListRow> AwaitingPickup { get; } = new List<SwitchListRow>();

        public List<string> Services { get; } = new List<string>();

        public List<string> Storage { get; } = new List<string>();

        public static SwitchList Build(OpsEngine engine)
        {
            var data = engine.Data;
            var state = engine.State;
            var list = new SwitchList();
            foreach (var wb in state.Waybills.Values)
            {
                if (!state.Cars.TryGetValue(wb.CarGuid, out var car))
                    continue;
                var dest = data.Destination(wb.Dest);
                var row = new SwitchListRow
                {
                    WaybillId = wb.Id,
                    CarGuid = car.Guid,
                    Mark = car.Mark,
                    ClassId = car.ClassId,
                    Load = wb.LoadId != null && data.Loads.TryGetValue(wb.LoadId, out var load) ? load.Name : "Empty",
                    DestCode = dest?.Code ?? wb.Dest,
                    DestName = dest?.Name ?? wb.Dest,
                    Location = car.LastSeen == null ? "not seen yet" : $"{car.LastSeen.Track} {car.LastSeen.Span.ToString("F0", CultureInfo.InvariantCulture)} m",
                    Pay = wb.Pay,
                };
                switch (wb.State)
                {
                    case WaybillState.Open:
                        row.Status = engine.IsAtDestination(wb) ? "spotting" : $"to {row.DestCode}";
                        list.ToMove.Add(row);
                        break;
                    case WaybillState.Servicing:
                        row.Status = ServicingStatus(engine, wb, car, dest as IndustryComponent, out var held);
                        row.Held = held;
                        list.AtIndustries.Add(row);
                        break;
                    default:
                        row.Status = "awaiting pickup";
                        list.AwaitingPickup.Add(row);
                        break;
                }
            }
            list.ToMove.Sort((a, b) => string.CompareOrdinal(a.Location, b.Location));
            list.AtIndustries.Sort((a, b) => string.CompareOrdinal(a.DestCode, b.DestCode));
            list.AwaitingPickup.Sort((a, b) => string.CompareOrdinal(a.DestCode, b.DestCode));

            foreach (var x in data.Interchanges)
            {
                var st = state.Interchanges[x.Id];
                if (st.Pending != null)
                    list.Services.Add($"{x.Name}: service {st.Pending.Id} {(st.Pending.LastDeferral != null ? "delayed: " + st.Pending.LastDeferral : "in progress")}");
                else
                {
                    var wait = Math.Max(0.0, st.NextServiceAt - engine.Now);
                    list.Services.Add($"{x.Name}: next service {OpsClock.Format(state.Clock, st.NextServiceAt)} (in {(int)(wait / 60)} h {(int)(wait % 60):D2} min)");
                }
            }
            foreach (var industry in data.Industries)
            {
                var parts = industry.Storage.Keys.Select(loadId =>
                    $"{data.Loads[loadId].Name} {engine.Storage(industry.Id, loadId).ToString("F1", CultureInfo.InvariantCulture)}/{engine.StorageCap(industry.Id, loadId).ToString("F1", CultureInfo.InvariantCulture)}");
                list.Storage.Add($"{industry.Name}: {string.Join(", ", parts)}");
            }
            return list;
        }

        private static string ServicingStatus(OpsEngine engine, Waybill wb, CarRecord car, IndustryComponent component, out bool held)
        {
            held = false;
            if (component == null)
                return "servicing";
            var verb = component.Type == ComponentType.Unloader ? "unloading" : "loading";
            var percent = (car.Progress * 100.0).ToString("F0", CultureInfo.InvariantCulture);
            if (!engine.IsAtDestination(wb))
                return $"{verb} paused at {percent}%: return it to {component.Code}";
            var level = engine.Storage(component.Industry.Id, component.LoadId);
            var cap = engine.StorageCap(component.Industry.Id, component.LoadId);
            if (component.Type == ComponentType.Unloader && level >= cap - 1e-6)
            {
                held = true;
                return $"held at {percent}%: storage full";
            }
            if (component.Type == ComponentType.Loader && level <= 1e-6)
            {
                held = true;
                return $"held at {percent}%: waiting for product";
            }
            return $"{verb} {percent}%";
        }
    }
}
