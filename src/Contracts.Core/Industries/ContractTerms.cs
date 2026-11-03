using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// What a held contract pays and costs on top of stock wages (design D7: "Railroader's
    /// downgrade penalties and quick-delivery bonus apply on top of stock pay"; owner,
    /// 2026-11-03: "we can add that"). After Railroader, as the core's <c>OpsEngine</c> keeps
    /// them for MurphyOps, on the industry contracts' levels and standing (ContractStanding):
    ///
    /// - **The quick-delivery bonus** at levels 2–5 is 4, 6, 8 or 10% of a contract job's base
    ///   wage, reckoned car by car on each car's share of it: full while the car's bill is under
    ///   a day old when the job is done, half on its second day, a quarter on its third, none
    ///   later. A passenger train's bill opens when the train is due.
    /// - **The penalty** for lowering or ending a contract is Railroader's $250 unit at this
    ///   game's pay scale (×30) for each level dropped, plus the unit for each day of history
    ///   short of six (at least one): a young contract costs more to give up.
    /// - **Poor performance forces a drop:** three scored days in a row averaging under 50%
    ///   take the contract down two levels at midnight (a level 1 or 2 contract ends), and the
    ///   penalty applies.
    /// </summary>
    public static class ContractTerms
    {
        /// <summary>Railroader's $250 penalty unit at this game's pay scale (design D7: 250 × 30).</summary>
        public const double PenaltyUnit = 250.0 * 30.0;

        public const int ForcedDropDays = 3;
        public const double ForcedDropAverage = 0.5;
        public const int ForcedDropLevels = 2;

        /// <summary>The quick-delivery bonus at a level, percent of the base wage on the bill's first day.</summary>
        public static double BonusPercent(int level)
        {
            switch (level)
            {
                case 2: return 4.0;
                case 3: return 6.0;
                case 4: return 8.0;
                case 5: return 10.0;
                default: return 0.0;
            }
        }

        /// <summary>The share of the bonus for a bill this many days old: 1 on its first day, ½ on its second, ¼ on its third, then none.</summary>
        public static double BonusShare(double ageDays)
        {
            if (ageDays < 0.0)
                ageDays = 0.0;
            var days = (int)Math.Floor(ageDays + 1e-9);
            return days == 0 ? 1.0 : days == 1 ? 0.5 : days == 2 ? 0.25 : 0.0;
        }

        /// <summary>The bonus on a wage (or a car's share of one) for a bill this old at a contract level, in whole dollars.</summary>
        public static double Bonus(double wage, int level, double ageDays) =>
            wage <= 0.0 ? 0.0 : Math.Round(wage * BonusPercent(level) * BonusShare(ageDays) / 100.0);

        /// <summary>
        /// The penalty for taking a contract from a level to a lower one (0 ends it), with this many
        /// days of history at the level; nothing for a raise, or for a contract not yet started.
        /// </summary>
        public static double Penalty(int level, int target, int historyDays)
        {
            var drop = level - Math.Max(0, target);
            if (level < 1 || drop <= 0)
                return 0.0;
            var days = Math.Max(1, historyDays);
            return PenaltyUnit * (drop + Math.Max(6 - days, 1));
        }

        /// <summary>The level poor performance forces a contract down to at midnight (0 ends it), or null if it holds.</summary>
        public static int? ForcedLevel(int level, IReadOnlyList<double> history)
        {
            if (level < 1 || history == null || history.Count < ForcedDropDays)
                return null;
            var recent = history.Skip(history.Count - ForcedDropDays).Average();
            return recent < ForcedDropAverage ? Math.Max(0, level - ForcedDropLevels) : (int?)null;
        }
    }
}
