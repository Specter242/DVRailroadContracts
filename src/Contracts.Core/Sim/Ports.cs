using System.Collections.Generic;
using Contracts.Core.State;

namespace Contracts.Core.Sim
{
    // The core's boundary (operations-design.md, 6.1 and 6.3): the runtime
    // observes Derail Valley and reports executor results; the core answers
    // with commands. The core never assumes a command succeeded.

    /// <summary>One bogie's place: the RailTrack GameObject name and the span along it.</summary>
    public sealed class BogieObservation
    {
        public BogieObservation(string track, double span)
        {
            Track = track;
            Span = span;
        }

        /// <summary>Null when the bogie is derailed or off any track.</summary>
        public string Track { get; }

        public double Span { get; }
    }

    /// <summary>What the runtime saw of one registered car this tick.</summary>
    public sealed class CarObservation
    {
        public string Guid { get; set; }

        /// <summary>False when no car with the GUID exists any more.</summary>
        public bool Present { get; set; } = true;

        public BogieObservation Front { get; set; }

        public BogieObservation Rear { get; set; }

        /// <summary>Absolute speed, m/s.</summary>
        public double Speed { get; set; }

        public bool CoupledToLoco { get; set; }

        public bool Derailed { get; set; }

        /// <summary>The Derail Valley cargo in the car (CargoType_v2.id), or null when empty.</summary>
        public string Cargo { get; set; }

        public static CarObservation Missing(string guid) => new CarObservation { Guid = guid, Present = false };
    }

    public enum CutFailure
    {
        /// <summary>Something occupies the target; retry later.</summary>
        Blocked,

        /// <summary>The cut does not fit the track at that span; shrink it.</summary>
        DoesNotFit,
    }

    /// <summary>Something the executor reports back.</summary>
    public abstract class ExecutorEvent
    {
    }

    /// <summary>The outcome of a <see cref="ServiceCommand"/>.</summary>
    public sealed class ServiceResult : ExecutorEvent
    {
        public string ServiceId { get; set; }

        /// <summary>False when the executor's gating refused the service; nothing was changed.</summary>
        public bool Executed { get; set; }

        public string DeferredReason { get; set; }

        public List<string> RemovedGuids { get; } = new List<string>();

        /// <summary>Planned-car token to the GUID of the car spawned for it.</summary>
        public Dictionary<string, string> Placed { get; } = new Dictionary<string, string>();

        /// <summary>Cut index to why it was not placed.</summary>
        public Dictionary<int, CutFailure> FailedCuts { get; } = new Dictionary<int, CutFailure>();
    }

    /// <summary>A cargo command threw or found the car in an unexpected state.</summary>
    public sealed class CargoFailed : ExecutorEvent
    {
        public string Guid { get; set; }

        public string Reason { get; set; }
    }

    /// <summary>Derail Valley is deleting a registered car (CarSpawner.CarAboutToBeDeleted).</summary>
    public sealed class CarDeleted : ExecutorEvent
    {
        public string Guid { get; set; }
    }

    public sealed class StepInput
    {
        /// <summary>The world clock in minutes since 0001-01-01.</summary>
        public double WorldMinutes { get; set; }

        /// <summary>Seconds of play this session (Unity's scaled time), for spotting dwell.</summary>
        public double SessionSeconds { get; set; }

        /// <summary>One per registered car; a car left out is treated as unobserved this tick, not lost.</summary>
        public List<CarObservation> Cars { get; } = new List<CarObservation>();

        public List<ExecutorEvent> Events { get; } = new List<ExecutorEvent>();
    }

    public abstract class OpsCommand
    {
    }

    public sealed class PayCommand : OpsCommand
    {
        public double Amount { get; set; }

        public string WaybillId { get; set; }

        public string Memo { get; set; }
    }

    public sealed class LoadCargoCommand : OpsCommand
    {
        public string Guid { get; set; }

        public string DvCargo { get; set; }
    }

    public sealed class UnloadCargoCommand : OpsCommand
    {
        public string Guid { get; set; }

        public string DvCargo { get; set; }
    }

    /// <summary>Text for the car's plates (TrainCar.UpdateJobIdOnCarPlates).</summary>
    public sealed class SetPlateCommand : OpsCommand
    {
        public string Guid { get; set; }

        public string Text { get; set; }
    }

    public sealed class NotifyCommand : OpsCommand
    {
        public string Text { get; set; }
    }

    /// <summary>One cut for one spot, spawned from <see cref="StartSpan"/> toward the spot's end.</summary>
    public sealed class ServiceCut
    {
        public int Index { get; set; }

        public string Track { get; set; }

        public double StartSpan { get; set; }

        public List<PlannedCar> Cars { get; } = new List<PlannedCar>();
    }

    /// <summary>
    /// Run an interchange service: remove the listed cars, then spawn the
    /// cuts. The executor first checks its gating (operations-design.md, 9.4)
    /// and changes nothing when it fails.
    /// </summary>
    public sealed class ServiceCommand : OpsCommand
    {
        public string ServiceId { get; set; }

        public string Interchange { get; set; }

        public List<string> RemoveGuids { get; } = new List<string>();

        public List<ServiceCut> Cuts { get; } = new List<ServiceCut>();
    }

    public enum LogLevel
    {
        Info,
        Warning,
        Error,
    }

    public sealed class LogLine
    {
        public LogLine(LogLevel level, string text)
        {
            Level = level;
            Text = text;
        }

        public LogLevel Level { get; }

        public string Text { get; }

        public override string ToString() => $"{Level}: {Text}";
    }

    public sealed class StepResult
    {
        public List<OpsCommand> Commands { get; } = new List<OpsCommand>();

        public List<LogLine> Log { get; } = new List<LogLine>();

        public ClockAdvance Clock { get; set; }
    }
}
