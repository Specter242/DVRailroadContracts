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
            if (Multiplayer.IsClient)
            {
                ShowInfo(main.infoScreen, main, Entry, "Contracts are managed by the host.");
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

    /// <summary>The Multiplayer mod's state, through its public API, if it is loaded.</summary>
    internal static class Multiplayer
    {
        public static bool IsClient
        {
            get
            {
                try
                {
                    var api = AccessTools.TypeByName("MPAPI.MultiplayerAPI");
                    var face = AccessTools.TypeByName("MPAPI.IMultiplayerAPI");
                    var instance = api?.GetProperty("Instance")?.GetValue(null);
                    if (instance == null || face == null)
                        return false;
                    var connected = face.GetProperty("IsConnected")?.GetValue(instance) as bool? ?? false;
                    var host = face.GetProperty("IsHost")?.GetValue(instance) as bool? ?? false;
                    return connected && !host;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

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
                    players = AccessTools.TypeByName("MPAPI.IServer")?.GetProperty("Players");
                    var player = AccessTools.TypeByName("MPAPI.IPlayer");
                    position = player?.GetProperty("Position");
                    loaded = player?.GetProperty("IsLoaded");
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
            var face = AccessTools.TypeByName("MPAPI.IMultiplayerAPI");
            var instance = api?.GetProperty("Instance")?.GetValue(null);
            if (instance == null || face == null)
                return api == null ? "not loaded" : "loaded, no instance";
            return string.Join(", ", new[] { "IsConnected", "IsHost", "IsSinglePlayer" }
                .Select(p => $"{p} {face.GetProperty(p)?.GetValue(instance)}"));
        }
    }
}
