using System;
using System.Collections.Generic;

namespace Contracts.Core.State
{
    /// <summary>
    /// Everything MurphyOps owns that changes in play, saved under the
    /// "murphy_ops" key (operations-design.md, 7.2). Derail Valley keeps the
    /// cars themselves, their cargo, the money and the clock; this is the
    /// operations bookkeeping around them.
    /// </summary>
    public sealed class OpsState
    {
        public const int CurrentSchema = 2;

        public int Schema { get; set; } = CurrentSchema;

        public string Map { get; set; }

        /// <summary>SHA256 of the murphy-ops.json this state last ran with.</summary>
        public string OpsDataSha256 { get; set; } = "";

        /// <summary>Derail Valley's tracks hash when last saved; cars saved under another hash are lost (14.4).</summary>
        public string TracksHash { get; set; } = "";

        public ClockState Clock { get; set; } = new ClockState();

        /// <summary>The seeded generator's state, so orders replay identically for a seed.</summary>
        public ulong Rng { get; set; }

        public int NextWaybill { get; set; } = 1;

        public int NextService { get; set; } = 1;

        /// <summary>Game days completed since the state was created (midnights passed).</summary>
        public int Day { get; set; }

        /// <summary>When the next midnight falls (ops minutes): performance and contract changes.</summary>
        public double NextMidnightAt { get; set; }

        public SortedDictionary<string, IndustryState> Industries { get; } = new SortedDictionary<string, IndustryState>(StringComparer.Ordinal);

        public SortedDictionary<string, InterchangeState> Interchanges { get; } = new SortedDictionary<string, InterchangeState>(StringComparer.Ordinal);

        /// <summary>The ops fleet manifest, by Derail Valley car GUID.</summary>
        public SortedDictionary<string, CarRecord> Cars { get; } = new SortedDictionary<string, CarRecord>(StringComparer.Ordinal);

        /// <summary>Open waybills only; closed ones leave a ledger entry and are dropped.</summary>
        public SortedDictionary<string, Waybill> Waybills { get; } = new SortedDictionary<string, Waybill>(StringComparer.Ordinal);

        /// <summary>The most recent entries, at most settings.ledgerLimit; totals are in <see cref="Stats"/>.</summary>
        public List<LedgerEntry> Ledger { get; } = new List<LedgerEntry>();

        public OpsStats Stats { get; set; } = new OpsStats();
    }

    public sealed class ClockState
    {
        /// <summary>Monotonic operations time in game minutes since the state was created.</summary>
        public double OpsMinutes { get; set; }

        /// <summary>The world clock at the last observation, in minutes since 0001-01-01.</summary>
        public double LastWorldMinutes { get; set; }

        /// <summary>Added to <see cref="OpsMinutes"/> gives the world's minutes since midnight (mod 1440).</summary>
        public double DayOffsetMinutes { get; set; }
    }

    public sealed class IndustryState
    {
        /// <summary>Carloads in storage per load ID, within 0 and the industry's cap.</summary>
        public SortedDictionary<string, double> Storage { get; } = new SortedDictionary<string, double>(StringComparer.Ordinal);

        /// <summary>The contract level, 0 (none) to 5; with contracts off, unused.</summary>
        public int Level { get; set; }

        /// <summary>A level the player asked for, applied at the next midnight; null for none.</summary>
        public int? RequestedLevel { get; set; }

        /// <summary>Daily performance, 0–1, newest last, at most seven days (Railroader's history).</summary>
        public List<PerformanceEntry> History { get; } = new List<PerformanceEntry>();

        /// <summary>Cars received (spotted) since the contract last changed.</summary>
        public int ReceivedCars { get; set; }

        /// <summary>When cars were spotted late since the last midnight (ops minutes); each counts as a five-day-old waybill.</summary>
        public List<double> LateSpots { get; } = new List<double>();
    }

    public sealed class PerformanceEntry
    {
        public int Day { get; set; }

        public double Value { get; set; }
    }

    public sealed class InterchangeState
    {
        /// <summary>When the last service ran (ops minutes), or null before the first.</summary>
        public double? LastService { get; set; }

        /// <summary>When the next service is due (ops minutes): the daily one, or an extra one.</summary>
        public double NextServiceAt { get; set; }

        /// <summary>A planned service still waiting for the executor (deferred or partly placed).</summary>
        public PlannedService Pending { get; set; }
    }

    public sealed class PlannedService
    {
        public string Id { get; set; }

        public double DueAt { get; set; }

        /// <summary>Cars ordered at <see cref="DueAt"/> and not yet placed, in placement order.</summary>
        public List<PlannedCar> Cars { get; } = new List<PlannedCar>();

        /// <summary>Whether the removal of cars awaiting pickup has happened; then only placement remains.</summary>
        public bool Removed { get; set; }

        /// <summary>Orders dropped so far (no room, or a shrunk cut); any schedule an extra service.</summary>
        public int Unfilled { get; set; }

        public int Attempts { get; set; }

        /// <summary>Ops minutes of the last attempt, to retry once per game minute.</summary>
        public double? LastAttempt { get; set; }

        public string LastDeferral { get; set; }
    }

    /// <summary>A car the interchange will place, with the waybill it will get.</summary>
    public sealed class PlannedCar
    {
        /// <summary>Ties the executor's spawned GUID back to this plan.</summary>
        public string Token { get; set; }

        public string ClassId { get; set; }

        public string Livery { get; set; }

        public string Mark { get; set; }

        /// <summary>The load ID for a loaded car, null for an empty.</summary>
        public string LoadId { get; set; }

        public WaybillKind Kind { get; set; }

        public string Dest { get; set; }

        public double Pay { get; set; }
    }

    public sealed class CarRecord
    {
        public string Guid { get; set; }

        public string ClassId { get; set; }

        public string Livery { get; set; }

        /// <summary>Reporting mark and number, e.g. "SOU 41235".</summary>
        public string Mark { get; set; }

        /// <summary>"foreign" for interchange cars; "home" is Phase 4.</summary>
        public string Owner { get; set; } = "foreign";

        /// <summary>The interchange a foreign car returns to.</summary>
        public string Home { get; set; }

        /// <summary>The load ID the car carries as far as operations know, or null when empty.</summary>
        public string LoadId { get; set; }

        public string WaybillId { get; set; }

        /// <summary>Loading or unloading progress at its current destination, 0–1.</summary>
        public double Progress { get; set; }

        public CarSighting LastSeen { get; set; }
    }

    public sealed class CarSighting
    {
        public string Track { get; set; }

        public double Span { get; set; }

        /// <summary>Ops minutes.</summary>
        public double T { get; set; }
    }

    public enum WaybillKind
    {
        InboundLoad,
        InboundEmpty,
        OutboundLoad,
        OutboundEmpty,
    }

    public enum WaybillState
    {
        /// <summary>En route to its destination.</summary>
        Open,

        /// <summary>Spotted and paid at an industry; loading or unloading.</summary>
        Servicing,

        /// <summary>Spotted and paid at an interchange; leaves at the next service.</summary>
        AwaitingPickup,
    }

    public sealed class Waybill
    {
        /// <summary>"MB-000124".</summary>
        public string Id { get; set; }

        public string CarGuid { get; set; }

        public WaybillKind Kind { get; set; }

        /// <summary>The load ID for a load waybill, null for an empty.</summary>
        public string LoadId { get; set; }

        /// <summary>Where the move starts: an interchange ID or a component's full ID.</summary>
        public string Origin { get; set; }

        /// <summary>Where the car goes: an interchange ID or a component's full ID.</summary>
        public string Dest { get; set; }

        public double Created { get; set; }

        /// <summary>Fixed when the waybill is written, so later tuning keeps promises.</summary>
        public double Pay { get; set; }

        public WaybillState State { get; set; }

        /// <summary>When the waybill entered its state (ops minutes).</summary>
        public double Since { get; set; }

        /// <summary>When it was paid, or null; a waybill is paid at most once.</summary>
        public double? PaidAt { get; set; }

        /// <summary>Days of age forgiven for a long move (Railroader: 1 beyond 20 miles, 2 beyond 40).</summary>
        public int GraceDays { get; set; }

        /// <summary>Spotted after its due time, so paid at the late factor.</summary>
        public bool Late { get; set; }

        /// <summary>The quick-delivery bonus paid on top at spotting (Railroader's, levels 2–5).</summary>
        public double Bonus { get; set; }

        public bool IsLoad => Kind == WaybillKind.InboundLoad || Kind == WaybillKind.OutboundLoad;
    }

    public sealed class LedgerEntry
    {
        public double T { get; set; }

        /// <summary>"freight" (a paid move), "void" (a waybill written off), or "interchanged".</summary>
        public string Kind { get; set; }

        public string WaybillId { get; set; }

        public string CarGuid { get; set; }

        public double Amount { get; set; }

        public string Memo { get; set; }
    }

    public sealed class OpsStats
    {
        public int Delivered { get; set; }

        public int Voided { get; set; }

        public int Interchanged { get; set; }

        public int Services { get; set; }

        public double Revenue { get; set; }
    }
}
