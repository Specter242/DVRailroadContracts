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
        public bool Ending;         // the player's own, asked to end: it ends at midnight
        public float? Performance;  // 0-1, for the player's own
        public int ScoredDays;      // the scored days at its level, for the player's own (a raise needs three)
        public int OpenWaybills;    // a passenger contract's departures due or running; the jobless row's cars
        public int? ToMove;         // the player's industry or bridge contract's cars still to move, once it has started; else null
        public string Needs;        // null, or the licenses the player lacks for it ("Needs Hazmat 2"), for the info line
    }

    /// <summary>A vehicle the kiosk's shop sells: its row, and the question before it is bought.</summary>
    internal sealed class ShopRow
    {
        public string Key;      // what the desk knows it by
        public string Name;
        public double Price;
        public string Ask;
    }

    /// <summary>Vehicles of the railroad's the kiosk buys back, sold together: the row, the offer, and why not now or the question before.</summary>
    internal sealed class SaleRow
    {
        public string Key;
        public string Label;
        public double Offer;
        public string Blocker;
        public string Ask;
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
        /// <summary>Whether a contract taken now starts at once, with its first cars (a career's first), not at midnight.</summary>
        bool StartsAtOnce { get; }
        void RequestLevel(ContractView mine, int level);
        /// <summary>Null if the player's contract may be raised a level now; otherwise why not, for the info line.</summary>
        string RaiseBlocker(ContractView mine);
        /// <summary>A blocked raise's row value, how far it is ("2/3 days", "needs >90%"); null if it may be raised.</summary>
        string RaiseValue(ContractView mine);
        /// <summary>Ends the player's contract at midnight, with its penalty then; one not yet started ends at once, free.</summary>
        void End(ContractView mine);
        /// <summary>The penalty for taking the player's contract down to a level (0 ends it), at its history now (design D7).</summary>
        double Penalty(ContractView mine, int target);
        /// <summary>The railroad's cash: the host's wallet, in Multiplayer.</summary>
        double Money { get; }
        /// <summary>The locomotives, or the coaches, the shop sells, each with its question for a kiosk at this place.</summary>
        IReadOnlyList<ShopRow> Shop(bool coaches, KioskPlace place);
        /// <summary>Buys a shop row for delivery by this place; the kiosk's page on how it went.</summary>
        string Buy(bool coaches, string key, KioskPlace place);
        /// <summary>The vehicles the kiosk buys back.</summary>
        IReadOnlyList<SaleRow> Selling();
        /// <summary>Sells a row back; the kiosk's page on how it went.</summary>
        string Sell(string key);
        /// <summary>Why the contracts are on hold (a mod they build on has changed), for the list's row and page; null when they run.</summary>
        string Hold { get; }
        /// <summary>Penalties owed, not yet taken from the wallet.</summary>
        double Owed { get; }
        /// <summary>A line for the Contracts menu, such as the day-duration notice; null for none.</summary>
        string Notice { get; }
        /// <summary>The contract cars without a job now: how many, and the kiosk's paragraph on them (null for none).</summary>
        (int Count, string Text) Jobless();
        /// <summary>The player's industry or bridge contract's cars now; null for a passenger contract or an offer.</summary>
        ContractCarsView Cars(ContractView mine);
        /// <summary>New cars waiting off the map for a free interchange track: how many, and the kiosk's page on them (null for none).</summary>
        (int Cars, string Page) Waiting();
        /// <summary>The game's day length, when shorter than 24 hours: the list row's value ("2 h") and its page; else null.</summary>
        (string Value, string Page)? DayLength { get; }
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
