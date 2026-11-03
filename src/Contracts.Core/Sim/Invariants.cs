using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Data;
using Contracts.Core.State;

namespace Contracts.Core.Sim
{
    /// <summary>
    /// The state invariants of operations-design.md, 7.3, checked after load
    /// and by the tests after every step.
    /// </summary>
    public static class Invariants
    {
        public static List<string> Check(OpsData data, OpsState state)
        {
            var problems = new List<string>();
            var carsWithWaybills = new HashSet<string>();
            foreach (var wb in state.Waybills.Values)
            {
                if (!state.Cars.TryGetValue(wb.CarGuid ?? "", out var car))
                    problems.Add($"{wb.Id} references missing car {wb.CarGuid}");
                else if (car.WaybillId != wb.Id)
                    problems.Add($"{wb.Id} belongs to {car.Mark}, whose waybill is {car.WaybillId ?? "none"}");
                if (!carsWithWaybills.Add(wb.CarGuid ?? ""))
                    problems.Add($"car {wb.CarGuid} has more than one open waybill");
                if (data.Destination(wb.Dest) == null)
                    problems.Add($"{wb.Id} destination '{wb.Dest}' is not in the data");
                if (data.Destination(wb.Origin) == null)
                    problems.Add($"{wb.Id} origin '{wb.Origin}' is not in the data");
                if ((wb.State == WaybillState.Open) != (wb.PaidAt == null))
                    problems.Add($"{wb.Id} is {wb.State} but {(wb.PaidAt == null ? "unpaid" : "paid")}");
                if (wb.State == WaybillState.Servicing && !(data.Destination(wb.Dest) is IndustryComponent))
                    problems.Add($"{wb.Id} is servicing at '{wb.Dest}', which is not an industry");
                if (wb.State == WaybillState.AwaitingPickup && !(data.Destination(wb.Dest) is Interchange))
                    problems.Add($"{wb.Id} awaits pickup at '{wb.Dest}', which is not an interchange");
                if (wb.IsLoad != (wb.LoadId != null))
                    problems.Add($"{wb.Id} is {wb.Kind} with load '{wb.LoadId}'");
                if (car != null && car.LoadId != wb.LoadId)
                    problems.Add($"{wb.Id} carries '{wb.LoadId ?? "empty"}' but {car.Mark} holds '{car.LoadId ?? "empty"}'");
            }
            foreach (var car in state.Cars.Values)
            {
                if (car.WaybillId != null && !state.Waybills.ContainsKey(car.WaybillId))
                    problems.Add($"{car.Mark} names missing waybill {car.WaybillId}");
                if (car.Progress < 0.0 || car.Progress > 1.0)
                    problems.Add($"{car.Mark} progress {car.Progress} is outside 0–1");
                if (car.Progress > 0.0)
                {
                    var wb = car.WaybillId != null && state.Waybills.TryGetValue(car.WaybillId, out var w) ? w : null;
                    if (wb == null || wb.State != WaybillState.Servicing)
                        problems.Add($"{car.Mark} has progress {car.Progress:F3} without a servicing waybill");
                }
                if (!data.CarClasses.ContainsKey(car.ClassId ?? ""))
                    problems.Add($"{car.Mark} has unknown class '{car.ClassId}'");
            }
            foreach (var industry in data.Industries)
            {
                if (!state.Industries.TryGetValue(industry.Id, out var s))
                {
                    problems.Add($"industry '{industry.Id}' has no state");
                    continue;
                }
                foreach (var cap in industry.Storage)
                {
                    var level = s.Storage.TryGetValue(cap.Key, out var v) ? v : 0.0;
                    if (level < -1e-9 || level > cap.Value + 1e-9)
                        problems.Add($"industry '{industry.Id}' {cap.Key} storage {level} is outside 0–{cap.Value}");
                }
            }
            foreach (var pair in state.Industries)
            {
                var industry = pair.Value;
                if (industry.Level < 0 || industry.Level > 5)
                    problems.Add($"industry '{pair.Key}' contract level {industry.Level} is outside 0–5");
                if (industry.RequestedLevel is int requested && (requested < 0 || requested > 5))
                    problems.Add($"industry '{pair.Key}' requested level {requested} is outside 0–5");
                if (industry.History.Count > 7 || industry.History.Any(h => h.Value < 0 || h.Value > 1))
                    problems.Add($"industry '{pair.Key}' performance history is malformed");
            }
            foreach (var pair in state.Interchanges.Where(p => p.Value.Pending != null))
            {
                foreach (var car in pair.Value.Pending.Cars.Where(c => data.Destination(c.Dest) == null))
                    problems.Add($"service {pair.Value.Pending.Id} plans {car.Mark} for unknown '{car.Dest}'");
            }
            return problems;
        }
    }
}
