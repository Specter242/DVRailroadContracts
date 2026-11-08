using System.Collections.Generic;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// A contract as the kiosk shows it: an offer at this kiosk, or one of the player's own. A
    /// contract is with an industry (all of its traffic) or is the bridge between the
    /// interchanges (design D11).
    /// </summary>
    internal sealed class ContractView
    {
        public string Id;           // stable: the industry's or bridge's id in the model
        public string Short;        // the list line, short enough for the kiosk's column
        public bool IsOffer;
        public int Level;           // 1-5; 0 for an offer, or a contract that starts at midnight
        public int RequestedLevel;  // the level from midnight; equal to Level when no change is asked
        public float? Performance;  // 0-1, for the player's own
        public int OpenWaybills;    // a passenger contract's departures due or running; the jobless row's cars
        public int? ToMove;         // the player's industry or bridge contract's cars still to move, once it has started; else null
        public string Needs;        // null, or the licenses the player lacks for it ("Needs Hazmat 2"), for the info line
    }

    /// <summary>A place a kiosk serves: a stock station, or an interchange office.</summary>
    internal sealed class KioskPlace
    {
        public string Id;            // a station's yard ID (MF), or an interchange's name (MF West)
        public bool IsInterchange;
        public string Name;          // for the screen's title
    }

    /// <summary>
    /// What the kiosk menu asks of the contract model. Level changes take effect at midnight
    /// (design, "Industry contracts"); the kiosk only records the request.
    /// </summary>
    internal interface IContractDesk
    {
        IReadOnlyList<ContractView> Offers(KioskPlace place);
        IReadOnlyList<ContractView> Mine();
        /// <summary>A contract's carloads a day at a level, short enough for the value column (design, "Contract volume").</summary>
        string Volume(ContractView contract, int level);
        /// <summary>What <see cref="Volume"/>'s figures are, for the info line.</summary>
        string Legend(ContractView contract);
        void Take(ContractView offer, int level);
        void RequestLevel(ContractView mine, int level);
        /// <summary>Null if the player's contract may be raised a level now; otherwise why not, for the info line.</summary>
        string RaiseBlocker(ContractView mine);
        void End(ContractView mine);
        /// <summary>The penalty for taking the player's contract down to a level (0 ends it), at its history now (design D7).</summary>
        double Penalty(ContractView mine, int target);
        /// <summary>Penalties owed, not yet taken from the wallet.</summary>
        double Owed { get; }
        /// <summary>A line for the Contracts menu, such as the day-duration notice; null for none.</summary>
        string Notice { get; }
        /// <summary>The contract cars without a job now: how many, and the kiosk's paragraph on them (null for none).</summary>
        (int Count, string Text) Jobless();
        /// <summary>The player's industry or bridge contract's cars now; null for a passenger contract or an offer.</summary>
        ContractCarsView Cars(ContractView mine);
    }

    /// <summary>
    /// A contract's cars now, for its screen (Contracts.Core ContractCars): how many are still to
    /// move, the info line's counts ("To move: 3 out / 5 in / 1 waste"), and the Cars page (null
    /// with no cars).
    /// </summary>
    internal sealed class ContractCarsView
    {
        public int ToMove;
        public string Line;
        public string Page;
    }
}
