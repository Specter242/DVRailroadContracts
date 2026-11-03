using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// What a contract needs the player to hold (owner, 2026-11-02: "the player will have
    /// mandatory waybills that the player doesn't have licenses for ... it doesn't make sense").
    /// Licenses gate the contract, not its bills: an industry's contract can be taken only with
    /// the cargo licenses (Hazmat, Military, Fragile) of all its traffic, so none of its bills
    /// ever asks for one the player lacks. The job-type licenses every contract uses are given
    /// to every career; train length caps each job's size.
    /// </summary>
    public static class ContractLicenses
    {
        /// <summary>About what the kiosk's info line holds.</summary>
        public const int InfoLength = 37;

        private static readonly Regex Numbered = new Regex(@"^(\D+?)(\d+)$", RegexOptions.CultureInvariant);

        /// <summary>A license's id as the player reads it: "Hazmat2" is "Hazmat 2".</summary>
        public static string Name(string id)
        {
            var m = Numbered.Match(id ?? "");
            return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}" : id ?? "";
        }

        /// <summary>
        /// Null with nothing missing; otherwise what a contract needs, for the info line. Of a
        /// numbered family only the highest is named: the stock shop sells each level only after
        /// the one below ("Needs Hazmat 2, Military 1").
        /// </summary>
        public static string Blocker(IEnumerable<string> missing)
        {
            var names = (missing ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal)
                .Select(id =>
                {
                    var m = Numbered.Match(id);
                    return m.Success ? (Family: m.Groups[1].Value, Level: int.Parse(m.Groups[2].Value), Id: id) : (Family: id, Level: 0, Id: id);
                })
                .GroupBy(l => l.Family, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Name(g.OrderByDescending(l => l.Level).First().Id))
                .ToList();
            if (names.Count == 0)
                return null;
            var text = "Needs " + string.Join(", ", names);
            return text.Length <= InfoLength ? text : text.Substring(0, InfoLength - 1).TrimEnd(' ', ',') + ".";
        }
    }
}
