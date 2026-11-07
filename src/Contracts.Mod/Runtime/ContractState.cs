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

    /// <summary>The runtime's saved state, under its own save key (design, "Saves").</summary>
    internal sealed class ContractState
    {
        public const string Key = "contracts_mod";
        public const int CurrentVersion = 1;

        [JsonProperty("version")] public int Version = CurrentVersion;
        [JsonProperty("starter")] public bool StarterGiven;
        [JsonProperty("seed")] public ulong Seed = (ulong)DateTime.UtcNow.Ticks;
        [JsonProperty("day")] public int Day = -1;               // the contract day last rolled over at midnight
        [JsonProperty("service")] public int ServiceDay = -1;    // the contract day of the last 06:00 service
        [JsonProperty("contracts")] public List<HeldContract> Contracts = new List<HeldContract>();
        [JsonProperty("waybills")] public List<Waybill> Waybills = new List<Waybill>();
        [JsonProperty("owed")] public double Owed;              // penalties not yet taken from the wallet (ContractPay)
        [JsonProperty("bought")] public List<Purchase> Bought = new List<Purchase>();   // vehicles bought at the kiosk, from 0.1.9

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
        public static ContractState Read(SaveGameData data, out bool unreadable)
        {
            unreadable = false;
            var saved = data?.GetJObject(Key);
            if (saved == null)
                return null;
            try
            {
                var state = saved.ToObject<ContractState>();
                if (state == null || state.Version > CurrentVersion)
                {
                    unreadable = true;
                    return null;
                }
                return state;
            }
            catch (Exception e)
            {
                Main.Error($"the saved contracts can't be read ({e.Message}); contracts stay off and the save keeps them");
                unreadable = true;
                return null;
            }
        }

        public void Write(SaveGameData data) => data.SetJObject(Key, JObject.FromObject(this));

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
