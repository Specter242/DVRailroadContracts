using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Data
{
    /// <summary>
    /// The static operations data of one map: murphy-ops.json, format 1
    /// (docs/build-format.md, "Operations"). Built by <see cref="OpsDataLoader"/>
    /// or by tests; <see cref="Freeze"/> wires the lookups and back references.
    /// </summary>
    public sealed class OpsData
    {
        public const int CurrentFormat = 1;

        private readonly Dictionary<string, IDestination> destinations = new Dictionary<string, IDestination>(StringComparer.Ordinal);
        private readonly Dictionary<string, Area> areaById = new Dictionary<string, Area>(StringComparer.Ordinal);

        public int Format { get; set; } = CurrentFormat;

        public string Map { get; set; }

        /// <summary>SHA256 of the source text, recorded in the save to notice data changes.</summary>
        public string Sha256 { get; set; } = "";

        public OpsSettings Settings { get; set; } = new OpsSettings();

        public Dictionary<string, CarClass> CarClasses { get; } = new Dictionary<string, CarClass>(StringComparer.Ordinal);

        public Dictionary<string, LoadDef> Loads { get; } = new Dictionary<string, LoadDef>(StringComparer.Ordinal);

        public List<Area> Areas { get; } = new List<Area>();

        public List<Industry> Industries { get; } = new List<Industry>();

        public List<Interchange> Interchanges { get; } = new List<Interchange>();

        public IEnumerable<IndustryComponent> Components => Industries.SelectMany(i => i.Components);

        /// <summary>Every place a waybill can name: components by full ID, interchanges by ID.</summary>
        public IEnumerable<IDestination> Destinations => destinations.Values;

        public IDestination Destination(string id) => id != null && destinations.TryGetValue(id, out var d) ? d : null;

        public IndustryComponent Component(string fullId) => Destination(fullId) as IndustryComponent;

        public Interchange Interchange(string id) => Destination(id) as Interchange;

        public Area Area(string id) => id != null && areaById.TryGetValue(id, out var a) ? a : null;

        /// <summary>
        /// The interchange that orders cars for a component: the first one in
        /// the component's area, else the first one on the map. M1 has a single
        /// interchange; choosing by geography or rotation is decision D5.
        /// </summary>
        public Interchange ServingInterchange(IndustryComponent component)
        {
            var areaId = component.Industry.AreaId;
            return Interchanges.FirstOrDefault(x => x.AreaId == areaId) ?? Interchanges.FirstOrDefault();
        }

        /// <summary>Billing milepost of a destination: its area's, or the interchange's own.</summary>
        public double Milepost(IDestination destination)
        {
            if (destination is Interchange interchange)
                return interchange.Milepost;
            if (destination is IndustryComponent component)
                return Area(component.Industry.AreaId)?.Milepost ?? 0.0;
            return 0.0;
        }

        /// <summary>Wires back references and lookups; call after building or changing the lists.</summary>
        public OpsData Freeze()
        {
            destinations.Clear();
            areaById.Clear();
            foreach (var pair in CarClasses)
                pair.Value.Id = pair.Key;
            foreach (var pair in Loads)
                pair.Value.Id = pair.Key;
            foreach (var area in Areas)
                areaById[area.Id] = area;
            foreach (var industry in Industries)
            {
                foreach (var component in industry.Components)
                {
                    component.Industry = industry;
                    destinations[component.FullId] = component;
                }
            }
            foreach (var interchange in Interchanges)
                destinations[interchange.Id] = interchange;
            return this;
        }
    }

    public sealed class OpsSettings
    {
        public int ServeHour { get; set; } = 6;

        public double ExtraServiceDelayHours { get; set; } = 2.5;

        /// <summary>m/s; at or below it a car counts as stationary.</summary>
        public double SpotSpeed { get; set; } = 0.3;

        /// <summary>Seconds of play a car must stay stationary on its destination to count as spotted.</summary>
        public double SpotDwellSeconds { get; set; } = 20.0;

        /// <summary>m; the runtime keeps players this far from an interchange's spots during its service.</summary>
        public double ServiceClearance { get; set; } = 600.0;

        public double PayScale { get; set; } = 30.0;

        public double MaxCatchUpDays { get; set; } = 7.0;

        public bool ServeOnNewSave { get; set; } = true;

        public int LedgerLimit { get; set; } = 500;

        public List<string> DisableProceduralJobsAt { get; } = new List<string>();

        /// <summary>Standing contracts (operations-design.md, 20.3); off unless the data enables them.</summary>
        public ContractSettings Contracts { get; set; } = new ContractSettings();
    }

    /// <summary>
    /// Standing contracts in Railroader's manner (operations-design.md, 20.3):
    /// the railroad holds one contract with each industry at a level 1–5, which
    /// scales the industry's traffic and the pay of its moves.
    /// </summary>
    public sealed class ContractSettings
    {
        public bool Enabled { get; set; }

        /// <summary>The level an industry's contract starts at, unless the industry names its own (0 = none).</summary>
        public int StartLevel { get; set; } = 3;

        /// <summary>The highest level the player can take on a new contract at (Railroader: 1–3 by reputation).</summary>
        public int MaxStartLevel { get; set; } = 3;

        /// <summary>Game hours from a waybill's writing within which spotting pays in full.</summary>
        public double DueHours { get; set; } = 24.0;

        /// <summary>Share of a waybill's pay for a car spotted after its due time.</summary>
        public double LatePayFactor { get; set; } = 0.5;

        /// <summary>
        /// Whether the level's factor (0.24–1.0) also scales the pay of moves to
        /// and from the industry. Off by default: like Railroader, the level
        /// scales traffic and pays a quick-delivery bonus (operations-design.md, 20.4).
        /// </summary>
        public bool LevelScalesPay { get; set; }

        /// <summary>Railroader's downgrade penalty unit in its dollars; MurphyOps multiplies it by payScale.</summary>
        public double PenaltyBase { get; set; } = 250.0;
    }

    public sealed class CarClass
    {
        /// <summary>AAR-style code, e.g. XM; set from the dictionary key.</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>Derail Valley livery IDs (TrainCarLivery.id) that can represent the class.</summary>
        public List<string> Liveries { get; } = new List<string>();

        /// <summary>Coupler-to-coupler length in meters.</summary>
        public double LengthM { get; set; }
    }

    public sealed class LoadDef
    {
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>The Derail Valley cargo it shows as (CargoType_v2.id).</summary>
        public string DvCargo { get; set; }

        public double TonsPerCar { get; set; }

        public bool Importable { get; set; } = true;
    }

    public sealed class Area
    {
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>The Derail Valley station ID (content.json), or null for an area that is not a station.</summary>
        public string Station { get; set; }

        public double Milepost { get; set; }

        public string Color { get; set; }

        /// <summary>Where the area's office stands, or null for an area without one.</summary>
        public OfficeRef Office { get; set; }
    }

    /// <summary>
    /// An area's office (operations-design.md, 20.2): an existing Derail Valley
    /// station office named by its station ID, or (later) a building placed at
    /// a position for an area that is not a Derail Valley station.
    /// </summary>
    public sealed class OfficeRef
    {
        /// <summary>The station whose office building serves the area, or null.</summary>
        public string Station { get; set; }

        /// <summary>Unity-frame position of a placed office, when not a station's.</summary>
        public double[] Position { get; set; }

        /// <summary>Unit XZ direction of a placed office's front.</summary>
        public double[] Facing { get; set; }
    }

    public sealed class Industry
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string AreaId { get; set; }

        /// <summary>Whether the industry works under a contract (Railroader's usesContract); others always run at full rate.</summary>
        public bool UsesContract { get; set; } = true;

        /// <summary>The contract level it starts at, or null for the settings' start level.</summary>
        public int? ContractLevel { get; set; }

        /// <summary>Storage cap in carloads per load ID.</summary>
        public Dictionary<string, double> Storage { get; } = new Dictionary<string, double>(StringComparer.Ordinal);

        public List<IndustryComponent> Components { get; } = new List<IndustryComponent>();
    }

    public enum ComponentType
    {
        Unloader,
        Loader,
    }

    /// <summary>A waybill destination: an industry component or an interchange.</summary>
    public interface IDestination
    {
        string Id { get; }

        string Name { get; }

        /// <summary>Short code for car plates.</summary>
        string Code { get; }

        IReadOnlyList<Spot> Spots { get; }
    }

    public sealed class IndustryComponent : IDestination
    {
        public Industry Industry { get; internal set; }

        /// <summary>The ID within its industry, e.g. "dock".</summary>
        public string LocalId { get; set; }

        public string FullId => Industry == null ? LocalId : Industry.Id + "." + LocalId;

        string IDestination.Id => FullId;

        public string Name => Industry == null ? LocalId : $"{Industry.Name} {Code}";

        public ComponentType Type { get; set; }

        public string Code { get; set; }

        public string LoadId { get; set; }

        public List<string> CarClasses { get; } = new List<string>();

        public List<Spot> SpotList { get; } = new List<Spot>();

        IReadOnlyList<Spot> IDestination.Spots => SpotList;

        public double ConsumePerDay { get; set; }

        public double UnloadPerDay { get; set; }

        public double ProducePerDay { get; set; }

        public double LoadPerCarPerDay { get; set; }

        public bool Orders { get; set; } = true;

        public bool Accepts(string carClass) => CarClasses.Contains(carClass);
    }

    public sealed class Interchange : IDestination
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string AreaId { get; set; }

        public string Code { get; set; }

        public List<Spot> SpotList { get; } = new List<Spot>();

        IReadOnlyList<Spot> IDestination.Spots => SpotList;

        public double CapacityFactor { get; set; } = 0.7;

        /// <summary>Reporting marks of arriving foreign cars.</summary>
        public List<string> Roads { get; } = new List<string>();

        public double Milepost { get; set; }
    }

    /// <summary>
    /// A stretch of one track: meters from..to along it from the span origin,
    /// which is where Derail Valley's PointSetTraveller.Span starts.
    /// </summary>
    public sealed class Spot
    {
        public Spot(string track, string runtimeName, double trackLength, double from, double to)
        {
            Track = track;
            RuntimeName = runtimeName;
            TrackLength = trackLength;
            From = from;
            To = to;
        }

        /// <summary>The track.json track ID.</summary>
        public string Track { get; }

        /// <summary>The track's GameObject name in Derail Valley.</summary>
        public string RuntimeName { get; }

        public double TrackLength { get; }

        public double From { get; }

        public double To { get; }

        public double Length => To - From;

        public bool Contains(string trackName, double span) =>
            trackName != null && string.Equals(trackName, RuntimeName, StringComparison.Ordinal) && span >= From && span <= To;
    }
}
