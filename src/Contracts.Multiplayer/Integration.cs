using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Contracts.Mod.Kiosk;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using UnityEngine;
using UnityModManagerNet;

namespace Contracts.Multiplayer
{
    /// <summary>
    /// The kiosk's messages over the Multiplayer mod (Contracts.Mod Kiosk/DeskNet has what they
    /// mean, Contracts.Core DeskWire their form). One packet type carries them all as bytes. On
    /// the host, a player's packets go to <see cref="DeskNet.OnHostMessage"/> and the host's
    /// answers to that player; on a game that joined, the host's packets go to
    /// <see cref="DeskNet.OnClientMessage"/>. Multiplayer starts a server and a client of its own
    /// on a host too (and in its single-player mode): that client is not a player who joined.
    /// </summary>
    public static class Integration
    {
        private static UnityModManager.ModEntry mod;
        private static IServer server;
        private static Ticker ticker;
        private static bool carsWarned;
        private static long lastPacketWarnTicks;

        internal static void Warn(string message) => mod?.Logger.Warning(message);

        internal static void WarnPacket(string message)
        {
            var now = DateTime.UtcNow.Ticks;
            var prev = Interlocked.Read(ref lastPacketWarnTicks);
            if (now - prev < TimeSpan.TicksPerSecond * 10)
                return;
            if (Interlocked.CompareExchange(ref lastPacketWarnTicks, now, prev) == prev)
                mod?.Logger.Warning(message);
        }

        private static void WarnCarsOnce(string reason)
        {
            if (carsWarned)
                return;
            carsWarned = true;
            Warn($"multiplayer: could not read players' cars ({reason}); blocking vehicle sales until available");
        }

        /// <summary>Called by Contracts.dll once, at load, when Multiplayer runs.</summary>
        public static void Start(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            MultiplayerAPI.ServerStarted += OnServerStarted;
            MultiplayerAPI.ClientStarted += OnClientStarted;
            MultiplayerAPI.ServerStopped += OnServerStopped;
            MultiplayerAPI.ClientStopped += OnClientStopped;
            mod.Logger.Log($"multiplayer: its part is loaded (Multiplayer {MultiplayerAPI.MultiplayerVersion}, API {MultiplayerAPI.LoadedApiVersion})");
        }

        private static void OnServerStarted(IServer started)
        {
            try
            {
                if (server != null)
                    server.OnPlayerDisconnected -= DeskNet.OnPlayerLeft;
                DeskNet.OnHostStopped();
                carsWarned = false;
                server = started;
                started.RegisterSerializablePacket<DeskPacket>((packet, sender) =>
                {
                    if (packet == null || packet.Data == null)
                        return;
                    DeskNet.OnHostMessage(sender == null || sender.IsHost ? null : sender, sender?.DisplayName ?? "a player", packet.Kind, packet.Data);
                });
                started.OnPlayerDisconnected += DeskNet.OnPlayerLeft;
                DeskNet.SendToPlayer = (player, kind, data) =>
                {
                    if (ReferenceEquals(server, started) && player is IPlayer to)
                        started.SendSerializablePacketToPlayer(new DeskPacket { Kind = kind, Data = data }, to);
                };
                DeskNet.CarsWithPlayers = () => ReadCarsWithPlayers(started);
                EnsureTicker();
                mod.Logger.Log("multiplayer: hosting: the players' kiosks are served from here");
            }
            catch (Exception e)
            {
                mod.Logger.LogException("multiplayer: the host's side failed to start; players who join get no Contracts menu", e);
            }
        }

        private static IEnumerable<TrainCar> ReadCarsWithPlayers(IServer current)
        {
            if (current == null || !ReferenceEquals(server, current))
                return null;
            try
            {
                var all = current.Players;
                if (all == null)
                {
                    WarnCarsOnce("server.Players is null");
                    return null;
                }
                var result = new List<TrainCar>();
                foreach (var player in all)
                {
                    if (player == null)
                    {
                        WarnCarsOnce("server.Players contained a null player");
                        return null;
                    }
                    if (!player.IsOnCar)
                        continue;
                    var car = player.OccupiedCar;
                    if (car == null)
                    {
                        WarnCarsOnce("a player is on a car whose TrainCar is unavailable");
                        return null;
                    }
                    result.Add(car);
                }
                carsWarned = false;
                return result;
            }
            catch (Exception e)
            {
                WarnCarsOnce(e.Message);
                return null;
            }
        }

        private static void OnClientStarted(IClient client)
        {
            try
            {
                client.RegisterSerializablePacket<DeskPacket>(packet =>
                {
                    if (packet == null || packet.Data == null)
                        return;
                    DeskNet.OnClientMessage(packet.Kind, packet.Data);
                });
                // A host's own client is not a player who joined.
                if (MultiplayerAPI.Instance == null || MultiplayerAPI.Instance.IsHost)
                    return;
                DeskNet.AddOnProblem = null;
                DeskNet.SendToHost = (kind, data) => client.SendSerializablePacketToServer(new DeskPacket { Kind = kind, Data = data });
                DeskNet.OnJoined();
                EnsureTicker();
            }
            catch (Exception e)
            {
                DeskNet.AddOnProblem = "its multiplayer part failed to join the host";
                mod.Logger.LogException("multiplayer: joining failed on this side; the kiosk shows no contracts", e);
            }
        }

        private static void OnServerStopped()
        {
            if (server != null)
            {
                server.OnPlayerDisconnected -= DeskNet.OnPlayerLeft;
                server = null;
            }
            carsWarned = false;
            DeskNet.OnHostStopped();
        }

        private static void OnClientStopped()
        {
            DeskNet.OnClientStopped();
        }

        private static void EnsureTicker()
        {
            if (ticker != null)
                return;
            var go = new GameObject("[contracts multiplayer]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            ticker = go.AddComponent<Ticker>();
        }

        /// <summary>Keeps the kiosks up to date: DeskNet.Tick every two seconds, in a session.</summary>
        private sealed class Ticker : MonoBehaviour
        {
            private float timer;

            private void Update()
            {
                timer -= Time.unscaledDeltaTime;
                if (timer > 0f)
                    return;
                timer = 2f;
                DeskNet.Tick(2f);
            }
        }
    }

    /// <summary>A kiosk message: its kind (DeskWire) and its bytes.</summary>
    public sealed class DeskPacket : ISerializablePacket
    {
        // The host's snapshot is kept under this before Multiplayer's own compression (DeskWire.MaxBytes).
        private const int MaxData = 64_000;

        public byte Kind;
        public byte[] Data = new byte[0];

        public void Serialize(BinaryWriter writer)
        {
            var length = Data?.Length ?? 0;
            if (length > MaxData)
            {
                Integration.Warn($"multiplayer: refusing to send oversized DeskPacket kind {Kind} ({length} bytes, max {MaxData})");
                throw new InvalidDataException($"DeskPacket length {length} exceeds maximum {MaxData}.");
            }
            writer.Write(Kind);
            writer.Write(length);
            if (length > 0)
                writer.Write(Data);
        }

        public void Deserialize(BinaryReader reader)
        {
            // Never throw out of Deserialize: Multiplayer's OnNetworkReceive only catches ParseException,
            // so other exceptions escape into LiteNetLib.NetManager.PollEvents and drop the rest of the batch.
            Data = null;
            if (reader == null)
            {
                Integration.WarnPacket("multiplayer: rejected DeskPacket with null reader");
                return;
            }
            try
            {
                var stream = reader.BaseStream;
                if (stream != null && stream.CanSeek && stream.Length - stream.Position < 1 + sizeof(int))
                {
                    Integration.WarnPacket($"multiplayer: rejected truncated DeskPacket header ({stream.Length - stream.Position} byte(s) available)");
                    return;
                }
                Kind = reader.ReadByte();
                var count = reader.ReadInt32();
                if (count <= 0 || count > MaxData)
                {
                    Integration.WarnPacket($"multiplayer: rejected DeskPacket kind {Kind} with invalid length {count} (expected 1..{MaxData})");
                    return;
                }
                if (stream != null && stream.CanSeek && stream.Length - stream.Position < count)
                {
                    Integration.WarnPacket($"multiplayer: rejected truncated DeskPacket kind {Kind} (expected {count} bytes, only {stream.Length - stream.Position} available)");
                    return;
                }
                var bytes = reader.ReadBytes(count);
                if (bytes.Length != count)
                {
                    Integration.WarnPacket($"multiplayer: rejected truncated DeskPacket kind {Kind} (expected {count} bytes, got {bytes.Length})");
                    return;
                }
                Data = bytes;
            }
            catch (Exception e)
            {
                Data = null;
                Integration.WarnPacket($"multiplayer: rejected malformed DeskPacket ({e.Message})");
            }
        }
    }
}
