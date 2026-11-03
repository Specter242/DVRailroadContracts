using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Contracts.Core.Data;
using Contracts.Core.State;

namespace Contracts.Core.Sim
{
    /// <summary>
    /// Plain text on a fixed character grid. The office board and the waybill
    /// sheets print in a monospaced face (operations-design.md, 20.5), so a
    /// line of N characters is exactly N cells wide, and the wrapping here
    /// decides every line the player reads. The papers print ASCII only, which
    /// every game font has.
    /// </summary>
    public static class TextGrid
    {
        public const string Ellipsis = "...";

        /// <summary>
        /// Word-wraps <paramref name="text"/> into lines of at most
        /// <paramref name="width"/> characters. A line's leading spaces are
        /// kept, and its continuation lines are indented by those spaces plus
        /// <paramref name="hanging"/>. A word longer than a line breaks hard.
        /// Newlines start new paragraphs; trailing spaces are dropped.
        /// </summary>
        public static List<string> Wrap(string text, int width, int hanging = 0)
        {
            if (width < 1)
                throw new ArgumentOutOfRangeException(nameof(width), "a line holds at least one character");
            var lines = new List<string>();
            foreach (var paragraph in (text ?? "").Replace("\r\n", "\n").Split('\n'))
                WrapParagraph(paragraph, width, Math.Max(0, hanging), lines);
            return lines;
        }

        private static void WrapParagraph(string paragraph, int width, int hanging, List<string> lines)
        {
            var lead = Math.Min(paragraph.Length - paragraph.TrimStart(' ').Length, width - 1);
            var continuation = new string(' ', Math.Min(lead + hanging, width / 2));
            var line = new StringBuilder(new string(' ', lead));
            var hasWord = false;
            foreach (var word in paragraph.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var rest = word;
                while (rest.Length > 0)
                {
                    var separator = hasWord ? 1 : 0;
                    if (line.Length + separator + rest.Length <= width)
                    {
                        if (hasWord)
                            line.Append(' ');
                        line.Append(rest);
                        hasWord = true;
                        break;
                    }
                    if (hasWord)
                    {
                        lines.Add(line.ToString());
                        line.Clear().Append(continuation);
                        hasWord = false;
                        continue;
                    }
                    // A word wider than the whole line: break it where the line ends.
                    var room = width - line.Length;
                    line.Append(rest, 0, room);
                    lines.Add(line.ToString());
                    line.Clear().Append(continuation);
                    rest = rest.Substring(room);
                }
            }
            if (hasWord || lines.Count == 0 || paragraph.Trim().Length == 0)
                lines.Add(line.ToString().TrimEnd());
        }

        /// <summary>The text cut to <paramref name="width"/> characters, ending in "..." when cut.</summary>
        public static string Fit(string text, int width)
        {
            text = text ?? "";
            if (text.Length <= width)
                return text;
            return width <= Ellipsis.Length ? text.Substring(0, Math.Max(0, width)) : text.Substring(0, width - Ellipsis.Length).TrimEnd() + Ellipsis;
        }

        /// <summary>One line with <paramref name="right"/> flush right; the left part is cut first when both do not fit.</summary>
        public static string Spread(string left, string right, int width)
        {
            right = Fit(right ?? "", width);
            var room = width - right.Length - 1;
            if (room < 1)
                return right;
            left = Fit(left ?? "", room);
            return left + new string(' ', width - left.Length - right.Length) + right;
        }

        /// <summary>"label  value" with the value wrapped beside a label column of <paramref name="labelWidth"/>.</summary>
        public static IEnumerable<string> Labeled(string label, string value, int width, int labelWidth)
        {
            var pad = new string(' ', labelWidth);
            var first = true;
            foreach (var line in Wrap(value, width - labelWidth))
            {
                yield return ((first ? (label ?? "").PadRight(labelWidth) : pad) + line).TrimEnd();
                first = false;
            }
        }
    }

    /// <summary>
    /// One waybill as its paper sheet prints it (operations-design.md, 20.5):
    /// a heading, a two-line title in large type that reads at a glance (the
    /// car and where it goes), and the details. Built
    /// from the state; the runtime only draws it, and prints it again when
    /// <see cref="Key"/> changes.
    /// </summary>
    public sealed class WaybillSheet
    {
        /// <summary>Characters per line of the heading and the body.</summary>
        public const int Columns = 36;

        /// <summary>Characters per title line, which prints at twice the body's size.</summary>
        public const int TitleColumns = 18;

        /// <summary>The most body lines a sheet holds.</summary>
        public const int BodyRows = 20;

        public const int LabelWidth = 7;

        public string WaybillId { get; private set; }

        public string CarGuid { get; private set; }

        /// <summary>"MURPHY BRANCH WAYBILL   MB-000124".</summary>
        public string Heading { get; private set; }

        /// <summary>Two lines in large type: "SOU 4834  XM" and "TO AHW-G3A".</summary>
        public List<string> Title { get; } = new List<string>();

        public List<string> Body { get; } = new List<string>();

        /// <summary>Whether the car still has to be moved: only such sheets are laid out at an office.</summary>
        public bool ToMove { get; private set; }

        /// <summary>The destination area's color ("#B5462E"), for the title band, or null.</summary>
        public string BandColor { get; private set; }

        /// <summary>Everything printed; the runtime prints the sheet again when it changes.</summary>
        public string Key => BandColor + "\n" + Heading + "\n" + string.Join("\n", Title) + "\n" + string.Join("\n", Body);

        /// <summary>The sheet of an open waybill, or null when the state holds no such waybill (it closed).</summary>
        public static WaybillSheet Build(OpsEngine engine, string waybillId)
        {
            if (waybillId == null || !engine.State.Waybills.TryGetValue(waybillId, out var wb))
                return null;
            var data = engine.Data;
            engine.State.Cars.TryGetValue(wb.CarGuid, out var car);
            var origin = data.Destination(wb.Origin);
            var dest = data.Destination(wb.Dest);
            var sheet = new WaybillSheet
            {
                WaybillId = wb.Id,
                CarGuid = wb.CarGuid,
                Heading = TextGrid.Spread("MURPHY BRANCH WAYBILL", wb.Id, Columns),
                ToMove = wb.State == WaybillState.Open,
                BandColor = data.Area(OfficeView.AreaOf(data, dest))?.Color,
            };
            var mark = car?.Mark ?? "UNKNOWN CAR";
            var classId = car?.ClassId ?? "";
            sheet.Title.Add(TextGrid.Fit(mark.Length + 2 + classId.Length <= TitleColumns ? $"{mark}  {classId}".TrimEnd() : mark, TitleColumns));
            sheet.Title.Add(TextGrid.Fit("TO " + (dest?.Code ?? wb.Dest), TitleColumns));

            var body = sheet.Body;
            void Add(string label, string value) => body.AddRange(TextGrid.Labeled(label, value, Columns, LabelWidth));

            Add("LOAD", LoadText(data, wb, dest));
            if (car != null)
                Add("CAR", data.CarClasses.TryGetValue(car.ClassId, out var carClass) && !string.IsNullOrEmpty(carClass.Name) ? $"{carClass.Name} ({car.ClassId})" : car.ClassId);
            Add("FROM", PlaceText(data, origin, wb.Origin));
            Add("TO", PlaceText(data, dest, wb.Dest));
            var spots = (dest?.Spots ?? Array.Empty<Spot>()).Take(3).ToList();
            for (var i = 0; i < spots.Count; i++)
            {
                Add(i == 0 ? "SPOT" : "", spots[i].RuntimeName);
                Add("", $"{Meters(spots[i].From)}-{Meters(spots[i].To)} m");
            }
            if (wb.PaidAt.HasValue)
                Add("PAID", $"${Money(wb.Pay + wb.Bonus)}{(wb.Bonus > 0 ? $" incl. ${Money(wb.Bonus)} bonus" : "")}{(wb.Late ? ", late" : "")}");
            else
            {
                Add("PAY", $"${Money(wb.Pay)} when spotted");
                var industry = (dest as IndustryComponent)?.Industry;
                var bonus = industry != null && engine.UnderContract(industry) ? OpsEngine.BonusPercent(engine.Level(industry)) : 0.0;
                if (bonus > 0 && wb.Pay > 0)
                    Add("BONUS", $"+{Percent(bonus)}/+{Percent(bonus / 2)}/+{Percent(bonus / 4)}% on days 1/2/3");
            }
            Add("BILLED", OpsClock.Stamp(engine.State.Clock, wb.Created));
            if (!wb.PaidAt.HasValue && engine.ContractsOn && wb.Pay > 0)
                Add("DUE", $"{OpsClock.Stamp(engine.State.Clock, wb.Created + engine.Data.Settings.Contracts.DueHours * 60.0)}; half pay after");
            if (wb.State == WaybillState.Servicing)
                Add("STATUS", (dest as IndustryComponent)?.Type == ComponentType.Loader ? "spotted; loading" : "spotted; unloading");
            else if (wb.State == WaybillState.AwaitingPickup)
                Add("STATUS", "spotted; awaiting pickup");

            if (body.Count > BodyRows)
            {
                body.RemoveRange(BodyRows - 1, body.Count - BodyRows + 1);
                body.Add(TextGrid.Ellipsis);
            }
            return sheet;
        }

        private static string LoadText(OpsData data, Waybill wb, IDestination dest)
        {
            string Name(string loadId) => loadId != null && data.Loads.TryGetValue(loadId, out var load) ? load.Name : loadId;
            switch (wb.Kind)
            {
                case WaybillKind.InboundEmpty:
                    var component = dest as IndustryComponent;
                    return component?.LoadId != null ? $"Empty, to load {Name(component.LoadId).ToLowerInvariant()}" : "Empty";
                case WaybillKind.OutboundEmpty:
                    return "Empty, going home";
                default:
                    return Name(wb.LoadId) ?? "Empty";
            }
        }

        private static string PlaceText(OpsData data, IDestination place, string id)
        {
            switch (place)
            {
                case IndustryComponent component:
                    var area = data.Area(component.Industry.AreaId)?.Name;
                    return $"{component.Industry.Name} ({component.Code}){(area != null ? ", " + area : "")}";
                case Interchange interchange:
                    return $"{interchange.Name} ({interchange.Code})";
                default:
                    return id ?? "unknown";
            }
        }

        private static string Meters(double m) => m.ToString("F0", CultureInfo.InvariantCulture);

        internal static string Money(double amount) => amount.ToString("N0", CultureInfo.InvariantCulture);

        internal static string Percent(double percent) => percent.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>What an office board prints and which waybill sheets the office lays out (operations-design.md, 20.5).</summary>
    public sealed class BoardPlan
    {
        /// <summary>"BRYSON CITY OFFICE", painted on the board.</summary>
        public string Title { get; set; }

        /// <summary>"also serving Ela, Connelly Creek", or empty.</summary>
        public string Subtitle { get; set; }

        /// <summary>The posted card: at most the grid's rows, each at most its columns.</summary>
        public List<string> Card { get; } = new List<string>();

        /// <summary>The card without its posting time: the runtime posts it again when this changes.</summary>
        public string CardKey { get; set; }

        /// <summary>Place index to the waybill whose sheet lies there, or null for a free place.</summary>
        public string[] Slots { get; set; } = new string[0];

        /// <summary>Waybills to move whose sheets found no free place, oldest first.</summary>
        public List<string> Waiting { get; } = new List<string>();

        public int ToMove { get; set; }

        public int Taken { get; set; }

        public int Servicing { get; set; }

        public int AwaitingPickup { get; set; }
    }

    /// <summary>
    /// The office's papers (operations-design.md, 20.5): the board's posted
    /// contracts card on a fixed character grid, and the places for waybill
    /// sheets on the office table (or a stand's ledge). Each waybill still to
    /// be moved gets a sheet laid out in a place, oldest first; a sheet keeps
    /// its place while it lies there, so taking one does not reshuffle the
    /// others; a taken sheet frees its place for the next.
    /// </summary>
    public static class OfficeBoard
    {
        public const int CardColumns = 84;

        public const int CardRows = 20;

        /// <summary>The office's waybills still to be moved and not taken, oldest first.</summary>
        public static List<string> ToLayOut(OpsEngine engine, OfficeView view, ICollection<string> taken) =>
            view.Sheets
                .Select(r => engine.State.Waybills.TryGetValue(r.WaybillId, out var w) ? w : null)
                .Where(w => w != null && w.State == WaybillState.Open && (taken == null || !taken.Contains(w.Id)))
                .OrderBy(w => w.Created).ThenBy(w => w.Id, StringComparer.Ordinal)
                .Select(w => w.Id).Distinct().ToList();

        /// <summary>
        /// Places for the <paramref name="pinnable"/> waybills: each keeps its
        /// previous place while it may still lie out; the others fill the free
        /// places in order, lowest first. No waybill takes two places.
        /// </summary>
        public static string[] AssignSlots(IReadOnlyList<string> previous, IReadOnlyList<string> pinnable, int capacity)
        {
            var slots = new string[Math.Max(0, capacity)];
            var wanted = new HashSet<string>(pinnable.Where(id => id != null), StringComparer.Ordinal);
            var placed = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; previous != null && i < Math.Min(previous.Count, slots.Length); i++)
            {
                if (previous[i] != null && wanted.Contains(previous[i]) && placed.Add(previous[i]))
                    slots[i] = previous[i];
            }
            var next = 0;
            foreach (var id in pinnable)
            {
                if (id == null || placed.Contains(id))
                    continue;
                while (next < slots.Length && slots[next] != null)
                    next++;
                if (next >= slots.Length)
                    break;
                slots[next] = id;
                placed.Add(id);
            }
            return slots;
        }

        /// <summary>
        /// The office's papers: the card (posted at <paramref name="posted"/>,
        /// an "HH:MM" time) and the sheets' places, <paramref name="capacity"/>
        /// of them, which lie <paramref name="place"/> ("on the table").
        /// <paramref name="taken"/> names the waybills whose sheets are not
        /// laid out at this office (in a hand, a pocket, or lying somewhere);
        /// they are not laid out again until those sheets are gone.
        /// </summary>
        public static BoardPlan Plan(OpsEngine engine, OfficeView view, IReadOnlyList<string> previousSlots, int capacity, ICollection<string> taken,
                                     string posted, int columns = CardColumns, int rows = CardRows, string place = "on the table")
        {
            var plan = new BoardPlan
            {
                Title = $"{view.AreaName.ToUpperInvariant()} OFFICE",
                Subtitle = view.AreaNames.Count > 1 ? "also serving " + string.Join(", ", view.AreaNames.Skip(1)) : "",
            };
            var pinnable = ToLayOut(engine, view, taken);
            plan.Slots = AssignSlots(previousSlots, pinnable, capacity);
            var pinned = new HashSet<string>(plan.Slots.Where(s => s != null), StringComparer.Ordinal);
            plan.Waiting.AddRange(pinnable.Where(id => !pinned.Contains(id)));

            var waybills = view.Sheets.Select(r => engine.State.Waybills.TryGetValue(r.WaybillId, out var w) ? w : null).Where(w => w != null).GroupBy(w => w.Id).Select(g => g.First()).ToList();
            plan.ToMove = waybills.Count(w => w.State == WaybillState.Open);
            plan.Taken = waybills.Count(w => w.State == WaybillState.Open && taken != null && taken.Contains(w.Id));
            plan.Servicing = waybills.Count(w => w.State == WaybillState.Servicing);
            plan.AwaitingPickup = waybills.Count(w => w.State == WaybillState.AwaitingPickup);

            var contracts = new List<string>();
            foreach (var contract in view.Contracts)
            {
                foreach (var line in ContractLines(contract))
                    contracts.AddRange(TextGrid.Wrap(line, columns, 2));
            }
            var summary = TextGrid.Wrap(
                $"WAYBILLS: {plan.ToMove} to move ({pinned.Count} {place}{(plan.Taken > 0 ? $", {plan.Taken} taken" : "")}{(plan.Waiting.Count > 0 ? $", {plan.Waiting.Count} waiting for room" : "")}), " +
                $"{plan.Servicing} at industries, {plan.AwaitingPickup} awaiting pickup", columns, 2);
            foreach (var line in Deliveries(engine, view))
                summary.AddRange(TextGrid.Wrap(line, columns, 4));
            var waiting = new List<string>();
            foreach (var id in plan.Waiting)
            {
                var row = view.Sheets.First(r => r.WaybillId == id);
                waiting.Add($"  waiting: {row.Mark} {row.ClassId} {row.Load} > {row.DestCode}, ${WaybillSheet.Money(row.Pay)}");
            }

            // The header takes one row. The waybill summary always prints; the
            // contracts give way to it, and the waiting list gives way to both.
            const string header = "CONTRACTS - changes take effect at midnight";
            var room = Math.Max(1, rows - 1);
            var body = new List<string>();
            if (contracts.Count + summary.Count > room)
            {
                body.AddRange(contracts.Take(Math.Max(0, room - summary.Count - 1)));
                body.Add("... more contracts in the radio's OFFICE mode");
                body.AddRange(summary);
                body = body.Take(room).ToList();
            }
            else
            {
                body.AddRange(contracts);
                body.AddRange(summary);
                var left = room - body.Count;
                if (waiting.Count <= left)
                    body.AddRange(waiting);
                else if (left >= 2)
                {
                    body.AddRange(waiting.Take(left - 1));
                    body.Add($"  ... {waiting.Count - (left - 1)} more waiting; see the switch list");
                }
            }
            plan.CardKey = header + "\n" + string.Join("\n", body);
            plan.Card.Add(TextGrid.Spread(header, "posted " + posted, columns));
            plan.Card.AddRange(body.Select(l => TextGrid.Fit(l, columns)));
            return plan;
        }

        /// <summary>
        /// When the next cars come for the office's industries: one line per
        /// interchange that serves them ("  next delivery: East Whittier
        /// Interchange at 17:00"). An interchange places cars only while
        /// players stand clear, and only as many as fit; orders left over wait
        /// for its next service.
        /// </summary>
        public static IEnumerable<string> Deliveries(OpsEngine engine, OfficeView view)
        {
            var data = engine.Data;
            var industries = new HashSet<string>(view.Contracts.Select(c => c.IndustryId), StringComparer.Ordinal);
            var interchanges = data.Industries.Where(i => industries.Contains(i.Id)).SelectMany(i => i.Components)
                .Select(data.ServingInterchange).Where(x => x != null).Distinct().OrderBy(x => x.Id, StringComparer.Ordinal);
            foreach (var x in interchanges)
            {
                if (!engine.State.Interchanges.TryGetValue(x.Id, out var st))
                    continue;
                yield return st.Pending != null
                    ? $"  next delivery: {x.Name}, {(st.Pending.LastDeferral != null ? "delayed: " + st.Pending.LastDeferral : "in progress")}"
                    : $"  next delivery: {x.Name} at {OpsClock.Format(engine.State.Clock, st.NextServiceAt)}";
            }
        }

        /// <summary>One contract as the card prints it: terms, standing, then traffic.</summary>
        public static IEnumerable<string> ContractLines(ContractLine c)
        {
            var terms = new StringBuilder($"{c.Industry}: {OfficeView.LevelText(c)}");
            if (c.UnderContract && c.BonusPercent > 0)
                terms.Append($"; quick-delivery bonus {WaybillSheet.Percent(c.BonusPercent)}/{WaybillSheet.Percent(c.BonusPercent / 2)}/{WaybillSheet.Percent(c.BonusPercent / 4)}%");
            if (c.UnderContract && c.Requested.HasValue)
                terms.Append(c.Requested.Value == 0 ? "; ENDS at midnight" : $"; level {c.Requested.Value} at midnight");
            yield return terms.ToString();
            if (c.UnderContract)
            {
                var standing = new StringBuilder($"  performance {OfficeView.PerformanceText(c)}");
                standing.Append(c.Level > 0 && c.MaxLevel > c.Level ? $"; may raise to level {c.MaxLevel}"
                    : c.Level > 0 ? "; raising needs 3 days over 90%"
                    : $"; may take on up to level {c.MaxLevel}");
                if (c.LowerPenalty > 0)
                    standing.Append($"; lowering costs ${WaybillSheet.Money(c.LowerPenalty)}");
                yield return standing.ToString();
            }
            yield return $"  {c.OpenWaybills} open waybill{(c.OpenWaybills == 1 ? "" : "s")}{(c.OpenWaybills > 0 ? $", oldest {c.OldestDays.ToString("F1", CultureInfo.InvariantCulture)} days" : "")}; storage {c.Storage}";
        }
    }
}
