using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>Where a contract car is in its work: the runtime's stages, by the same names.</summary>
    public enum CarStage
    {
        Idle,
        ToShipper,
        Loading,
        ToReceiver,
        Unloading,
        ToInterchange,
        OffMap,
        BeingLoaded,
        BeingUnloaded,
    }

    /// <summary>Which of a contract's traffic a car is, as its cars a day: out, in or waste; or the bridge's.</summary>
    public enum CarLine
    {
        Out,
        In,
        Waste,
        Bridge,
    }

    /// <summary>What a contract car needs next, in the order of its work.</summary>
    public enum CarStep
    {
        /// <summary>Its job hauls it to its industry's station: an empty to load, or a load to unload.</summary>
        HaulTo,

        /// <summary>At its industry's station: its job spots it on the industry's track.</summary>
        Spot,

        /// <summary>At its industry's station, without a job: spotted once there is room on the industry's track.</summary>
        WaitForRoom,

        BeingLoaded,
        BeingUnloaded,

        /// <summary>A load its job hauls on from the industry to a receiver on the map.</summary>
        HaulAway,

        /// <summary>Its job takes it to an interchange track, to leave the map.</summary>
        TakeOut,

        /// <summary>Set out on an interchange track: the 06:00 service takes it away.</summary>
        Pickup,

        /// <summary>Its job ended unfinished: the 06:00 service plans it again.</summary>
        JobEnded,

        /// <summary>Set out, then moved off the interchange: the 06:00 service plans it again.</summary>
        MovedOff,

        /// <summary>No work: the 06:00 service plans it.</summary>
        NotPlanned,
    }

    /// <summary>What the runtime knows of a contract car: its waybill, its job, and where it stands.</summary>
    public sealed class BilledCar
    {
        /// <summary>Its number, as on the car.</summary>
        public string Id { get; set; }

        /// <summary>Its job's ID, or null with none.</summary>
        public string Job { get; set; }

        /// <summary>The contract its waybill is billed to.</summary>
        public string Contract { get; set; }

        /// <summary>Where it goes next: an industry or interchange ID.</summary>
        public string Next { get; set; }

        /// <summary>Its load, or the load it goes to take on; null for an empty with none planned.</summary>
        public string Cargo { get; set; }

        public string CarType { get; set; }
        public CarStage Stage { get; set; }

        /// <summary>Without a job at its industry's station, waiting for room on the industry's track.</summary>
        public bool WaitsToSpot { get; set; }

        /// <summary>It stands on an interchange track.</summary>
        public bool OnInterchange { get; set; }

        /// <summary>Where it stands, as the player reads it: an interchange's code or a station's.</summary>
        public string At { get; set; }

        /// <summary>Where it goes next, as the player reads it: a station's code or an interchange's.</summary>
        public string To { get; set; }

        /// <summary>When its industry will be done with it, or null.</summary>
        public string Note { get; set; }
    }

    /// <summary>A car as one contract sees it: its line, what it needs next, and where.</summary>
    public sealed class ContractCar
    {
        public ContractCar(string id, string job, CarLine line, CarStep step, string place, string toward = null, string note = null)
        {
            Id = id ?? "?";
            Job = string.IsNullOrEmpty(job) ? null : job;
            Line = line;
            Step = step;
            Place = place ?? "?";
            Toward = toward;
            Note = string.IsNullOrEmpty(note) ? null : note;
        }

        public string Id { get; }
        public string Job { get; }
        public CarLine Line { get; }
        public CarStep Step { get; }

        /// <summary>Where the step is: where it goes for a move, where it stands otherwise.</summary>
        public string Place { get; }

        /// <summary>For the bridge, the interchange it is billed to.</summary>
        public string Toward { get; }

        public string Note { get; }

        public bool ToMove => ContractCars.Moves(Step);
    }

    /// <summary>
    /// A contract's cars, counted and listed for the kiosk (Ace, Discord, 2026-11-08: "an easy way
    /// to keep track of cars in/out/waste ... sort it by most remaining moves to least"; owner:
    /// "Build it"). Cars are counted, not moves.
    ///
    /// - **Whose.** An industry's cars are those billed to it and those bound for it: a load from
    ///   one contract's industry to another's counts for both, out for one and in for the other,
    ///   as its bill counts toward both standings. The bridge's are those billed to it.
    /// - **Lines.** As its cars a day: an empty to load and its load are out (or waste, by its
    ///   cargo); a load to unload and its empty after are in. An empty with no load planned goes
    ///   by its car type: in if the industry takes anything in that type, else waste or out.
    ///   The bridge's cars go by the interchange they are billed to.
    /// - **To move.** A car still to move is billed for a move not yet made: to haul, to spot
    ///   (or waiting for room to), to take out, or its job ended unfinished. Cars at the industry,
    ///   set out for the pickup, or idle are listed but not counted.
    /// </summary>
    public static class ContractCars
    {
        /// <summary>Whether a step is a move the player still has to make.</summary>
        public static bool Moves(CarStep step)
        {
            switch (step)
            {
                case CarStep.HaulTo:
                case CarStep.Spot:
                case CarStep.WaitForRoom:
                case CarStep.HaulAway:
                case CarStep.TakeOut:
                case CarStep.JobEnded:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The car as an industry's contract sees it; null if it is neither billed to the industry nor bound for it.</summary>
        public static ContractCar Of(BilledCar car, Industry industry, IndustryData data)
        {
            if (car == null || industry == null)
                return null;
            var toHere = car.Next == industry.Id;
            if (!toHere && car.Contract != industry.Id)
                return null;
            return Make(car, LineOf(car, industry, data, toHere), toHere, null);
        }

        /// <summary>The car as the bridge's contract sees it; null if it isn't billed to the bridge.</summary>
        public static ContractCar Of(BilledCar car, Bridge bridge)
        {
            if (car == null || bridge == null || car.Contract != bridge.Id)
                return null;
            return Make(car, CarLine.Bridge, false, car.To);
        }

        private static ContractCar Make(BilledCar car, CarLine line, bool toHere, string toward)
        {
            var step = StepOf(car, toHere);
            var moving = Moves(step) || step == CarStep.BeingLoaded || step == CarStep.BeingUnloaded;
            var place = moving && step != CarStep.JobEnded ? car.To ?? car.At : car.At;
            return new ContractCar(car.Id, car.Job, line, step, place, toward, car.Note);
        }

        private static CarStep StepOf(BilledCar car, bool toHere)
        {
            switch (car.Stage)
            {
                case CarStage.Idle:
                    return CarStep.NotPlanned;
                case CarStage.OffMap:
                    return car.OnInterchange ? CarStep.Pickup : CarStep.MovedOff;
                case CarStage.BeingLoaded:
                    return CarStep.BeingLoaded;
                case CarStage.BeingUnloaded:
                    return CarStep.BeingUnloaded;
            }
            if (car.Job == null)
                return car.WaitsToSpot ? CarStep.WaitForRoom : CarStep.JobEnded;
            switch (car.Stage)
            {
                case CarStage.ToShipper:
                    return CarStep.HaulTo;
                case CarStage.ToReceiver:
                    return toHere ? CarStep.HaulTo : CarStep.HaulAway;
                case CarStage.Loading:
                case CarStage.Unloading:
                    return CarStep.Spot;
                default:
                    return CarStep.TakeOut;
            }
        }

        private static CarLine LineOf(BilledCar car, Industry industry, IndustryData data, bool toHere)
        {
            var role = car.Cargo == null ? (LineRole?)null : industry.Lines.FirstOrDefault(l => l.Cargo.Contains(car.Cargo))?.Role;
            bool Fits(LineRole r) => car.CarType != null && data != null &&
                                     industry.LinesOf(r).Any(l => l.Cargo.Any(c => data.CarTypes(c).Contains(car.CarType)));
            switch (car.Stage)
            {
                // An empty to load at its shipper.
                case CarStage.ToShipper:
                case CarStage.Loading:
                case CarStage.BeingLoaded:
                    if (role.HasValue)
                        return Line(role.Value);
                    return Fits(LineRole.Waste) && !Fits(LineRole.Product) ? CarLine.Waste : CarLine.Out;
                // A load to unload: in at its receiver, out (or waste) from the industry that shipped it.
                case CarStage.ToReceiver:
                case CarStage.Unloading:
                case CarStage.BeingUnloaded:
                    return toHere ? CarLine.In : role == LineRole.Waste ? CarLine.Waste : CarLine.Out;
                // Leaving, set out, or idle: a load by its cargo; an empty by its car type, the
                // empty of a load taken in first.
                default:
                    if (role.HasValue)
                        return Line(role.Value);
                    return Fits(LineRole.Material) ? CarLine.In
                        : Fits(LineRole.Waste) && !Fits(LineRole.Product) ? CarLine.Waste
                        : Fits(LineRole.Product) ? CarLine.Out
                        : CarLine.In;
            }
        }

        private static CarLine Line(LineRole role) =>
            role == LineRole.Material ? CarLine.In : role == LineRole.Waste ? CarLine.Waste : CarLine.Out;

        /// <summary>How many of the cars are still to move.</summary>
        public static int ToMove(IEnumerable<ContractCar> cars) => (cars ?? Enumerable.Empty<ContractCar>()).Count(c => c != null && c.ToMove);

        /// <summary>
        /// The kiosk's info line: the cars to move, "out / in / waste" as the contract's cars a day;
        /// for the bridge (<paramref name="bridgeEnds"/>, its interchanges as the player reads them),
        /// by where they go. At most 34 characters with two-digit counts.
        /// </summary>
        public static string ToMoveLine(IEnumerable<ContractCar> cars, IReadOnlyList<string> bridgeEnds = null) =>
            "To move: " + string.Join(" / ", Sections(cars, bridgeEnds).Select(s => $"{s.Cars.Count(c => c.ToMove)} {s.Label}"));

        /// <summary>
        /// The kiosk's Cars page: a section a line (out, in, waste; or the bridge's directions),
        /// each with its cars and how many are to move, then a sentence for each group of them by
        /// what they need next, where, and their job; null with no cars.
        /// </summary>
        public static string Page(IEnumerable<ContractCar> cars, IReadOnlyList<string> bridgeEnds = null)
        {
            var list = (cars ?? Enumerable.Empty<ContractCar>()).Where(c => c != null).ToList();
            if (list.Count == 0)
                return null;
            var sections = new List<string>();
            foreach (var (label, mine) in Sections(list, bridgeEnds))
            {
                var title = char.ToUpperInvariant(label[0]) + label.Substring(1);
                if (mine.Count == 0)
                {
                    sections.Add($"{title}: none.");
                    continue;
                }
                var lines = new List<string> { $"{title}: {mine.Count} car{(mine.Count == 1 ? "" : "s")}, {mine.Count(c => c.ToMove)} to move." };
                foreach (var g in mine.GroupBy(c => (c.Step, c.Place, c.Job))
                             .OrderBy(g => g.Key.Step)
                             .ThenBy(g => g.Key.Place, StringComparer.Ordinal)
                             .ThenBy(g => g.Key.Job ?? "", StringComparer.Ordinal))
                {
                    var names = string.Join(", ", g.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Note == null ? c.Id : $"{c.Id} ({c.Note})"));
                    var job = g.Key.Job != null && Moves(g.Key.Step) ? $" (job {g.Key.Job})" : "";
                    lines.Add($"{Lead(g.Key.Step, g.Key.Place)}{job}: {names}.");
                }
                sections.Add(string.Join("\n", lines));
            }
            return string.Join("\n\n", sections);
        }

        private static List<(string Label, List<ContractCar> Cars)> Sections(IEnumerable<ContractCar> cars, IReadOnlyList<string> bridgeEnds)
        {
            var list = (cars ?? Enumerable.Empty<ContractCar>()).Where(c => c != null).ToList();
            if (bridgeEnds != null)
                return bridgeEnds.Select(e => ($"to {e}", list.Where(c => c.Line == CarLine.Bridge && c.Toward == e).ToList())).ToList();
            return new List<(string, List<ContractCar>)>
            {
                ("out", list.Where(c => c.Line == CarLine.Out).ToList()),
                ("in", list.Where(c => c.Line == CarLine.In).ToList()),
                ("waste", list.Where(c => c.Line == CarLine.Waste).ToList()),
            };
        }

        private static string Lead(CarStep step, string place)
        {
            switch (step)
            {
                case CarStep.HaulTo:
                case CarStep.HaulAway:
                    return $"To haul to {place}";
                case CarStep.Spot: return $"To spot at {place}";
                case CarStep.WaitForRoom: return $"Waiting at {place} for room on the industry track";
                case CarStep.BeingLoaded: return $"Being loaded at {place}";
                case CarStep.BeingUnloaded: return $"Being unloaded at {place}";
                case CarStep.TakeOut: return $"To take out to {place}";
                case CarStep.Pickup: return $"Waiting at {place} for the 06:00 pickup";
                case CarStep.JobEnded: return $"Job ended unfinished, at {place}, planned again at 06:00";
                case CarStep.MovedOff: return $"Moved off the interchange, at {place}, planned again at 06:00";
                default: return $"Idle at {place}, planned again at 06:00";
            }
        }

        /// <summary>Contracts in order of their cars to move, most first; those with none counted (null) as none, and ties as they were.</summary>
        public static IEnumerable<T> MostToMoveFirst<T>(IEnumerable<T> contracts, Func<T, int?> toMove) =>
            (contracts ?? Enumerable.Empty<T>()).OrderByDescending(c => toMove(c) ?? 0);
    }
}
