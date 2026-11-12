using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DV.ServicePenalty.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// The Contracts entry in every career manager kiosk's main menu (design D9).
    ///
    /// The kiosk's display has more text lines than its main menu uses: the menu writes four
    /// (fees, licences, owned vehicles, stats) down one column, and the line below Stats is
    /// free on that screen. Contracts goes there, as a fifth entry of the same menu, so it is
    /// selected and highlighted like the stock four. Confirming it opens the Contracts list,
    /// one of two screens the mod adds beside the stock ones. A kiosk whose display has no
    /// such line keeps its stock menu.
    ///
    /// With Career Manager API active, that mod builds every kiosk's main menu from its own list
    /// and takes the menu's input, so Contracts goes on its list instead, last
    /// (<see cref="CareerManagerApi"/>): the entry is the API's, the screens behind it are the same.
    /// </summary>
    internal static class KioskMenu
    {
        public const string Entry = "Contracts";
        public static IContractDesk Desk;

        private static string message;

        /// <summary>The characters the Contracts list's info line holds: it doesn't wrap, and clips at the screen's edge.</summary>
        public const int InfoLineChars = 37;

        /// <summary>A short notice to show once on the Contracts list's info line, such as what dropped at midnight (at most <see cref="InfoLineChars"/>).</summary>
        public static void Tell(string text) => message = text;

        public static string TakeMessage()
        {
            var m = message;
            message = null;
            return m;
        }

        internal sealed class Extension
        {
            /// <summary>The menu line this mod added; null when Career Manager API holds the menu.</summary>
            public TextMeshPro Line;
            public ContractsScreen List;
            public ContractScreen Detail;
        }

        private static readonly ConditionalWeakTable<CareerManagerMainScreen, Extension> extensions =
            new ConditionalWeakTable<CareerManagerMainScreen, Extension>();

        public static bool TryGet(CareerManagerMainScreen main, out Extension extension) =>
            extensions.TryGetValue(main, out extension);

        internal static void Extend(CareerManagerMainScreen main)
        {
            var kiosk = main.screenSwitcher;
            var t = Traverse.Create(main);
            var selectable = t.Field("selectableText").GetValue<TextMeshPro[]>();
            if (kiosk == null || selectable == null || selectable.Length != 4 || main.fees == null || main.stats == null)
            {
                Main.Warn($"kiosk {Where(kiosk)}: main menu not as expected; Contracts left out");
                return;
            }
            var line = LineBelow(kiosk, main.fees, main.stats, selectable);
            if (line == null)
            {
                Main.Warn($"kiosk {Where(kiosk)}: no free line below Stats; Contracts left out");
                return;
            }
            if (!HasScreens(main))
                return;
            t.Field("selectableText").SetValue(selectable.Concat(new[] { line }).ToArray());
            t.Field("selector").SetValue(new IntIterator(0, 5, isWrappable: true));
            t.Field("activeSlotCount").SetValue(5);
            var screens = new GameObject(Entry);
            screens.transform.SetParent(main.transform.parent, false);
            Attach(main, line, screens.AddComponent<ContractsScreen>());
        }

        /// <summary>
        /// Contracts on a kiosk whose main menu Career Manager API holds: its last entry, which
        /// opens the Contracts list (the API makes the list's object; the detail screen goes
        /// beside it). A Multiplayer client's entry says the host manages the contracts.
        /// </summary>
        internal static void ExtendThrough(CareerManagerMainScreen main, Func<ContractsScreen> addEntry)
        {
            if (main == null || extensions.TryGetValue(main, out _) || !HasScreens(main))
                return;
            var list = addEntry();
            if (list == null)
            {
                Main.Warn($"kiosk {Where(main.screenSwitcher)}: Career Manager API did not take the Contracts entry; Contracts left out");
                return;
            }
            Attach(main, null, list);
        }

        private static bool HasScreens(CareerManagerMainScreen main)
        {
            var kiosk = main.screenSwitcher;
            if (KioskLines.From(kiosk) != null && kiosk.GetComponentInChildren<CareerManagerCustomActionChoiceScreen>(true) != null
                && kiosk.GetComponentInChildren<CareerManagerInfoScreen>(true) != null)
                return true;
            Main.Warn($"kiosk {Where(kiosk)}: screens not as expected; Contracts left out");
            return false;
        }

        /// <summary>The Contracts list and its detail screen (beside it, on its object), for a kiosk's main menu.</summary>
        private static void Attach(CareerManagerMainScreen main, TextMeshPro line, ContractsScreen list)
        {
            var kiosk = main.screenSwitcher;
            var lines = KioskLines.From(kiosk);
            var detail = list.gameObject.AddComponent<ContractScreen>();
            list.Init(kiosk, main, detail, lines);
            detail.Init(kiosk, list, kiosk.GetComponentInChildren<CareerManagerCustomActionChoiceScreen>(true),
                kiosk.GetComponentInChildren<CareerManagerInfoScreen>(true), lines);
            extensions.Add(main, new Extension { Line = line, List = list, Detail = detail });
        }

        /// <summary>
        /// The next line down the main menu's column, after Stats: the stock entries are evenly
        /// spaced, so it lies one step below Stats, in the same column.
        /// </summary>
        private static TextMeshPro LineBelow(DisplayScreenSwitcher kiosk, TextMeshPro first, TextMeshPro last, TextMeshPro[] used)
        {
            Vector3 At(Component c) => kiosk.transform.InverseTransformPoint(c.transform.position);
            var step = (At(last) - At(first)) / 3f;
            if (step.magnitude < 1e-4f)
                return null;
            var target = At(last) + step;
            return kiosk.allTextFields
                .Where(f => f != null && !used.Contains(f) && f.transform.parent == last.transform.parent)
                .Select(f => (Field: f, Off: (At(f) - target).magnitude))
                .Where(c => c.Off < step.magnitude * 0.25f)
                .OrderBy(c => c.Off)
                .Select(c => c.Field)
                .FirstOrDefault();
        }

        internal static void Open(CareerManagerMainScreen main, Extension extension)
        {
            // A player who joined a Multiplayer game reads the host's contracts (DeskNet), once the host has sent them.
            if (Multiplayer.IsClient && !DeskNet.ClientReady(out var why))
            {
                ShowInfo(main.infoScreen, main, Entry, why);
                main.screenSwitcher.SetActiveDisplay(main.infoScreen);
                return;
            }
            main.screenSwitcher.SetActiveDisplay(extension.List);
        }

        private static readonly System.Reflection.MethodInfo SetInfo = AccessTools.Method(typeof(CareerManagerInfoScreen), "SetInfoData",
            new[] { typeof(IDisplayScreen), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(Action) });

        internal static void ShowInfo(CareerManagerInfoScreen screen, IDisplayScreen back, string title, string paragraph) =>
            SetInfo?.Invoke(screen, new object[] { back, title, paragraph, "", "", "", "", null });

        internal static string Where(Component c)
        {
            if (c == null)
                return "?";
            var t = c.transform;
            while (t.parent != null && t.parent.name != "Offices_w3" && t.parent.name != "Offices")
                t = t.parent;
            return t.name;
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), "Awake")]
    internal static class MainScreenAwake
    {
        private static void Postfix(CareerManagerMainScreen __instance)
        {
            try
            {
                // Career Manager API's menu takes Contracts as it starts (CareerManagerApi).
                if (!CareerManagerApi.Hook())
                    KioskMenu.Extend(__instance);
            }
            catch (Exception e)
            {
                Main.Error($"kiosk {KioskMenu.Where(__instance)}: adding Contracts failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), nameof(CareerManagerMainScreen.Activate))]
    internal static class MainScreenActivate
    {
        private static void Postfix(CareerManagerMainScreen __instance)
        {
            if (!KioskMenu.TryGet(__instance, out var x) || x.Line == null)
                return;
            x.Line.text = KioskMenu.Entry;
            x.Line.color = __instance.CurrentSelection == 4 ? __instance.screenSwitcher.HIGHLIGHTED_COLOR : __instance.screenSwitcher.REGULAR_COLOR;
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), nameof(CareerManagerMainScreen.Disable))]
    internal static class MainScreenDisable
    {
        private static void Postfix(CareerManagerMainScreen __instance)
        {
            if (KioskMenu.TryGet(__instance, out var x) && x.Line != null)
                x.Line.text = string.Empty;
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), nameof(CareerManagerMainScreen.HandleInputAction))]
    internal static class MainScreenInput
    {
        private static bool Prefix(CareerManagerMainScreen __instance, InputAction input)
        {
            if (input != InputAction.Confirm || __instance.CurrentSelection != 4 || !KioskMenu.TryGet(__instance, out var x) || x.Line == null)
                return true;
            KioskMenu.Open(__instance, x);
            return false;
        }
    }

    [HarmonyPatch(typeof(CareerManagerMainScreen), nameof(CareerManagerMainScreen.GetCurrentSelection))]
    internal static class MainScreenSelection
    {
        private static bool Prefix(CareerManagerMainScreen __instance, ref string __result)
        {
            if (__instance.CurrentSelection != 4 || !KioskMenu.TryGet(__instance, out var x) || x.Line == null)
                return true;
            __result = KioskMenu.Entry;
            return false;
        }
    }

    /// <summary>
    /// The Multiplayer mod's state, through its public API, if it is loaded.
    ///
    /// Its interfaces are in <c>MPAPI.Interfaces</c> (MultiplayerAPI 1.1.0, with Multiplayer
    /// 0.1.16). Until 0.1.11 they were looked up in <c>MPAPI</c>, where they are not, so no game
    /// was ever taken for a client's and no other player was ever seen: a player who joined ran
    /// the contracts too, and the interchange offices' booklets came out for the host alone
    /// (found 2026-11-10 against the installed mod; Blaze221, Nexus, 2026-11-08: "client cannot
    /// see the jobs booklets at all"). Both places are tried, and a Multiplayer whose API can't
    /// be read is <see cref="Role.Unknown"/>, on which the contracts stay off: a client running
    /// them is worse than a host without them.
    /// </summary>
    internal static class Multiplayer
    {
        internal enum Role
        {
            /// <summary>No Multiplayer mod, or it is not in a session.</summary>
            None,
            /// <summary>The host, or Multiplayer's own single-player session: the contracts run here.</summary>
            Host,
            /// <summary>A player who joined: the host runs the contracts.</summary>
            Client,
            /// <summary>Multiplayer is loaded, and its API can't be read.</summary>
            Unknown,
        }

        /// <summary>An interface of the API, wherever this version keeps it.</summary>
        private static Type Face(string name) =>
            AccessTools.TypeByName("MPAPI.Interfaces." + name) ?? AccessTools.TypeByName("MPAPI." + name);

        private static bool roleSaid;

        public static Role Now
        {
            get
            {
                try
                {
                    var api = AccessTools.TypeByName("MPAPI.MultiplayerAPI");
                    if (api == null)
                        return Role.None;
                    var face = Face("IMultiplayerAPI");
                    var connected = face?.GetProperty("IsConnected");
                    var host = face?.GetProperty("IsHost");
                    if (connected == null || host == null)
                    {
                        if (!roleSaid)
                        {
                            roleSaid = true;
                            Main.Error("Multiplayer: its API has changed (IMultiplayerAPI.IsConnected and IsHost not found), so a host can't be told from a player who joined; contracts stay off with it loaded. Update Railroad Contracts");
                        }
                        return Role.Unknown;
                    }
                    var instance = api.GetProperty("Instance")?.GetValue(null);
                    if (instance == null)
                        return Role.None;
                    if (!(connected.GetValue(instance) as bool? ?? false))
                        return Role.None;
                    return (host.GetValue(instance) as bool? ?? false) ? Role.Host : Role.Client;
                }
                catch (Exception e)
                {
                    if (!roleSaid)
                    {
                        roleSaid = true;
                        Main.Error($"Multiplayer: its API could not be read ({e.Message}); contracts stay off with it loaded");
                    }
                    return Role.Unknown;
                }
            }
        }

        public static bool IsClient => Now == Role.Client;

        private static bool positionsLooked, positionsFailed;
        private static PropertyInfo server, players, position, loaded;

        /// <summary>
        /// Where the players are, on a Multiplayer host: each loaded player's position in the
        /// scene's frame (the API's IPlayer.Position, the host's own among them, as the Multiplayer
        /// mod measures a station office's range). None in single player or on a client.
        /// </summary>
        public static List<Vector3> PlayerPositions()
        {
            var result = new List<Vector3>();
            try
            {
                if (!positionsLooked)
                {
                    positionsLooked = true;
                    server = AccessTools.TypeByName("MPAPI.MultiplayerAPI")?.GetProperty("Server", BindingFlags.Public | BindingFlags.Static);
                    players = Face("IServer")?.GetProperty("Players");
                    var player = Face("IPlayer");
                    position = player?.GetProperty("Position");
                    loaded = player?.GetProperty("IsLoaded");
                    if (server != null && (players == null || position == null))
                        Main.Warn("Multiplayer: its API has changed (IServer.Players or IPlayer.Position not found); booklets at the interchange offices come out for the host only");
                }
                var host = server?.GetValue(null);
                if (host == null || players == null || position == null || !(players.GetValue(host) is IEnumerable all))
                    return result;
                foreach (var p in all)
                    if (p != null && (loaded == null || loaded.GetValue(p) as bool? != false) && position.GetValue(p) is Vector3 at)
                        result.Add(at);
            }
            catch (Exception e)
            {
                if (!positionsFailed)
                {
                    positionsFailed = true;
                    Main.Warn($"Multiplayer: the players' positions could not be read ({e.Message}); booklets at the interchange offices come out for the host only");
                }
            }
            return result;
        }

        public static string Describe()
        {
            var api = AccessTools.TypeByName("MPAPI.MultiplayerAPI");
            var face = Face("IMultiplayerAPI");
            var instance = api?.GetProperty("Instance")?.GetValue(null);
            if (face == null && api != null)
                return "loaded, its API not readable";
            if (instance == null || face == null)
                return api == null ? "not loaded" : "loaded, no instance";
            return string.Join(", ", new[] { "IsConnected", "IsHost", "IsSinglePlayer" }
                .Select(p => $"{p} {face.GetProperty(p)?.GetValue(instance)}"));
        }
    }
}
