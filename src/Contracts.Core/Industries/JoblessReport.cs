using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// Why a contract car has no job now. Most end at the next 06:00 service; a car at an
    /// industry goes out when it is done, and one waiting for room is spotted when room is made.
    /// </summary>
    public enum JoblessReason
    {
        /// <summary>Set out on an interchange track for the connecting railroad: the service takes it away.</summary>
        WaitingForPickup,
        /// <summary>Set out for pickup, then moved off the interchange: the service plans it again.</summary>
        MovedOffInterchange,
        /// <summary>No work: the service plans it.</summary>
        Idle,
        /// <summary>Its job ended unfinished (abandoned or expired): the service plans it again.</summary>
        JobEnded,
        /// <summary>Spotted on its shipper's track: the industry loads it, then it goes out.</summary>
        BeingLoaded,
        /// <summary>Spotted on its receiver's track: the industry unloads it, then it goes out empty.</summary>
        BeingUnloaded,
        /// <summary>At its industry's station, its industry track full: spotted once there is room.</summary>
        WaitingToSpot,
    }

    /// <summary>
    /// A contract car without a job: its number, why, where it stands (an interchange's code or a
    /// station's), and a note said after its number (when an industry will be done with it).
    /// </summary>
    public sealed class JoblessCar
    {
        public JoblessCar(string id, JoblessReason reason, string place, string note = null)
        {
            Id = id ?? "?";
            Reason = reason;
            Place = place ?? "?";
            Note = string.IsNullOrEmpty(note) ? null : note;
        }

        public string Id { get; }
        public JoblessReason Reason { get; }
        public string Place { get; }
        public string Note { get; }
    }

    /// <summary>
    /// The contract cars without a job, said plainly (owner, 2026-11-03: two refrigerator cars on
    /// the West Interchange "don't have a job number associated"): for the kiosk's "Cars without
    /// jobs" screen and the log. Cars are grouped by why and where, in order, each group naming
    /// at most so many cars.
    /// </summary>
    public static class JoblessReport
    {
        private static readonly JoblessReason[] Order =
        {
            JoblessReason.WaitingForPickup, JoblessReason.MovedOffInterchange, JoblessReason.BeingLoaded, JoblessReason.BeingUnloaded,
            JoblessReason.WaitingToSpot, JoblessReason.JobEnded, JoblessReason.Idle,
        };

        /// <summary>The kiosk's paragraph: one sentence a group, a blank line between; null with no cars.</summary>
        public static string Paragraph(IEnumerable<JoblessCar> cars, int namesPerGroup = 8)
        {
            var groups = Groups(cars, namesPerGroup).Select(g => $"{g.Lead}: {g.Names}.").ToList();
            return groups.Count == 0 ? null : string.Join("\n\n", groups);
        }

        /// <summary>The log's line: every group, separated by semicolons; null with no cars.</summary>
        public static string LogLine(IEnumerable<JoblessCar> cars, int namesPerGroup = 30)
        {
            var list = (cars ?? Enumerable.Empty<JoblessCar>()).ToList();
            if (list.Count == 0)
                return null;
            return $"{list.Count} contract car(s) without a job: " +
                   string.Join("; ", Groups(list, namesPerGroup).Select(g => $"{char.ToLowerInvariant(g.Lead[0])}{g.Lead.Substring(1)}: {g.Names}"));
        }

        private static IEnumerable<(string Lead, string Names)> Groups(IEnumerable<JoblessCar> cars, int namesPerGroup)
        {
            var list = (cars ?? Enumerable.Empty<JoblessCar>()).Where(c => c != null).ToList();
            foreach (var reason in Order)
                foreach (var at in list.Where(c => c.Reason == reason).GroupBy(c => c.Place).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var ids = at.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Note == null ? c.Id : $"{c.Id} ({c.Note})").ToList();
                    var names = string.Join(", ", ids.Take(Math.Max(1, namesPerGroup)));
                    if (ids.Count > namesPerGroup)
                        names += $" and {ids.Count - namesPerGroup} more";
                    yield return (Lead(reason, at.Key), names);
                }
        }

        private static string Lead(JoblessReason reason, string place)
        {
            switch (reason)
            {
                case JoblessReason.WaitingForPickup: return $"Waiting at {place} for the 06:00 pickup";
                case JoblessReason.MovedOffInterchange: return $"Moved off the interchange, at {place}, planned again at 06:00";
                case JoblessReason.JobEnded: return $"Job ended unfinished, at {place}, planned again at 06:00";
                case JoblessReason.BeingLoaded: return $"Being loaded at {place}";
                case JoblessReason.BeingUnloaded: return $"Being unloaded at {place}";
                case JoblessReason.WaitingToSpot: return $"Waiting at {place} for room on the industry track";
                default: return $"Idle at {place}, planned again at 06:00";
            }
        }
    }
}
