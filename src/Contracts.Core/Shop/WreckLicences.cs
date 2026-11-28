using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Shop
{
    /// <summary>
    /// One of the game's restoration wrecks, for the licence it locks its cab with (#45).
    /// </summary>
    public sealed class Wreck
    {
        public Wreck(string licence, bool untouched)
        {
            Licence = licence;
            Untouched = untouched;
        }

        /// <summary>The licence of its locomotive (its cab's lock, with the restoration licence).</summary>
        public string Licence { get; }

        /// <summary>Its restoration not begun: its cab still locked (the game's S0 or S1).</summary>
        public bool Untouched { get; }
    }

    /// <summary>
    /// The licences 0.2.x gave free with the game's restoration wrecks, taken back once (#45; owner,
    /// 2026-11-24: "let's just do a quick double check if the player owns any purchased
    /// locomotives that were not the demonstrators, and then we can revoke any licenses").
    ///
    /// The save keeps only the licences held, not how each was got, and the game prints a
    /// booklet for every licence held at each load (its licence safeguard), so a free licence and
    /// a paid one look the same. A licence is taken back when it is the likely bug case:
    /// - it is the licence of a wreck on the map, and every wreck of that licence is untouched
    ///   (one being restored needs it: its cab would lock again);
    /// - the player owns no locomotive that needs it (bought, the starter, a garage's
    ///   demonstrator or any other car of theirs);
    /// - it is not one a career starts with (the game's, and the starting locomotive's).
    /// A licence bought with money for a locomotive the player doesn't own is taken back too: the
    /// save can't tell it apart, and buying that locomotive brings its licence again.
    /// </summary>
    public static class WreckLicences
    {
        public static IReadOnlyList<string> ToTakeBack(
            IEnumerable<string> held,
            IEnumerable<Wreck> wrecks,
            IEnumerable<string> neededByOwned,
            IEnumerable<string> startedWith)
        {
            var byLicence = (wrecks ?? Enumerable.Empty<Wreck>()).Where(w => w?.Licence != null)
                .GroupBy(w => w.Licence, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.All(w => w.Untouched), StringComparer.Ordinal);
            var keep = new HashSet<string>((neededByOwned ?? Enumerable.Empty<string>())
                .Concat(startedWith ?? Enumerable.Empty<string>()).Where(l => l != null), StringComparer.Ordinal);
            return (held ?? Enumerable.Empty<string>()).Where(l => l != null).Distinct(StringComparer.Ordinal)
                .Where(l => byLicence.TryGetValue(l, out var untouched) && untouched && !keep.Contains(l))
                .OrderBy(l => l, StringComparer.Ordinal).ToList();
        }
    }
}
