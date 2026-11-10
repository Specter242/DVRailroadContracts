using System;

namespace Contracts.Core.Industries
{
    /// <summary>
    /// The kiosk's page on the game's day length (Vertigo75, Nexus, 2026-11-09: "It won't let me
    /// change the time at all"; owner: "fix all"). Contracts count game days, so days shorter than
    /// 24 hours run them faster. The built-in difficulty presets can't be changed; a custom one
    /// can, and a career takes one only once the profile has unlocked Train Length 1 or
    /// Concurrent Jobs 1 (the game's rule). Sleeping skips time, aging the waybills and using up
    /// the jobs' bonus time.
    /// </summary>
    public static class DayLength
    {
        public const double FullDayMinutes = 24 * 60;

        /// <summary>The day's length for the kiosk's value column ("2 h"), or null for 24-hour days (or none known).</summary>
        public static string Value(double minutes) =>
            Short(minutes) ? $"{minutes / 60.0:0.#} h" : null;

        /// <summary>The page, or null for 24-hour days (or none known).</summary>
        public static string Page(double minutes)
        {
            if (!Short(minutes))
                return null;
            var length = minutes < 60.0 ? $"{minutes:0} minutes" : minutes == 60.0 ? "1 hour" : $"{minutes / 60.0:0.#} hours";
            return $"Contracts count game days: waybills age, contracts are scored at midnight and the cars come at 06:00, " +
                   $"all by the world clock. Days last {length} here, so contracts run {FullDayMinutes / minutes:0.#} times faster than at 24 hours.\n\n" +
                   "To set 24-hour days, use a custom difficulty preset: in the main menu, pick the career, edit its difficulty " +
                   "and add a preset with Day Duration at 24 hours. Its Bonus Time sets how long jobs keep their bonus, up to 200%.\n\n" +
                   "The built-in presets can't be changed. A custom preset works in a career once your profile has unlocked " +
                   "the Train Length 1 or Concurrent Jobs 1 license, in any career.\n\n" +
                   "Sleeping skips time: it ages your contracts' waybills and uses up the bonus time of the jobs you've taken. " +
                   "Sleep while you wait for the 06:00 cars, not with jobs under way.";
        }

        private static bool Short(double minutes) => minutes > 0.0 && minutes < FullDayMinutes - 1.0;
    }
}
