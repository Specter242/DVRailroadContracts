namespace Contracts.Core.Net
{
    /// <summary>
    /// A job turned in by a player who joined a Multiplayer session (owner, 2026-11-12; design,
    /// "Multiplayer"). The game pays a job by printing banknotes at the validator, and
    /// Multiplayer runs a joined player's turn-in on the host's game: the banknotes come out in
    /// the host's copy of that office, where the player who earned them can't take them.
    ///
    /// So the host's game pays such a job straight into the railroad's cash, which Multiplayer
    /// shows to every player. The host can't see who turned a job in, only where: a turn-in at
    /// an office the host's own player is not in is a joined player's.
    /// </summary>
    public static class GuestPay
    {
        /// <summary>How near its validator, in meters, the host's player is taken to have turned the job in themself.</summary>
        public const double Reach = 10.0;

        /// <summary>
        /// Whether a job turned in now is paid straight into the railroad's cash:
        /// <paramref name="hostsOthers"/> is whether this game hosts a session others can join,
        /// <paramref name="hostAway"/> how far the host's player is from the validator, null when
        /// not known (then it is paid straight in: banknotes nobody can reach are the worse fault).
        /// </summary>
        public static bool Direct(bool hostsOthers, double? hostAway) =>
            hostsOthers && !(hostAway <= Reach);
    }
}
