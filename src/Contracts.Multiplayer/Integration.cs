using System;
using System.IO;
using System.Linq;
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

        /// <summary>Called by Contracts.dll once, at load, when Multiplayer runs.</summary>
        public static void Start(UnityModManager.ModEntry modEntry)
        {
            mod = modEntry;
            MultiplayerAPI.ServerStarted += OnServerStarted;
            MultiplayerAPI.ClientStarted += OnClientStarted;
            MultiplayerAPI.ServerStopped += OnStopped;
            MultiplayerAPI.ClientStopped += OnStopped;
            mod.Logger.Log($"multiplayer: its part is loaded (Multiplayer {MultiplayerAPI.MultiplayerVersion}, API {MultiplayerAPI.LoadedApiVersion})");
        }

        private static void OnServerStarted(IServer started)
        {
            try
            {
                server = started;
                started.RegisterSerializablePacket<DeskPacket>((packet, sender) =>
                    DeskNet.OnHostMessage(sender == null || sender.IsHost ? null : sender, sender?.DisplayName ?? "a player", packet.Kind, packet.Data));
                started.OnPlayerDisconnected += DeskNet.OnPlayerLeft;
                DeskNet.SendToPlayer = (player, kind, data) =>
                {
                    if (player is IPlayer to)
                        started.SendSerializablePacketToPlayer(new DeskPacket { Kind = kind, Data = data }, to);
                };
                DeskNet.CarsWithPlayers = () => started.Players.Where(p => p != null && p.IsOnCar).Select(p => p.OccupiedCar).ToList();
                EnsureTicker();
                mod.Logger.Log("multiplayer: hosting: the players' kiosks are served from here");
            }
            catch (Exception e)
            {
                mod.Logger.LogException("multiplayer: the host's side failed to start; players who join get no Contracts menu", e);
            }
        }

        private static void OnClientStarted(IClient client)
        {
            try
            {
                client.RegisterSerializablePacket<DeskPacket>(packet => DeskNet.OnClientMessage(packet.Kind, packet.Data));
                // A host's own client is not a player who joined.
                if (MultiplayerAPI.Instance == null || MultiplayerAPI.Instance.IsHost)
                    return;
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

        private static void OnStopped()
        {
            server = null;
            DeskNet.OnSessionEnded();
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
            writer.Write(Kind);
            writer.Write(Data?.Length ?? 0);
            if (Data != null && Data.Length > 0)
                writer.Write(Data);
        }

        public void Deserialize(BinaryReader reader)
        {
            Kind = reader.ReadByte();
            var count = reader.ReadInt32();
            Data = count > 0 && count <= MaxData ? reader.ReadBytes(count) : new byte[0];
        }
    }
}
