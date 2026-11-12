using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Contracts.Core.Net
{
    /// <summary>
    /// What a Multiplayer host and the players who joined say to each other about the kiosk's
    /// Contracts menu (owner, 2026-11-10: "Fix, multiplayer"; design, "The kiosk for players who
    /// joined"). The host runs the contracts and owns the wallet; a player who joined sees a
    /// snapshot of what the kiosk's screens show, and asks the host to act.
    ///
    /// - <see cref="DeskHello"/>, a player to the host: its build and protocol.
    /// - <see cref="DeskSnapshot"/>, the host to players: everything the screens read, as plain
    ///   text and numbers, with a revision that only goes up.
    /// - <see cref="DeskRequest"/>, a player to the host: take, ask for a level, end, buy, sell,
    ///   or just send the snapshot again.
    /// - <see cref="DeskResult"/>, the host to that player: done or refused, with the words for
    ///   the kiosk (a purchase's page, a refusal's reason).
    ///
    /// The Multiplayer mod carries a message as bytes; this is their form. It gzips what is 1 KiB
    /// or more and cuts off silently past 65,535 bytes, so a snapshot is kept under
    /// <see cref="MaxBytes"/> before compression: long pages are clipped (<see cref="Clip"/>).
    /// Everything read is bounded, and bytes that are not a message read as null.
    /// </summary>
    public static class DeskWire
    {
        /// <summary>Raised when a message's form changes; a host and a player with different ones don't talk.</summary>
        public const int Protocol = 1;

        public const int MaxBytes = 60_000;
        public const int MaxText = 6_000;
        public const int MaxShort = 240;
        public const int MaxRows = 400;

        public const byte Hello = 1, Request = 2, Result = 3, Snapshot = 4;

        /// <summary>A text cut to a length, with a mark that it goes on (on the host's kiosk).</summary>
        public static string Clip(string text, int max) =>
            text == null || text.Length <= max ? text : text.Substring(0, Math.Max(0, max - 24)).TrimEnd() + "\n\n(More at the host's kiosk.)";

        // ------------------------------------------------------------ writing

        public static byte[] Write(DeskHello m) => Bytes(w =>
        {
            Put(w, m.Version, MaxShort);
            w.Write(m.Protocol);
        });

        public static byte[] Write(DeskRequest m) => Bytes(w =>
        {
            w.Write(m.Seq);
            w.Write((byte)m.Action);
            Put(w, m.Id, MaxShort);
            w.Write(m.Level);
            Put(w, m.Place, MaxShort);
            w.Write(m.PlaceIsInterchange);
        });

        public static byte[] Write(DeskResult m) => Bytes(w =>
        {
            w.Write(m.Seq);
            w.Write((byte)m.Action);
            w.Write(m.Ok);
            Put(w, m.Message, MaxText);
        });

        /// <summary>A snapshot's bytes, its long pages clipped as far as it takes to stay under <see cref="MaxBytes"/>; null if it can't be made to fit.</summary>
        public static byte[] Write(DeskSnapshot s)
        {
            foreach (var page in new[] { MaxText, 1_500, 400, 0 })
            {
                var bytes = Bytes(w => Put(w, s, page));
                if (bytes.Length <= MaxBytes)
                    return bytes;
            }
            // Too many rows for one message, pages or none: nothing is sent, and the sender says so.
            return null;
        }

        private static void Put(BinaryWriter w, DeskSnapshot s, int page)
        {
            w.Write(s.Revision);
            Put(w, s.HostVersion, MaxShort);
            w.Write(s.Money);
            w.Write(s.Owed);
            Put(w, s.Notice, MaxShort);
            Put(w, s.Hold, MaxShort);
            w.Write(s.StartsAtOnce);
            w.Write(s.JoblessCount);
            Put(w, Clip(s.JoblessText, page), MaxText);
            w.Write(s.WaitingCars);
            Put(w, Clip(s.WaitingPage, page), MaxText);
            Put(w, s.DayValue, MaxShort);
            Put(w, Clip(s.DayPage, Math.Max(page, 1_500)), MaxText);
            Rows(w, s.Mine, c =>
            {
                Put(w, c.Id, MaxShort);
                Put(w, c.Short, MaxShort);
                w.Write((byte)c.Level);
                w.Write((byte)c.Requested);
                w.Write(c.Ending);
                w.Write(c.Performance ?? -1f);
                w.Write(c.ScoredDays);
                w.Write(c.OpenWaybills);
                w.Write(c.ToMove ?? -1);
                Put(w, c.Needs, MaxShort);
                w.Write(c.Passenger);
                for (var level = 1; level <= 5; level++)
                    Put(w, c.Volumes != null && c.Volumes.Length > level ? c.Volumes[level] : null, MaxShort);
                Put(w, c.Legend, MaxShort);
                Put(w, c.RaiseBlocker, MaxShort);
                Put(w, c.RaiseValue, MaxShort);
                for (var target = 0; target < 5; target++)
                    w.Write(c.Penalties != null && c.Penalties.Length > target ? c.Penalties[target] : 0.0);
                w.Write(c.HasCars);
                w.Write(c.CarsToMove);
                Put(w, c.CarsLine, MaxShort);
                Put(w, Clip(c.CarsPage, page), MaxText);
            });
            Rows(w, s.Offers, o =>
            {
                Put(w, o.Id, MaxShort);
                Put(w, o.Short, MaxShort);
                Put(w, o.Station, MaxShort);
                Put(w, o.Needs, MaxShort);
                Put(w, o.Volume, MaxShort);
                Put(w, o.Legend, MaxShort);
                w.Write(o.Passenger);
            });
            foreach (var shop in new[] { s.Locomotives, s.Coaches })
                Rows(w, shop, r =>
                {
                    Put(w, r.Key, MaxShort);
                    Put(w, r.Name, MaxShort);
                    w.Write(r.Price);
                });
            Rows(w, s.Sales, r =>
            {
                Put(w, r.Key, MaxShort);
                Put(w, r.Label, MaxShort);
                w.Write(r.Offer);
                Put(w, r.Blocker, MaxShort);
                Put(w, r.Ask, MaxText);
            });
            Rows(w, (s.Adopted ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(), kv =>
            {
                Put(w, kv.Key, MaxShort);
                Put(w, kv.Value, MaxShort);
            });
        }

        // ------------------------------------------------------------ reading

        public static DeskHello ReadHello(byte[] data) => Read(data, r => new DeskHello { Version = Text(r, MaxShort), Protocol = r.ReadInt32() });

        public static DeskRequest ReadRequest(byte[] data) => Read(data, r => new DeskRequest
        {
            Seq = r.ReadUInt16(), Action = Action(r.ReadByte()), Id = Text(r, MaxShort), Level = r.ReadInt32(),
            Place = Text(r, MaxShort), PlaceIsInterchange = r.ReadBoolean(),
        });

        public static DeskResult ReadResult(byte[] data) => Read(data, r => new DeskResult
        {
            Seq = r.ReadUInt16(), Action = Action(r.ReadByte()), Ok = r.ReadBoolean(), Message = Text(r, MaxText),
        });

        public static DeskSnapshot ReadSnapshot(byte[] data) => Read(data, r =>
        {
            var s = new DeskSnapshot
            {
                Revision = r.ReadUInt32(), HostVersion = Text(r, MaxShort), Money = r.ReadDouble(), Owed = r.ReadDouble(),
                Notice = Text(r, MaxShort), Hold = Text(r, MaxShort), StartsAtOnce = r.ReadBoolean(),
                JoblessCount = r.ReadInt32(), JoblessText = Text(r, MaxText), WaitingCars = r.ReadInt32(), WaitingPage = Text(r, MaxText),
                DayValue = Text(r, MaxShort), DayPage = Text(r, MaxText),
            };
            s.Mine = Rows(r, () =>
            {
                var c = new SnapContract
                {
                    Id = Text(r, MaxShort), Short = Text(r, MaxShort), Level = r.ReadByte(), Requested = r.ReadByte(), Ending = r.ReadBoolean(),
                };
                var performance = r.ReadSingle();
                c.Performance = performance < 0f ? (float?)null : performance;
                c.ScoredDays = r.ReadInt32();
                c.OpenWaybills = r.ReadInt32();
                var toMove = r.ReadInt32();
                c.ToMove = toMove < 0 ? (int?)null : toMove;
                c.Needs = Text(r, MaxShort);
                c.Passenger = r.ReadBoolean();
                c.Volumes = new string[6];
                for (var level = 1; level <= 5; level++)
                    c.Volumes[level] = Text(r, MaxShort);
                c.Legend = Text(r, MaxShort);
                c.RaiseBlocker = Text(r, MaxShort);
                c.RaiseValue = Text(r, MaxShort);
                c.Penalties = new double[5];
                for (var target = 0; target < 5; target++)
                    c.Penalties[target] = r.ReadDouble();
                c.HasCars = r.ReadBoolean();
                c.CarsToMove = r.ReadInt32();
                c.CarsLine = Text(r, MaxShort);
                c.CarsPage = Text(r, MaxText);
                return c;
            });
            s.Offers = Rows(r, () => new SnapOffer
            {
                Id = Text(r, MaxShort), Short = Text(r, MaxShort), Station = Text(r, MaxShort), Needs = Text(r, MaxShort),
                Volume = Text(r, MaxShort), Legend = Text(r, MaxShort), Passenger = r.ReadBoolean(),
            });
            s.Locomotives = Rows(r, () => new SnapShopRow { Key = Text(r, MaxShort), Name = Text(r, MaxShort), Price = r.ReadDouble() });
            s.Coaches = Rows(r, () => new SnapShopRow { Key = Text(r, MaxShort), Name = Text(r, MaxShort), Price = r.ReadDouble() });
            s.Sales = Rows(r, () => new SnapSaleRow
            {
                Key = Text(r, MaxShort), Label = Text(r, MaxShort), Offer = r.ReadDouble(), Blocker = Text(r, MaxShort), Ask = Text(r, MaxText),
            });
            s.Adopted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in Rows(r, () => (Key: Text(r, MaxShort), Value: Text(r, MaxShort))))
                if (kv.Key != null)
                    s.Adopted[kv.Key] = kv.Value;
            return s;
        });

        // ------------------------------------------------------------ the form

        private static byte[] Bytes(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                write(w);
                w.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>A message from its bytes; null if they are not one (cut short, too long, or not this form).</summary>
        private static T Read<T>(byte[] data, Func<BinaryReader, T> read) where T : class
        {
            if (data == null || data.Length == 0 || data.Length > MaxBytes + 1_024)
                return null;
            try
            {
                using (var stream = new MemoryStream(data))
                using (var r = new BinaryReader(stream, Encoding.UTF8))
                {
                    var message = read(r);
                    return stream.Position == stream.Length ? message : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static DeskAction Action(byte value) =>
            Enum.IsDefined(typeof(DeskAction), value) ? (DeskAction)value : throw new InvalidDataException("no such action");

        /// <summary>A text, null or cut to a length: a flag, then its UTF-8 bytes with their count.</summary>
        private static void Put(BinaryWriter w, string text, int max)
        {
            w.Write(text != null);
            if (text == null)
                return;
            var bytes = Encoding.UTF8.GetBytes(text.Length <= max ? text : text.Substring(0, max));
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        private static string Text(BinaryReader r, int max)
        {
            if (!r.ReadBoolean())
                return null;
            var count = r.ReadInt32();
            // UTF-8 takes four bytes a character at most.
            if (count < 0 || count > max * 4)
                throw new InvalidDataException("a text too long");
            var bytes = r.ReadBytes(count);
            if (bytes.Length != count)
                throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }

        private static void Rows<T>(BinaryWriter w, IReadOnlyList<T> rows, Action<T> put)
        {
            var count = Math.Min(rows?.Count ?? 0, MaxRows);
            w.Write((ushort)count);
            for (var i = 0; i < count; i++)
                put(rows[i]);
        }

        private static List<T> Rows<T>(BinaryReader r, Func<T> read)
        {
            var count = r.ReadUInt16();
            if (count > MaxRows)
                throw new InvalidDataException("too many rows");
            var rows = new List<T>(count);
            for (var i = 0; i < count; i++)
                rows.Add(read());
            return rows;
        }
    }

    /// <summary>What a player asks the host's desk to do.</summary>
    public enum DeskAction : byte
    {
        /// <summary>Nothing: send the snapshot again.</summary>
        Refresh = 0,
        Take = 1,
        Level = 2,
        End = 3,
        Buy = 4,
        Sell = 5,
    }

    public sealed class DeskHello
    {
        public string Version;
        public int Protocol;
    }

    public sealed class DeskRequest
    {
        /// <summary>The player's count of its requests, so a result is matched to what it answers.</summary>
        public ushort Seq;
        public DeskAction Action;
        /// <summary>The contract, the shop row or the sale row.</summary>
        public string Id;
        public int Level;
        /// <summary>The kiosk's place, for a purchase's delivery.</summary>
        public string Place;
        public bool PlaceIsInterchange;
    }

    public sealed class DeskResult
    {
        public ushort Seq;
        public DeskAction Action;
        public bool Ok;
        /// <summary>The words for the kiosk: a purchase's or a sale's page, or why the host refused.</summary>
        public string Message;
    }

    /// <summary>Everything the kiosk's Contracts screens read, as the host has it now.</summary>
    public sealed class DeskSnapshot
    {
        public uint Revision;
        public string HostVersion;
        public double Money, Owed;
        public string Notice, Hold;
        public bool StartsAtOnce;
        public int JoblessCount;
        public string JoblessText;
        public int WaitingCars;
        public string WaitingPage;
        public string DayValue, DayPage;
        public List<SnapContract> Mine = new List<SnapContract>();
        public List<SnapOffer> Offers = new List<SnapOffer>();
        public List<SnapShopRow> Locomotives = new List<SnapShopRow>();
        public List<SnapShopRow> Coaches = new List<SnapShopRow>();
        public List<SnapSaleRow> Sales = new List<SnapSaleRow>();
        /// <summary>A station's industry that another station's kiosk lists as its own, by station.</summary>
        public Dictionary<string, string> Adopted = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>One of the railroad's contracts, with what its screen shows.</summary>
    public sealed class SnapContract
    {
        public string Id, Short;
        public int Level, Requested;
        public bool Ending;
        public float? Performance;
        public int ScoredDays, OpenWaybills;
        public int? ToMove;
        public string Needs;
        public bool Passenger;
        /// <summary>Its cars a day at each level, by level (1 to 5).</summary>
        public string[] Volumes = new string[6];
        public string Legend, RaiseBlocker, RaiseValue;
        /// <summary>The penalty for taking it down to each level, by target (0 ends it).</summary>
        public double[] Penalties = new double[5];
        public bool HasCars;
        public int CarsToMove;
        public string CarsLine, CarsPage;
    }

    public sealed class SnapOffer
    {
        public string Id, Short;
        /// <summary>Its station; null for the bridge.</summary>
        public string Station;
        public string Needs, Volume, Legend;
        public bool Passenger;
    }

    public sealed class SnapShopRow
    {
        public string Key, Name;
        public double Price;
    }

    public sealed class SnapSaleRow
    {
        public string Key, Label;
        public double Offer;
        public string Blocker, Ask;
    }
}
