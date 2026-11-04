using System;
using System.Collections.Generic;

namespace Contracts.Core.Industries
{
    /// <summary>A cut of new cars for one track: the cars from <see cref="From"/>, <see cref="Count"/> of them.</summary>
    public sealed class TrackCut<TTrack>
    {
        public TrackCut(TTrack track, int from, int count)
        {
            Track = track;
            From = from;
            Count = count;
        }

        public TTrack Track { get; }
        public int From { get; }
        public int Count { get; }
    }

    /// <summary>
    /// One interchange's new cars, placed as cuts that fill its empty tracks one after another
    /// (design, "Interchange service"). Each free track, in order, takes as many of the cars still
    /// to place as fit with <see cref="Spare"/> metres to spare; a track with cars on it is left
    /// alone, and a track too short for the next car is passed over.
    ///
    /// The service places one cut a frame (owner, 2026-11-03: spread the 06:00 placement over
    /// several frames). Whether a track is free is asked when its turn comes, so a cut at a time
    /// gives the same cuts as placing them all at once whenever nothing else takes a track between
    /// frames.
    /// </summary>
    public sealed class TrackFill<TTrack>
    {
        /// <summary>The room a cut leaves on its track (both ends together), in metres.</summary>
        public const double Spare = 30.0;

        private readonly IReadOnlyList<TTrack> tracks;
        private int nextTrack;

        public TrackFill(int cars, IReadOnlyList<TTrack> tracks)
        {
            if (cars < 0)
                throw new ArgumentOutOfRangeException(nameof(cars));
            Cars = cars;
            this.tracks = tracks ?? new List<TTrack>();
        }

        /// <summary>The cars to place, in order.</summary>
        public int Cars { get; }

        /// <summary>The cars placed so far; they are always the first ones.</summary>
        public int Placed { get; private set; }

        public int Unplaced => Cars - Placed;

        /// <summary>Every car is placed, or every track has had its turn.</summary>
        public bool Done => Placed >= Cars || nextTrack >= tracks.Count;

        /// <summary>
        /// The next cut: the next free track that takes at least one car, with the cars that fit on
        /// it; null when <see cref="Done"/>. <paramref name="carsLength"/> is the length of
        /// <c>count</c> cars from car <c>from</c>, couplers included.
        /// </summary>
        public TrackCut<TTrack> Next(Func<TTrack, bool> isFree, Func<TTrack, double> trackLength, Func<int, int, double> carsLength)
        {
            while (!Done)
            {
                var track = tracks[nextTrack++];
                if (!isFree(track))
                    continue;
                var room = trackLength(track) - Spare;
                var take = 0;
                while (Placed + take < Cars && carsLength(Placed, take + 1) < room)
                    take++;
                if (take > 0)
                    return new TrackCut<TTrack>(track, Placed, take);
            }
            return null;
        }

        /// <summary>The cut is on its track. A cut that failed to spawn is not reported; its cars go to the next track.</summary>
        public void Spawned(TrackCut<TTrack> cut)
        {
            if (cut == null || cut.From != Placed)
                throw new InvalidOperationException("a cut is placed in turn, from the first car not yet placed");
            Placed += cut.Count;
        }
    }
}
