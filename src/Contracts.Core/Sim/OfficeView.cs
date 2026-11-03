using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Contracts.Core.Data;
using Contracts.Core.State;

namespace Contracts.Core.Sim
{
    /// <summary>One industry's standing contract as the office shows it.</summary>
    public sealed class ContractLine
    {
        public string IndustryId { get; set; }

        public string Industry { get; set; }

        public bool UnderContract { get; set; }

        /// <summary>0 (none) to 5.</summary>
        public int Level { get; set; }

        /// <summary>The level's factor on traffic and pay, 0–1.</summary>
        public double Factor { get; set; }

        /// <summary>The level asked for at the next midnight, or null.</summary>
        public int? Requested { get; set; }

        /// <summary>Mean daily performance over the history, or null without any.</summary>
        public double? Performance { get; set; }

        public int HistoryDays { get; set; }

        /// <summary>The highest level the player may ask for now.</summary>
        public int MaxLevel { get; set; }

        /// <summary>The quick-delivery bonus at this level, percent of base pay on the first day.</summary>
        public double BonusPercent { get; set; }

        /// <summary>The penalty for lowering one level now.</summary>
        public double LowerPenalty { get; set; }

        public int OpenWaybills { get; set; }

        /// <summary>Age in days of the oldest open waybill, or 0.</summary>
        public double OldestDays { get; set; }

        public string Storage { get; set; }
    }

    /// <summary>
    /// What an area's office shows (operations-design.md, 20.3): each
    /// industry's standing contract (level, performance, what may change at
    /// midnight) and the area's open waybills as job sheets. The runtime only
    /// draws it, on the office board, the radio's office mode, or the console.
    /// </summary>
    public sealed class OfficeView
    {
        /// <summary>The first area the office serves; the office is named after it.</summary>
        public string AreaId { get; set; }

        public string AreaName { get; set; }

        /// <summary>Every area the office serves (an area without its own office is served by another's).</summary>
        public List<string> AreaIds { get; } = new List<string>();

        public List<string> AreaNames { get; } = new List<string>();

        public string Clock { get; set; }

        public List<ContractLine> Contracts { get; } = new List<ContractLine>();

        /// <summary>The area's open waybills: cars bound to or from its industries and interchanges.</summary>
        public List<SwitchListRow> Sheets { get; } = new List<SwitchListRow>();

        public static OfficeView Build(OpsEngine engine, string areaId) => Build(engine, new[] { areaId });

        public static OfficeView Build(OpsEngine engine, IReadOnlyList<string> areaIds)
        {
            var data = engine.Data;
            var area = data.Area(areaIds.FirstOrDefault());
            var view = new OfficeView
            {
                AreaId = area?.Id ?? areaIds.FirstOrDefault(),
                AreaName = area?.Name ?? areaIds.FirstOrDefault(),
                Clock = OpsClock.Format(engine.State.Clock, engine.Now),
            };
            view.AreaIds.AddRange(areaIds);
            view.AreaNames.AddRange(areaIds.Select(id => data.Area(id)?.Name ?? id));
            bool InArea(string destinationId) => areaIds.Contains(AreaOf(data, data.Destination(destinationId)));

            foreach (var industry in data.Industries.Where(i => areaIds.Contains(i.AreaId)))
            {
                var s = engine.State.Industries[industry.Id];
                var ids = new HashSet<string>(industry.Components.Select(c => c.FullId));
                var open = engine.State.Waybills.Values.Where(w => w.State == WaybillState.Open && (ids.Contains(w.Dest) || ids.Contains(w.Origin))).ToList();
                view.Contracts.Add(new ContractLine
                {
                    IndustryId = industry.Id,
                    Industry = industry.Name,
                    UnderContract = engine.UnderContract(industry),
                    Level = s.Level,
                    Factor = engine.ContractFactor(industry),
                    Requested = s.RequestedLevel,
                    Performance = engine.AveragePerformance(industry),
                    HistoryDays = s.History.Count,
                    MaxLevel = engine.UnderContract(industry) ? engine.MaxAvailableLevel(industry) : 0,
                    BonusPercent = engine.UnderContract(industry) ? OpsEngine.BonusPercent(s.Level) : 0.0,
                    LowerPenalty = engine.UnderContract(industry) && s.Level > 0 ? engine.PenaltyFor(industry, s.Level - 1) : 0.0,
                    OpenWaybills = open.Count,
                    OldestDays = open.Count == 0 ? 0.0 : open.Max(w => engine.Now - w.Created) / OpsClock.MinutesPerDay,
                    Storage = string.Join(", ", industry.Storage.Keys.Select(load =>
                        $"{data.Loads[load].Name.ToLowerInvariant()} {engine.Storage(industry.Id, load).ToString("F1", CultureInfo.InvariantCulture)}/{engine.StorageCap(industry.Id, load).ToString("F1", CultureInfo.InvariantCulture)}")),
                });
            }
            var list = SwitchList.Build(engine);
            foreach (var row in list.ToMove.Concat(list.AtIndustries).Concat(list.AwaitingPickup))
            {
                var wb = engine.State.Waybills.TryGetValue(row.WaybillId, out var w) ? w : null;
                if (wb != null && (InArea(wb.Dest) || InArea(wb.Origin)))
                    view.Sheets.Add(row);
            }
            return view;
        }

        public static string AreaOf(OpsData data, IDestination destination)
        {
            switch (destination)
            {
                case IndustryComponent component:
                    return component.Industry.AreaId;
                case Interchange interchange:
                    return interchange.AreaId;
                default:
                    return null;
            }
        }

        /// <summary>"level 3 (49%)", or "no contract".</summary>
        public static string LevelText(ContractLine c) =>
            !c.UnderContract ? "no contract needed" : c.Level == 0 ? "no contract" : $"level {c.Level} ({c.Factor * 100:F0}%)";

        public static string PerformanceText(ContractLine c) =>
            c.Performance.HasValue ? $"{c.Performance.Value * 100:F0}% over {c.HistoryDays} day{(c.HistoryDays == 1 ? "" : "s")}" : "no history yet";

        /// <summary>The office as plain text, for the office board and the console.</summary>
        public string Render(int maxSheets = 12)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{AreaName.ToUpperInvariant()} OFFICE   {Clock}");
            var others = AreaNames.Skip(1).ToList();
            if (others.Count > 0)
                sb.AppendLine($"also serving {string.Join(", ", others)}");
            sb.AppendLine("CONTRACTS (changes take effect at midnight)");
            foreach (var c in Contracts)
            {
                sb.Append($"  {c.Industry}: {LevelText(c)}");
                if (c.UnderContract)
                {
                    sb.Append($", performance {PerformanceText(c)}");
                    if (c.BonusPercent > 0)
                        sb.Append($", quick-delivery bonus {c.BonusPercent:0.#}% day 1, {c.BonusPercent / 2:0.#}% day 2, {c.BonusPercent / 4:0.##}% day 3");
                    if (c.Requested.HasValue)
                        sb.Append(c.Requested.Value == 0 ? "; ENDS at midnight" : $"; → level {c.Requested.Value} at midnight");
                    sb.Append(c.Level > 0 && c.MaxLevel > c.Level ? $"; may raise to level {c.MaxLevel}"
                        : c.Level > 0 ? "; raising needs 3 days over 90%"
                        : $"; may take on up to level {c.MaxLevel}");
                    if (c.LowerPenalty > 0)
                        sb.Append($"; lowering costs ${Money(c.LowerPenalty)}");
                }
                sb.AppendLine();
                sb.AppendLine($"    {c.OpenWaybills} open waybills{(c.OpenWaybills > 0 ? $", oldest {c.OldestDays:F1} days" : "")}; storage {c.Storage}");
            }
            sb.AppendLine(Sheets.Count == 0 ? "WAYBILLS: none open" : $"WAYBILLS ({Sheets.Count})");
            foreach (var r in Sheets.Take(maxSheets))
                sb.AppendLine($"  {r.Mark} {r.ClassId} {r.Load} > {r.DestCode}: {r.Status}, ${Money(r.Pay)}");
            if (Sheets.Count > maxSheets)
                sb.AppendLine($"  … {Sheets.Count - maxSheets} more on the switch list");
            return sb.ToString().TrimEnd();
        }

        private static string Money(double amount) => amount.ToString("N0", CultureInfo.InvariantCulture);
    }
}
