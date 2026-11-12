using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using Contracts.Core.Net;
using Contracts.Mod.Runtime;
using DV.ServicePenalty.UI;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// The kiosk's Contracts menu for players who joined a Multiplayer game (owner, 2026-11-10:
    /// "Fix, multiplayer"). The host runs the contracts and owns the wallet; until now a player
    /// who joined was shown "Contracts are managed by the host", and since stations spawn no
    /// locomotives could not even buy one.
    ///
    /// - **The host** answers a player's hello with a snapshot of what the kiosk's screens show
    ///   (Contracts.Core DeskWire), sends a new one to every player whenever it changes, and
    ///   does what a player asks at its own desk, as if at its own kiosk, with the result back.
    /// - **A player who joined** has <see cref="RemoteDesk"/> behind its kiosk: it reads the last
    ///   snapshot, and sends what the player asks.
    ///
    /// This class is the seam to the Multiplayer mod. Its API is compiled into the add-on
    /// (Contracts.Multiplayer.dll, loaded only when Multiplayer runs), which sets the senders
    /// here and passes what arrives; nothing here names a Multiplayer type, so the mod loads
    /// without it. A host and a player must have the same build (the hello says which).
    /// </summary>
    public static class DeskNet
    {
        // ------------------------------------------------------------ set by the add-on

        /// <summary>A player who joined, to the host.</summary>
        public static Action<byte, byte[]> SendToHost;

        /// <summary>The host, to one player (the add-on's own player object).</summary>
        public static Action<object, byte, byte[]> SendToPlayer;

        /// <summary>The cars the players of the session stand on, on the host.</summary>
        public static Func<IEnumerable<TrainCar>> CarsWithPlayers;

        /// <summary>Why the add-on is not at work, for the kiosk of a player who joined; null when it is.</summary>
        public static string AddOnProblem = "this game has no multiplayer part of Railroad Contracts running";

        // ------------------------------------------------------------ the host

        // Players who said hello with this build: they get the snapshots.
        private static readonly List<object> players = new List<object>();
        private static uint revision;
        private static byte[] lastSent;
        private static float pushTimer;
        private static bool unknownSaid;

        // The desk of the game that runs the contracts (never the remote one: a host is not a player who joined).
        private static IContractDesk HostDesk =>
            ContractRuntime.Instance == null ? null : ReferenceEquals(KioskMenu.Desk, Remote) ? Remote.Own : KioskMenu.Desk;

        /// <summary>From the add-on, on the host: a player's message.</summary>
        public static void OnHostMessage(object player, string name, byte kind, byte[] data)
        {
            try
            {
                if (player == null)
                {
                    if (!unknownSaid)
                    {
                        unknownSaid = true;
                        Main.Warn("multiplayer: a kiosk message from a player Multiplayer doesn't know (not answered)");
                    }
                    return;
                }
                if (kind == DeskWire.Hello)
                {
                    var hello = DeskWire.ReadHello(data);
                    if (hello == null)
                        return;
                    if (hello.Protocol != DeskWire.Protocol || hello.Version != Main.Version)
                    {
                        players.Remove(player);
                        Main.Warn($"multiplayer: {name} has Railroad Contracts {hello.Version}, this host {Main.Version}: its kiosk gets no contracts");
                        Send(player, DeskWire.Result, DeskWire.Write(new DeskResult
                        {
                            Seq = 0, Action = DeskAction.Refresh, Ok = false,
                            Message = $"The host has Railroad Contracts {Main.Version}; you have {hello.Version}. Use the same version.",
                        }));
                        return;
                    }
                    if (!players.Contains(player))
                    {
                        players.Add(player);
                        Main.Log($"multiplayer: {name}'s kiosk is served from here ({players.Count} player(s))");
                    }
                    SendSnapshot(new[] { player }, force: true);
                    return;
                }
                if (kind != DeskWire.Request || !players.Contains(player))
                    return;
                var request = DeskWire.ReadRequest(data);
                if (request == null)
                    return;
                var result = Handle(request, name);
                Send(player, DeskWire.Result, DeskWire.Write(result));
                SendSnapshot(players.ToList(), force: false);
            }
            catch (Exception e)
            {
                Main.Error($"multiplayer: a message from {name} could not be dealt with: {e}");
            }
        }

        /// <summary>From the add-on, on the host: a player left.</summary>
        public static void OnPlayerLeft(object player) => players.Remove(player);

        /// <summary>From the add-on: the session ended.</summary>
        public static void OnSessionEnded()
        {
            players.Clear();
            lastSent = null;
            SendToHost = null;
            SendToPlayer = null;
            CarsWithPlayers = null;
            Remote.Stop();
        }

        /// <summary>Does at the host's desk what a player asked at its kiosk; the result for that player.</summary>
        internal static DeskResult Handle(DeskRequest request, string name)
        {
            var result = new DeskResult { Seq = request.Seq, Action = request.Action, Ok = true };
            var desk = HostDesk;
            if (desk == null)
            {
                result.Ok = false;
                result.Message = "The host's contracts are not running.";
                return result;
            }
            DeskResult Refuse(string why)
            {
                result.Ok = false;
                result.Message = why;
                return result;
            }
            var place = request.Place == null ? null : new KioskPlace { Id = request.Place, IsInterchange = request.PlaceIsInterchange, Name = request.Place };
            var mine = desk.Mine().FirstOrDefault(v => v.Id == request.Id);
            switch (request.Action)
            {
                case DeskAction.Refresh:
                    return result;
                case DeskAction.Take:
                    var offer = desk.Offers(place).FirstOrDefault(o => o.Id == request.Id);
                    if (offer == null)
                        return Refuse(mine != null ? $"{mine.Short} is already the railroad's." : "That contract is not on offer.");
                    if (offer.Needs != null)
                        return Refuse(offer.Needs);
                    desk.Take(offer, 1);
                    break;
                case DeskAction.Level:
                    if (mine == null)
                        return Refuse("That contract is not the railroad's any more.");
                    if (request.Level > mine.Level && mine.Level >= 1 && desk.RaiseBlocker(mine) is string blocker)
                        return Refuse(blocker);
                    desk.RequestLevel(mine, request.Level);
                    break;
                case DeskAction.End:
                    if (mine == null)
                        return Refuse("That contract is not the railroad's any more.");
                    desk.End(mine);
                    break;
                case DeskAction.Buy:
                    // The row's key, after "L:" for a locomotive or "C:" for coaches.
                    if (request.Id == null || request.Id.Length < 3 || request.Id[1] != ':')
                        return Refuse("That is not sold here.");
                    result.Message = desk.Buy(request.Id[0] == 'C', request.Id.Substring(2), place);
                    break;
                case DeskAction.Sell:
                    result.Message = desk.Sell(request.Id);
                    break;
            }
            Main.Log($"multiplayer: {name} at {request.Place ?? "a kiosk"}: {request.Action} {request.Id}{(request.Action == DeskAction.Level ? $" {request.Level}" : "")}");
            return result;
        }

        /// <summary>The host's desk as a snapshot, for the players' kiosks.</summary>
        internal static DeskSnapshot Snapshot(IContractDesk desk)
        {
            var jobless = desk.Jobless();
            var waiting = desk.Waiting();
            var day = desk.DayLength;
            var s = new DeskSnapshot
            {
                HostVersion = Main.Version, Money = desk.Money, Owed = desk.Owed, Notice = desk.Notice, Hold = desk.Hold, StartsAtOnce = desk.StartsAtOnce,
                JoblessCount = jobless.Count, JoblessText = jobless.Text, WaitingCars = waiting.Cars, WaitingPage = waiting.Page,
                DayValue = day?.Value, DayPage = day?.Page,
            };
            foreach (var v in desk.Mine())
            {
                var cars = desk.Cars(v);
                var c = new SnapContract
                {
                    Id = v.Id, Short = v.Short, Level = v.Level, Requested = v.RequestedLevel, Ending = v.Ending, Performance = v.Performance,
                    ScoredDays = v.ScoredDays, OpenWaybills = v.OpenWaybills, ToMove = v.ToMove, Needs = v.Needs, Passenger = RuntimeDesk.IsPassenger(v.Id),
                    Legend = desk.Legend(v), RaiseBlocker = desk.RaiseBlocker(v), RaiseValue = desk.RaiseValue(v),
                    HasCars = cars != null, CarsToMove = cars?.ToMove ?? 0, CarsLine = cars?.Line, CarsPage = cars?.Page,
                };
                for (var level = 1; level <= 5; level++)
                    c.Volumes[level] = desk.Volume(v, level);
                for (var target = 0; target < 5; target++)
                    c.Penalties[target] = target < v.Level || target == 0 ? desk.Penalty(v, target) : 0.0;
                s.Mine.Add(c);
            }
            foreach (var (station, v) in RuntimeDesk.AllOffers())
                s.Offers.Add(new SnapOffer
                {
                    Id = v.Id, Short = v.Short, Station = station, Needs = v.Needs, Volume = desk.Volume(v, 1), Legend = desk.Legend(v),
                    Passenger = RuntimeDesk.IsPassenger(v.Id),
                });
            s.Locomotives = desk.Shop(false, null).Select(r => new SnapShopRow { Key = r.Key, Name = r.Name, Price = r.Price }).ToList();
            s.Coaches = desk.Shop(true, null).Select(r => new SnapShopRow { Key = r.Key, Name = r.Name, Price = r.Price }).ToList();
            s.Sales = desk.Selling().Select(r => new SnapSaleRow { Key = r.Key, Label = r.Label, Offer = r.Offer, Blocker = r.Blocker, Ask = r.Ask }).ToList();
            foreach (var kv in RuntimeDesk.AdoptedBy)
                s.Adopted[kv.Key] = kv.Value;
            return s;
        }

        /// <summary>Sends the snapshot to players: always if forced, else only when it differs from the last one sent.</summary>
        private static void SendSnapshot(IReadOnlyList<object> to, bool force)
        {
            var desk = HostDesk;
            if (desk == null || to.Count == 0 || SendToPlayer == null)
                return;
            var snapshot = Snapshot(desk);
            snapshot.Revision = 0;
            var plain = DeskWire.Write(snapshot);
            if (plain == null)
            {
                Main.Error("multiplayer: the kiosk's snapshot is too long to send; the players' kiosks keep the last one");
                return;
            }
            var changed = lastSent == null || !plain.SequenceEqual(lastSent);
            if (!changed && !force)
                return;
            if (changed)
            {
                lastSent = plain;
                revision++;
            }
            snapshot.Revision = revision;
            var bytes = DeskWire.Write(snapshot);
            foreach (var player in changed ? players.Union(to).ToList() : to.ToList())
                Send(player, DeskWire.Snapshot, bytes);
        }

        private static void Send(object player, byte kind, byte[] data)
        {
            if (data != null)
                SendToPlayer?.Invoke(player, kind, data);
        }

        /// <summary>
        /// From the add-on, every two seconds or so, host or player: the host sends a new snapshot
        /// when what the kiosks show has changed (a midnight, a service, the host's own kiosk); a
        /// player who has none yet says hello again (a hello sent before the host could hear it
        /// is lost without a word).
        /// </summary>
        public static void Tick(float seconds)
        {
            try
            {
                if (Remote.Active)
                {
                    Remote.Tick(seconds);
                    return;
                }
                if (players.Count == 0 || HostDesk == null)
                    return;
                pushTimer -= seconds;
                if (pushTimer > 0f)
                    return;
                pushTimer = 5f;
                SendSnapshot(players.ToList(), force: false);
            }
            catch (Exception e)
            {
                Main.Error($"multiplayer: keeping the players' kiosks up to date failed: {e}");
                pushTimer = 30f;
            }
        }

        // ------------------------------------------------------------ a player who joined

        /// <summary>The desk behind the kiosk of a player who joined.</summary>
        internal static readonly RemoteDesk Remote = new RemoteDesk();

        /// <summary>From the add-on: this game joined a host's session.</summary>
        public static void OnJoined() => Remote.Start();

        /// <summary>From the add-on, on a player's game: the host's message.</summary>
        public static void OnClientMessage(byte kind, byte[] data)
        {
            try
            {
                if (kind == DeskWire.Snapshot)
                    Remote.Take(DeskWire.ReadSnapshot(data));
                else if (kind == DeskWire.Result)
                    Remote.Take(DeskWire.ReadResult(data));
            }
            catch (Exception e)
            {
                Main.Error($"multiplayer: a message from the host could not be dealt with: {e}");
            }
        }

        /// <summary>Whether the kiosk of a player who joined can show the contracts; if not, why, for the kiosk's page.</summary>
        internal static bool ClientReady(out string why)
        {
            why = null;
            if (Remote.Active && Remote.Ready)
                return true;
            why = !Remote.Active
                ? $"The host runs the contracts, and this kiosk can't show them: {AddOnProblem}."
                : Remote.Refused ?? "Waiting for the host's contracts.\n\nThe host runs them; this kiosk shows them once the host has sent them. Try again in a moment. " +
                  $"If this stays, the host may not have Railroad Contracts {Main.Version}.";
            return false;
        }
    }

    /// <summary>
    /// The desk behind the kiosk of a player who joined a Multiplayer game: it reads the host's
    /// last snapshot (Contracts.Core RemoteDeskModel) and sends what the player asks to the
    /// host. What is asked shows at once; the host's next snapshot says what came of it, and a
    /// refusal is told on the list's line. A purchase's or a sale's page says the host was
    /// asked, and is replaced by the host's answer when it comes.
    /// </summary>
    internal sealed class RemoteDesk : IContractDesk
    {
        private readonly RemoteDeskModel model = new RemoteDeskModel();
        private IContractDesk own;
        private float helloTimer;
        // The page awaiting the host's answer: its request, and where it is shown.
        private ushort awaiting;
        private DisplayScreenSwitcher awaitingKiosk;
        private CareerManagerInfoScreen awaitingInfo;
        private IDisplayScreen awaitingBack;
        private string awaitingTitle;

        public bool Active { get; private set; }
        internal IContractDesk Own => own;
        public bool Ready => model.Ready;
        public string Refused { get; private set; }
        internal RemoteDeskModel Model => model;

        /// <summary>
        /// This game joined a host: the kiosk reads the host's contracts from now on.
        /// <paramref name="takeKiosk"/> false leaves the kiosk its own desk (the probe, which is
        /// host and player at once).
        /// </summary>
        public void Start(bool takeKiosk = true)
        {
            if (Active)
                return;
            Active = true;
            Refused = null;
            model.Clear();
            awaiting = 0;
            own = null;
            if (takeKiosk)
            {
                own = KioskMenu.Desk;
                KioskMenu.Desk = this;
            }
            helloTimer = 0f;
            Main.Log("multiplayer: joined a host; the kiosk's Contracts menu is the host's");
        }

        /// <summary>The session ended: the kiosk is this game's own again.</summary>
        public void Stop()
        {
            if (!Active)
                return;
            Active = false;
            model.Clear();
            if (ReferenceEquals(KioskMenu.Desk, this))
                KioskMenu.Desk = own;
            own = null;
        }

        public void Tick(float seconds)
        {
            helloTimer -= seconds;
            if (helloTimer > 0f)
                return;
            // Until the host's first snapshot, a hello every few seconds; after it, now and then, so a host that started again finds this player.
            helloTimer = model.Ready ? 60f : 4f;
            if (Refused == null || !model.Ready)
                DeskNet.SendToHost?.Invoke(DeskWire.Hello, DeskWire.Write(new DeskHello { Version = Main.Version, Protocol = DeskWire.Protocol }));
        }

        public void Take(DeskSnapshot snapshot)
        {
            if (!Active || snapshot == null)
                return;
            if (model.Apply(snapshot))
                Refused = null;
        }

        public void Take(DeskResult result)
        {
            if (!Active || result == null)
                return;
            // The host's answer to the hello: a different build.
            if (result.Seq == 0)
            {
                if (!result.Ok)
                {
                    Refused = result.Message;
                    model.Clear();
                    Main.Warn($"multiplayer: the host refused this game's kiosk: {result.Message}");
                }
                return;
            }
            var paged = result.Action == DeskAction.Buy || result.Action == DeskAction.Sell;
            if (paged && result.Seq == awaiting && awaitingKiosk != null && ReferenceEquals(awaitingKiosk.CurrentScreen, awaitingInfo))
            {
                KioskMenu.ShowInfo(awaitingInfo, awaitingBack, awaitingTitle, result.Message ?? "Done.");
                awaitingKiosk.SetActiveDisplay(awaitingInfo);
            }
            else if (!result.Ok || paged)
                KioskMenu.Tell(Line(result.Message));
            if (result.Seq == awaiting)
                awaiting = 0;
        }

        /// <summary>A message's first line, cut to the list's info line.</summary>
        private static string Line(string message)
        {
            var line = (message ?? "The host refused").Split('\n')[0].Trim();
            return line.Length <= KioskMenu.InfoLineChars ? line : line.Substring(0, KioskMenu.InfoLineChars - 1).TrimEnd() + ".";
        }

        /// <summary>The kiosk shows a page that the host's answer will replace (ContractScreen, after a purchase or a sale).</summary>
        internal void Awaiting(DisplayScreenSwitcher kiosk, CareerManagerInfoScreen info, IDisplayScreen back, string title)
        {
            if (awaiting == 0)
                return;
            awaitingKiosk = kiosk;
            awaitingInfo = info;
            awaitingBack = back;
            awaitingTitle = title;
        }

        private ushort Ask(DeskAction action, string id, int level = 0, KioskPlace place = null)
        {
            var seq = model.NextSeq();
            DeskNet.SendToHost?.Invoke(DeskWire.Request, DeskWire.Write(new DeskRequest
            {
                Seq = seq, Action = action, Id = id, Level = level, Place = place?.Id, PlaceIsInterchange = place?.IsInterchange ?? false,
            }));
            return seq;
        }

        private static ContractView View(SnapContract c) => new ContractView
        {
            Id = c.Id, Short = c.Short, IsOffer = false, Level = c.Level, RequestedLevel = c.Requested, Ending = c.Ending, Performance = c.Performance,
            ScoredDays = c.ScoredDays, OpenWaybills = c.OpenWaybills, ToMove = c.ToMove, Needs = c.Needs,
        };

        // ------------------------------------------------------------ the desk

        public IReadOnlyList<ContractView> Offers(KioskPlace place) =>
            model.OffersAt(RuntimeDesk.StationOf(place), place != null && place.IsInterchange)
                .Select(o => new ContractView { Id = o.Id, Short = o.Short, IsOffer = true, Needs = o.Needs }).ToList();

        public IReadOnlyList<ContractView> Mine() =>
            ContractCars.MostToMoveFirst((model.Snapshot?.Mine ?? new List<SnapContract>()).Select(View), v => v.ToMove).ToList();

        public string Volume(ContractView contract, int level)
        {
            var mine = model.Mine(contract.Id);
            if (mine != null)
                return level >= 1 && level <= 5 ? mine.Volumes[level] ?? "" : "";
            return model.Snapshot?.Offers.FirstOrDefault(o => o.Id == contract.Id)?.Volume ?? "";
        }

        public string Legend(ContractView contract) =>
            model.Mine(contract.Id)?.Legend ?? model.Snapshot?.Offers.FirstOrDefault(o => o.Id == contract.Id)?.Legend ?? "";

        public bool StartsAtOnce => model.Snapshot?.StartsAtOnce ?? false;

        public void Take(ContractView offer, int level)
        {
            model.Took(offer.Id);
            Ask(DeskAction.Take, offer.Id, 1);
        }

        public void RequestLevel(ContractView mine, int level)
        {
            model.AskedLevel(mine.Id, level);
            Ask(DeskAction.Level, mine.Id, level);
        }

        public string RaiseBlocker(ContractView mine) => model.Mine(mine.Id) is SnapContract c ? c.RaiseBlocker : "Not the railroad's contract";

        public string RaiseValue(ContractView mine) => model.Mine(mine.Id) is SnapContract c ? c.RaiseValue : "locked";

        public void End(ContractView mine)
        {
            model.AskedEnd(mine.Id);
            Ask(DeskAction.End, mine.Id);
        }

        public double Penalty(ContractView mine, int target) =>
            model.Mine(mine.Id) is SnapContract c && target >= 0 && target < c.Penalties.Length ? c.Penalties[target] : 0.0;

        public double Money => model.Snapshot?.Money ?? 0.0;

        public IReadOnlyList<ShopRow> Shop(bool coaches, KioskPlace place) =>
            ((coaches ? model.Snapshot?.Coaches : model.Snapshot?.Locomotives) ?? new List<SnapShopRow>())
            .Select(r => new ShopRow
            {
                Key = r.Key, Name = r.Name, Price = r.Price,
                Ask = $"Buy {r.Name} for ${r.Price:N0}? The railroad's cash pays for it, and it arrives at once on a free interchange track." +
                      (coaches ? " Bring them to a platform to board." : ""),
            }).ToList();

        public string Buy(bool coaches, string key, KioskPlace place)
        {
            awaiting = Ask(DeskAction.Buy, (coaches ? "C:" : "L:") + key, 0, place);
            return $"The host was asked for {key}.\n\nIts answer comes here in a moment.";
        }

        public IReadOnlyList<SaleRow> Selling() =>
            (model.Snapshot?.Sales ?? new List<SnapSaleRow>())
            .Select(r => new SaleRow { Key = r.Key, Label = r.Label, Offer = r.Offer, Blocker = r.Blocker, Ask = r.Ask }).ToList();

        public string Sell(string key)
        {
            awaiting = Ask(DeskAction.Sell, key);
            return "The host was asked to sell it.\n\nIts answer comes here in a moment.";
        }

        public string Hold => model.Snapshot?.Hold;

        public double Owed => model.Snapshot?.Owed ?? 0.0;

        public string Notice => model.Snapshot?.Notice;

        public (int Count, string Text) Jobless() => (model.Snapshot?.JoblessCount ?? 0, model.Snapshot?.JoblessText);

        public (int Cars, string Page) Waiting() => (model.Snapshot?.WaitingCars ?? 0, model.Snapshot?.WaitingPage);

        public ContractCarsView Cars(ContractView mine) =>
            model.Mine(mine.Id) is SnapContract c && c.HasCars ? new ContractCarsView { ToMove = c.CarsToMove, Line = c.CarsLine, Page = c.CarsPage } : null;

        public (string Value, string Page)? DayLength =>
            model.Snapshot?.DayPage is string page ? (model.Snapshot.DayValue, page) : ((string, string)?)null;
    }
}
