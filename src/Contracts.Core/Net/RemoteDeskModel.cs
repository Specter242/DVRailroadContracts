using System;
using System.Collections.Generic;
using System.Linq;

namespace Contracts.Core.Net
{
    /// <summary>The order of the kiosk's offers, the same at the host's kiosk and at a player's who joined.</summary>
    public static class DeskOrder
    {
        /// <summary>
        /// An offer's place in the list: this place's own first (at an interchange office, the
        /// bridge once it can be taken), then those the player may take, then those locked; by id
        /// within each.
        /// </summary>
        public static int Rank(string offerStation, bool locked, ICollection<string> here, bool atInterchange) =>
            (offerStation == null ? atInterchange && !locked : here != null && here.Contains(offerStation)) ? 0 : locked ? 2 : 1;

        /// <summary>What a kiosk counts as here: its station (an interchange office's is the station beside it) and the industry that station's kiosk lists as its own.</summary>
        public static HashSet<string> Here(string station, IReadOnlyDictionary<string, string> adopted)
        {
            var here = new HashSet<string>(StringComparer.Ordinal);
            if (station == null)
                return here;
            here.Add(station);
            if (adopted != null && adopted.TryGetValue(station, out var also) && also != null)
                here.Add(also);
            return here;
        }
    }

    /// <summary>
    /// The desk as a player who joined has it: the host's last snapshot, and what the player has
    /// asked for since, shown at once (the host's next snapshot says what came of it).
    /// </summary>
    public sealed class RemoteDeskModel
    {
        private ushort seq;

        /// <summary>The host's last snapshot, with the player's own asks applied; null before the first.</summary>
        public DeskSnapshot Snapshot { get; private set; }

        public bool Ready => Snapshot != null;

        /// <summary>Takes a snapshot unless an older one than is held; a host that started again counts from 0 and is taken.</summary>
        public bool Apply(DeskSnapshot snapshot)
        {
            if (snapshot == null)
                return false;
            if (Snapshot != null && snapshot.Revision <= Snapshot.Revision && Snapshot.Revision - snapshot.Revision < 1_000)
                return false;
            Snapshot = snapshot;
            return true;
        }

        /// <summary>Forgets the host's snapshot: the session ended.</summary>
        public void Clear() => Snapshot = null;

        public ushort NextSeq() => ++seq == 0 ? ++seq : seq;

        /// <summary>The offers in a kiosk's order.</summary>
        public List<SnapOffer> OffersAt(string station, bool atInterchange)
        {
            if (Snapshot == null)
                return new List<SnapOffer>();
            var here = DeskOrder.Here(station, Snapshot.Adopted);
            return Snapshot.Offers
                .OrderBy(o => DeskOrder.Rank(o.Station, o.Needs != null, here, atInterchange))
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .ToList();
        }

        public SnapContract Mine(string id) => Snapshot?.Mine.FirstOrDefault(c => c.Id == id);

        /// <summary>The player took an offer: it is among the railroad's contracts at once, new, or at level 1 if it starts at once.</summary>
        public void Took(string id)
        {
            var offer = Snapshot?.Offers.FirstOrDefault(o => o.Id == id);
            if (offer == null || offer.Needs != null)
                return;
            Snapshot.Offers.Remove(offer);
            var volumes = new string[6];
            volumes[1] = offer.Volume;
            Snapshot.Mine.Add(new SnapContract
            {
                Id = offer.Id, Short = offer.Short, Level = Snapshot.StartsAtOnce ? 1 : 0, Requested = 1, Passenger = offer.Passenger,
                Volumes = volumes, Legend = offer.Legend,
            });
            Snapshot.StartsAtOnce = false;
        }

        /// <summary>The player asked for a level from midnight (keeping its level takes back an end asked for).</summary>
        public void AskedLevel(string id, int level)
        {
            var c = Mine(id);
            if (c == null)
                return;
            c.Ending = false;
            c.Requested = Math.Max(1, Math.Min(5, level));
        }

        /// <summary>The player asked to end a contract: at midnight, or at once for one not yet started.</summary>
        public void AskedEnd(string id)
        {
            var c = Mine(id);
            if (c == null)
                return;
            if (c.Level < 1)
                Snapshot.Mine.Remove(c);
            else
            {
                c.Ending = true;
                c.Requested = c.Level;
            }
        }
    }
}
