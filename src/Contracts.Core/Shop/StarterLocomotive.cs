using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Shop
{
    /// <summary>
    /// The locomotive a new career starts with (owner, 2026-11-21: "we need to figure out how to
    /// have a selectable starting loco", "I want a steam start too"), chosen on the career's own
    /// row of the new-career screen (2026-11-23, "Let's do this and we can also add the JK3 as an
    /// option if installed"). The DE2 is the default, as every career began before. The S282 is
    /// left out: a road engine with a tender, worth many times a DE2, it would skip the early game.
    /// </summary>
    public enum StarterLocomotive
    {
        DE2 = 0,
        DM3 = 1,
        S060 = 2,
        /// <summary>The Jk3 mod's 0-8-0 steam locomotive (a custom car loader one, with its tender): offered only when it is installed.</summary>
        JK3 = 3,
    }

    public static class Starters
    {
        /// <summary>The game's own choices, always offered, in the row's order.</summary>
        public static readonly IReadOnlyList<StarterLocomotive> Stock = new[] { StarterLocomotive.DE2, StarterLocomotive.DM3, StarterLocomotive.S060 };

        public const StarterLocomotive Default = StarterLocomotive.DE2;

        /// <summary>The JK3's livery id, as the Jk3 mod registers it with the custom car loader (its tender is Jk3B).</summary>
        public const string Jk3Livery = "Jk3A";

        /// <summary>What the row offers: the game's own, then the JK3 when its mod is installed.</summary>
        public static IReadOnlyList<StarterLocomotive> Offered(bool jk3Installed) =>
            jk3Installed ? Stock.Concat(new[] { StarterLocomotive.JK3 }).ToList() : Stock;

        /// <summary>
        /// A choice as the career saved it (its name): one this build doesn't know, one no longer
        /// offered (the JK3 after its mod was removed), or none (a career begun before the row) is
        /// the default.
        /// </summary>
        public static StarterLocomotive Valid(string saved, IReadOnlyList<StarterLocomotive> offered)
        {
            if (saved == null || !Enum.TryParse(saved, false, out StarterLocomotive starter) || !Enum.IsDefined(typeof(StarterLocomotive), starter)
                || saved != starter.ToString())
                return Default;
            return offered != null && offered.Contains(starter) ? starter : Default;
        }

        /// <summary>The name the row, the shop and the career's save give it.</summary>
        public static string Name(StarterLocomotive starter) => starter.ToString();

        /// <summary>A steam start: its fire has to be lit and fed.</summary>
        public static bool IsSteam(StarterLocomotive starter) => starter == StarterLocomotive.S060 || starter == StarterLocomotive.JK3;

        /// <summary>The items a steam start needs to light and fire its engine: a lighter and a shovel.</summary>
        public static readonly IReadOnlyList<string> SteamKit = new[] { "lighter", "shovel" };

        /// <summary>
        /// What a career's starting items lack for its starter: for a steam start, the lighter and
        /// shovel the game's Basic starting items don't have (its Expanded and Engineer ones do);
        /// for a diesel, nothing. Matched by the items' prefab names, as the game names them.
        /// </summary>
        public static IReadOnlyList<string> KitMissing(StarterLocomotive starter, IEnumerable<string> startingItems)
        {
            if (!IsSteam(starter))
                return Array.Empty<string>();
            var have = new HashSet<string>(startingItems ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            return SteamKit.Where(i => !have.Contains(i)).ToList();
        }
    }
}
