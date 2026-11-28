using System;
using System.Collections.Generic;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// Assigning an inbound cut of cars for an industry to its loading or unloading tracks when
    /// they have room (#59), falling back to the station yard only for cars that do not fit.
    ///
    /// Each step picks the industry track with the most room left and takes the largest contiguous
    /// prefix of the cut that fits in it, preserving car order on the starting track. Once no
    /// industry track can fit the next car, all remaining cars go together in one yard cut
    /// (<see cref="TrackCut{TTrack}.Track"/> is <c>default</c>) and wait at the station to be
    /// spotted once room opens.
    /// </summary>
    public static class DirectDelivery
    {
        /// <summary>
        /// Partitions <paramref name="carCount"/> inbound cars into direct-to-industry-track cuts
        /// and at most one trailing station-yard cut (<c>Track == default</c>).
        /// <paramref name="length"/> gives the total length of <c>count</c> cars starting at
        /// <c>from</c>, including coupler separation (matching the game's track reservation).
        /// </summary>
        public static List<TrackCut<TTrack>> Plan<TTrack>(int carCount, IReadOnlyList<TTrack> spots, IReadOnlyList<double> room,
            Func<int, int, double> length)
        {
            if (carCount < 0)
                throw new ArgumentOutOfRangeException(nameof(carCount));
            if (length == null)
                throw new ArgumentNullException(nameof(length));

            var cuts = new List<TrackCut<TTrack>>();
            if (carCount == 0)
                return cuts;

            var count = spots?.Count ?? 0;
            if (count == 0 || room == null || room.Count < count)
            {
                cuts.Add(new TrackCut<TTrack>(default, 0, carCount));
                return cuts;
            }

            var remaining = new double[count];
            for (var i = 0; i < count; i++)
                remaining[i] = room[i];

            var at = 0;
            while (at < carCount)
            {
                var best = -1;
                var bestRoom = double.NegativeInfinity;
                for (var i = 0; i < count; i++)
                {
                    if (best < 0 || remaining[i] > bestRoom)
                    {
                        best = i;
                        bestRoom = remaining[i];
                    }
                }

                var fit = 0;
                if (best >= 0 && bestRoom > 0.0)
                {
                    while (at + fit < carCount && length(at, fit + 1) <= bestRoom)
                        fit++;
                }

                if (fit == 0)
                {
                    cuts.Add(new TrackCut<TTrack>(default, at, carCount - at));
                    break;
                }

                remaining[best] -= length(at, fit);
                cuts.Add(new TrackCut<TTrack>(spots[best], at, fit));
                at += fit;
            }

            return cuts;
        }
    }
}
