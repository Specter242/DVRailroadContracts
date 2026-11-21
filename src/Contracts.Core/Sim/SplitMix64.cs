using System;
using System.Collections.Generic;

namespace Contracts.Core.Sim
{
    /// <summary>
    /// A seeded generator whose whole state is one number saved with the
    /// game, so a save replays the same orders and marks (SplitMix64).
    /// </summary>
    public sealed class SplitMix64
    {
        public SplitMix64(ulong state) => State = state;

        public ulong State { get; private set; }

        /// <summary>Deterministic 64-bit FNV-1a hash (unlike string.GetHashCode, stable across runs and platforms).</summary>
        public static ulong Hash(string text)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                if (text != null)
                    for (var i = 0; i < text.Length; i++)
                    {
                        var ch = text[i];
                        hash ^= (byte)ch;
                        hash *= 1099511628211UL;
                        hash ^= (byte)(ch >> 8);
                        hash *= 1099511628211UL;
                    }
                return hash;
            }
        }

        public ulong NextULong()
        {
            unchecked
            {
                State += 0x9E3779B97F4A7C15UL;
                var z = State;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>A double in [0, 1).</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        /// <summary>An integer in [0, <paramref name="maxExclusive"/>).</summary>
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            return Math.Min(maxExclusive - 1, (int)(NextDouble() * maxExclusive));
        }

        /// <summary>An integer in [<paramref name="min"/>, <paramref name="maxExclusive"/>).</summary>
        public int Next(int min, int maxExclusive) => min + Next(maxExclusive - min);

        public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

        /// <summary>Fisher–Yates, in place.</summary>
        public void Shuffle<T>(IList<T> items)
        {
            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = Next(i + 1);
                var t = items[i];
                items[i] = items[j];
                items[j] = t;
            }
        }
    }
}
