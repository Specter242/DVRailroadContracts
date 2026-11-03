using System;
using System.Collections.Generic;
using Contracts.Core.State;

namespace Contracts.Core.Sim
{
    /// <summary>What one observation of the world clock did to the ops clock.</summary>
    public readonly struct ClockAdvance
    {
        public ClockAdvance(double start, double minutes, string rejected)
        {
            Start = start;
            Minutes = minutes;
            Rejected = rejected;
        }

        /// <summary>Ops minutes before the advance.</summary>
        public double Start { get; }

        /// <summary>Game minutes integrated (0 when rejected).</summary>
        public double Minutes { get; }

        /// <summary>Why the world time was not integrated, or null.</summary>
        public string Rejected { get; }

        public double End => Start + Minutes;
    }

    /// <summary>
    /// The monotonic operations clock (operations-design.md, 9.1). Ops time
    /// follows Derail Valley's world clock minute for minute, but never runs
    /// backward and never integrates an absurd jump: either resynchronizes
    /// to the world without advancing, keeping the time of day aligned.
    /// </summary>
    public static class OpsClock
    {
        public const double MinutesPerDay = 1440.0;

        /// <summary>Industry ticks never span more game time than this.</summary>
        public const double MaxStepMinutes = 15.0;

        /// <summary>World time that steps back by less than this (save rounding) is ignored quietly.</summary>
        public const double BackwardTolerance = 1.0;

        public static ClockState Start(double worldMinutes) => new ClockState
        {
            OpsMinutes = 0.0,
            LastWorldMinutes = worldMinutes,
            DayOffsetMinutes = Mod(worldMinutes, MinutesPerDay),
        };

        public static ClockAdvance Observe(ClockState clock, double worldMinutes, double maxCatchUpDays)
        {
            var start = clock.OpsMinutes;
            var delta = worldMinutes - clock.LastWorldMinutes;
            if (double.IsNaN(delta) || double.IsInfinity(delta))
                return new ClockAdvance(start, 0.0, "the world clock is not a number");
            if (delta < 0.0)
            {
                if (delta > -BackwardTolerance)
                    return new ClockAdvance(start, 0.0, null);
                Resync(clock, worldMinutes);
                return new ClockAdvance(start, 0.0, $"the world clock went back {-delta:F0} min; resynchronized without advancing");
            }
            if (delta > maxCatchUpDays * MinutesPerDay)
            {
                Resync(clock, worldMinutes);
                return new ClockAdvance(start, 0.0, $"the world clock jumped {delta / MinutesPerDay:F1} days, more than {maxCatchUpDays} allowed; resynchronized without advancing");
            }
            clock.OpsMinutes += delta;
            clock.LastWorldMinutes = worldMinutes;
            return new ClockAdvance(start, delta, null);
        }

        /// <summary>Takes the world's time without advancing, keeping the time of day aligned.</summary>
        public static void Resync(ClockState clock, double worldMinutes)
        {
            clock.LastWorldMinutes = worldMinutes;
            clock.DayOffsetMinutes = Mod(Mod(worldMinutes, MinutesPerDay) - clock.OpsMinutes, MinutesPerDay);
        }

        /// <summary>World minutes since midnight at the given ops time.</summary>
        public static double TimeOfDay(ClockState clock, double opsMinutes) => Mod(opsMinutes + clock.DayOffsetMinutes, MinutesPerDay);

        /// <summary>The first ops time strictly after <paramref name="t"/> when the world clock reads hour:00.</summary>
        public static double NextDailyAfter(ClockState clock, double t, int hour)
        {
            var wait = Mod(hour * 60.0 - TimeOfDay(clock, t), MinutesPerDay);
            if (wait < 1e-6)
                wait += MinutesPerDay;
            return t + wait;
        }

        /// <summary>Splits an advance into ticks of at most <paramref name="maxStep"/> minutes.</summary>
        public static IEnumerable<(double t0, double dt)> Steps(double start, double minutes, double maxStep = MaxStepMinutes)
        {
            if (!(minutes > 0.0))
                yield break;
            var count = (int)Math.Ceiling(minutes / maxStep - 1e-9);
            var dt = minutes / count;
            for (var i = 0; i < count; i++)
                yield return (start + i * dt, dt);
        }

        /// <summary>"HH:MM" of the world clock at an ops time.</summary>
        public static string Format(ClockState clock, double opsMinutes)
        {
            var minutes = (int)Math.Floor(TimeOfDay(clock, opsMinutes) + 1e-6) % (int)MinutesPerDay;
            return $"{minutes / 60:D2}:{minutes % 60:D2}";
        }

        /// <summary>
        /// The operations day an ops time falls on, counted from 1 for the day
        /// the state was created, by the world clock's midnights.
        /// </summary>
        public static int DayNumber(ClockState clock, double opsMinutes) =>
            (int)(Math.Floor((opsMinutes + clock.DayOffsetMinutes + 1e-6) / MinutesPerDay) - Math.Floor((clock.DayOffsetMinutes + 1e-6) / MinutesPerDay)) + 1;

        /// <summary>"day 2 06:00": an ops time as the papers print it.</summary>
        public static string Stamp(ClockState clock, double opsMinutes) => $"day {DayNumber(clock, opsMinutes)} {Format(clock, opsMinutes)}";

        internal static double Mod(double value, double modulus)
        {
            var r = value % modulus;
            return r < 0 ? r + modulus : r;
        }
    }
}
