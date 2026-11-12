using System;
using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Fields are filled by the JSON reader.
#pragma warning disable CS0649

namespace Contracts.Mod.Runtime
{
    /// <summary>A contract the player holds (design D11, D13): an industry, a city's passenger service, or the bridge, at a level.</summary>
    internal sealed class HeldContract
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("short")] public string Short;
        [JsonProperty("level")] public int Level;          // 0 until its first midnight
        [JsonProperty("requested")] public int Requested;  // the level from the next midnight
        [JsonProperty("since")] public int SinceDay;       // the contract day it was taken
        // Asked to end: it ends at the next midnight, with its penalty then (from 0.1.12; before, at once).
        [JsonProperty("ending")] public bool Ending;
        // Its standing (Contracts.Core ContractStanding): daily scores at this level, newest last,
        // and the final ages (days) of its bills closed since the last midnight.
        [JsonProperty("history")] public List<double> History = new List<double>();
        [JsonProperty("closed")] public List<double> Closed = new List<double>();
        // A passenger contract's departures (Contracts.Core PassengerService): those due and not
        // yet formed (when each was made due), those running, and the kind of the next one.
        [JsonProperty("due")] public List<double> Due = new List<double>();
        [JsonProperty("running")] public List<Departure> Running = new List<Departure>();
        [JsonProperty("express")] public bool NextExpress = true;
    }

    /// <summary>A passenger contract's departure that is running: its train's job, and when it was made due.</summary>
    internal sealed class Departure
    {
        [JsonProperty("job")] public string Job;
        [JsonProperty("opened")] public double Opened;
    }

    /// <summary>
    /// Where a contract car is in its work: what its current job leads to. A save holds the
    /// number, so new stages go last.
    /// </summary>
    internal enum Stage
    {
        Idle,          // no work: the next service plans it
        ToShipper,     // an empty on its way to a shipper: to load, or with no load planned (freed at an unloader) to wait there
        Loading,       // at the shipper's station: being spotted on its loading track, or waiting for room there
        ToReceiver,    // a load on its way to a receiver's station
        Unloading,     // at the receiver's station: being spotted on its unloading track, or waiting for room there
        ToInterchange, // leaving the map: on its way to an interchange track
        OffMap,        // on an interchange track, waiting for the service to take it away
        BeingLoaded,   // spotted: the shipper loads it, then it goes out at once (IndustryWork)
        BeingUnloaded, // spotted: the receiver unloads it, then it goes out empty at once
    }

    /// <summary>
    /// A contract car's waybill (design, "Waybills and work"): the contract it serves, its cargo
    /// (loaded or to load), where it goes next, and where the load goes after that.
    /// </summary>
    internal sealed class Waybill
    {
        [JsonProperty("guid")] public string Guid;
        [JsonProperty("contract")] public string Contract;
        [JsonProperty("cargo")] public string Cargo;        // CargoType name; null for an empty with no load planned
        [JsonProperty("next")] public string Next;          // a station yard ID or an interchange id
        [JsonProperty("then")] public string Then;          // for an empty to load: where the load goes
        [JsonProperty("stage")] public Stage Stage;
        [JsonProperty("at")] public string At;              // the last station or interchange it was known at
        [JsonProperty("opened")] public double? Opened;     // when its open bill was written (ContractRuntime.Clock); null with none open
        [JsonProperty("spotted")] public double? Spotted;   // when it was spotted at its industry (BeingLoaded, BeingUnloaded)
        [JsonProperty("ready")] public double? Ready;       // when its industry is done with it, once its work has begun
    }

    /// <summary>
    /// A vehicle the player got at the kiosk (or a new career's starter), for its sale
    /// (VehicleSales): its share of what was paid, and the purchase it came with (a
    /// locomotive's tender, a train of coaches), sold together.
    /// </summary>
    internal sealed class Purchase
    {
        [JsonProperty("guid")] public string Guid;
        [JsonProperty("set")] public string Set;       // the guid of the purchase's first car
        [JsonProperty("price")] public double Price;   // this car's share of what was paid
    }

    /// <summary>
    /// New cars a service had no room for on their interchange's tracks (Contracts.Core
    /// ServiceRetry): an order's cars still to place, waiting off the map for the service to
    /// come again.
    /// </summary>
    internal sealed class WaitingOrder
    {
        [JsonProperty("interchange")] public string Interchange;
        [JsonProperty("contract")] public string Contract;
        [JsonProperty("type")] public string CarType;
        [JsonProperty("cargo")] public string Cargo;
        [JsonProperty("to")] public string BilledTo;
        [JsonProperty("then")] public string Then;
        [JsonProperty("loaded")] public bool Loaded;
        [JsonProperty("count")] public int Count;

        public static WaitingOrder Of(Contracts.Core.Industries.Order o) => new WaitingOrder
        {
            Interchange = o.Interchange, Contract = o.Contract, CarType = o.CarType, Cargo = o.Cargo,
            BilledTo = o.BilledTo, Then = o.Then, Loaded = o.Loaded, Count = o.Count,
        };

        public Contracts.Core.Industries.Order Order() => new Contracts.Core.Industries.Order
        {
            Interchange = Interchange, Contract = Contract, CarType = CarType, Cargo = Cargo,
            BilledTo = BilledTo, Then = Then, Loaded = Loaded, Count = Count,
        };
    }

    /// <summary>
    /// The runtime's saved state, under its own save key (design, "Saves").
    ///
    /// **Versions.** <see cref="Version"/> says who can read a save, not who wrote it:
    /// - a field added with a harmless default (as every one since 0.1.0: the standing, owed,
    ///   the industries' work, the purchases, an end asked for, the cars waiting for room) leaves
    ///   it alone. An older build then reads the save without the field and drops it on its next
    ///   save, which loses that detail and nothing else;
    /// - a change an older build would misread (a field's meaning, a stage renumbered) raises
    ///   it, and older builds then leave the save alone with contracts off, as <see cref="Parse"/>
    ///   does for a newer one.
    /// <see cref="ModVersion"/> names the build that wrote it, for the log. What is read is put
    /// right by <see cref="Normalize"/>: a list left out or null, a stage a newer build added.
    /// </summary>
    internal sealed class ContractState
    {
        public const string Key = "contracts_mod";
        public const int CurrentVersion = 1;

        [JsonProperty("version")] public int Version = CurrentVersion;
        [JsonProperty("mod")] public string ModVersion;          // the build that wrote the save (from 0.1.12)
        [JsonProperty("starter")] public bool StarterGiven;
        [JsonProperty("starterDue")] public bool StarterDue;     // a new career whose starter could not be delivered yet: tried again at each load
        [JsonProperty("seed")] public ulong Seed = (ulong)DateTime.UtcNow.Ticks;
        [JsonProperty("day")] public int Day = -1;               // the contract day last rolled over at midnight
        [JsonProperty("service")] public int ServiceDay = -1;    // the contract day of the last 06:00 service
        [JsonProperty("contracts")] public List<HeldContract> Contracts = new List<HeldContract>();
        [JsonProperty("waybills")] public List<Waybill> Waybills = new List<Waybill>();
        [JsonProperty("owed")] public double Owed;              // penalties not yet taken from the wallet (ContractPay)
        [JsonProperty("bought")] public List<Purchase> Bought = new List<Purchase>();   // vehicles bought at the kiosk, from 0.1.9
        // New cars the last service had no room for, and when it comes again for them (from 0.1.12).
        [JsonProperty("waiting")] public List<WaitingOrder> Waiting = new List<WaitingOrder>();
        [JsonProperty("retry")] public double? RetryAt;

        [JsonIgnore] private Dictionary<string, Waybill> byGuid;

        public Waybill Of(string guid)
        {
            if (guid == null)
                return null;
            if (byGuid == null || byGuid.Count != Waybills.Count)
                byGuid = Waybills.Where(w => w.Guid != null).GroupBy(w => w.Guid).ToDictionary(g => g.Key, g => g.First());
            return byGuid.TryGetValue(guid, out var w) ? w : null;
        }

        public void Add(Waybill w)
        {
            Waybills.Add(w);
            byGuid = null;
        }

        public void Remove(string guid)
        {
            Waybills.RemoveAll(w => w.Guid == guid);
            byGuid = null;
        }

        /// <summary>The state in a save, or null if it has none or it can't be read (then the runtime stays off).</summary>
        public static ContractState Read(SaveGameData data, out bool unreadable) => Parse(data?.GetJObject(Key), out unreadable);

        /// <summary>The state from its saved form; null with none, or if it can't be read or a newer build's format (said in the log).</summary>
        public static ContractState Parse(JObject saved, out bool unreadable)
        {
            unreadable = false;
            if (saved == null)
                return null;
            try
            {
                var state = saved.ToObject<ContractState>();
                if (state == null || state.Version > CurrentVersion)
                {
                    Main.Error($"the saved contracts are in a newer format (version {state?.Version}, written by {state?.ModVersion ?? "a newer build"}; " +
                               $"this build reads up to {CurrentVersion}); contracts stay off and the save keeps them. Update Railroad Contracts");
                    unreadable = true;
                    return null;
                }
                var mended = state.Normalize();
                if (mended.Count > 0)
                    Main.Warn($"the saved contracts (written by {state.ModVersion ?? "0.1.11 or earlier"}) were put right: {string.Join("; ", mended)}");
                return state;
            }
            catch (Exception e)
            {
                Main.Error($"the saved contracts can't be read ({e.Message}); contracts stay off and the save keeps them");
                unreadable = true;
                return null;
            }
        }

        /// <summary>
        /// Puts right what a save can hold that the runtime can't work with: lists left null, records
        /// with no id, levels out of range, a stage this build doesn't know (a newer build's: the
        /// car goes idle, and the next service plans it again). Returns what it changed.
        /// </summary>
        public List<string> Normalize()
        {
            var mended = new List<string>();
            Contracts = Contracts ?? new List<HeldContract>();
            Waybills = Waybills ?? new List<Waybill>();
            Bought = Bought ?? new List<Purchase>();
            Waiting = Waiting ?? new List<WaitingOrder>();
            var before = (Contracts.Count, Waybills.Count, Bought.Count, Waiting.Count);
            Contracts.RemoveAll(c => c == null || string.IsNullOrEmpty(c.Id));
            Waybills.RemoveAll(w => w == null || string.IsNullOrEmpty(w.Guid));
            Bought.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Guid));
            Waiting.RemoveAll(o => o == null || o.Count <= 0 || string.IsNullOrEmpty(o.Interchange) || string.IsNullOrEmpty(o.CarType));
            var dropped = before.Item1 - Contracts.Count + before.Item2 - Waybills.Count + before.Item3 - Bought.Count + before.Item4 - Waiting.Count;
            if (dropped > 0)
                mended.Add($"{dropped} record(s) without an id dropped");
            foreach (var c in Contracts)
            {
                c.History = c.History ?? new List<double>();
                c.Closed = c.Closed ?? new List<double>();
                c.Due = c.Due ?? new List<double>();
                c.Running = c.Running ?? new List<Departure>();
                c.Running.RemoveAll(r => r == null);
                var (level, requested) = (Math.Max(0, Math.Min(5, c.Level)), Math.Max(0, Math.Min(5, c.Requested)));
                if (level != c.Level || requested != c.Requested)
                {
                    mended.Add($"{c.Id}'s level {c.Level} (asked {c.Requested}) brought within 0 to 5");
                    c.Level = level;
                    c.Requested = requested;
                }
            }
            foreach (var w in Waybills.Where(w => !Enum.IsDefined(typeof(Stage), w.Stage)))
            {
                mended.Add($"car {w.Guid}'s stage {(int)w.Stage}, unknown to this build, made idle");
                w.Stage = Stage.Idle;
                w.Opened = w.Spotted = w.Ready = null;
            }
            if (Owed < 0.0 || double.IsNaN(Owed))
            {
                mended.Add($"owed {Owed} made 0");
                Owed = 0.0;
            }
            byGuid = null;
            return mended;
        }

        public void Write(SaveGameData data)
        {
            ModVersion = Main.Version;
            data.SetJObject(Key, JObject.FromObject(this));
        }

        /// <summary>The guids of the contract cars in a save, read before the runtime starts (for the deleter guard at load).</summary>
        public static HashSet<string> GuidsIn(SaveGameData data)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (data?.GetJObject(Key)?["waybills"] is JArray list)
                    foreach (var w in list)
                        if (w["guid"]?.ToString() is string g && g.Length > 0)
                            result.Add(g);
            }
            catch (Exception e)
            {
                Main.Error($"reading the contract cars from the save failed: {e.Message}");
            }
            return result;
        }

        public static SaveGameData CurrentSave => SingletonBehaviour<SaveGameManager>.Instance?.data;
    }
}
