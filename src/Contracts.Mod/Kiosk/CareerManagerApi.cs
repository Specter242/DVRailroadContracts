using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.ServicePenalty.UI;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// Career Manager API (wwwDayDream, Nexus 951), reached by reflection when it is active. It
    /// rebuilds every kiosk's main menu from its own list of entries as the kiosk starts, and
    /// takes the menu's input, so a line added to the stock menu is dropped (a Discord report,
    /// 2026-11-03: Contracts missing at the Steel Mill with it installed). With it active,
    /// Contracts is one of its entries instead, added when it starts a kiosk
    /// (<c>CareerManagerAwake</c>, <c>TryAddToMainScreen</c>): last, after Stats, as without it.
    /// </summary>
    internal static class CareerManagerApi
    {
        private const string ModId = "CareerManagerAPI";
        private static bool looked, hooked;
        private static Type trackerType, canConfirmType;
        private static MethodInfo tryAdd;
        private static EventInfo awake;

        /// <summary>Whether Career Manager API is active, with the members this mod uses.</summary>
        public static bool Active
        {
            get
            {
                if (!looked)
                {
                    looked = true;
                    var api = AccessTools.TypeByName("CareerManagerAPI.CareerManagerAPI");
                    trackerType = AccessTools.TypeByName("CareerManagerAPI.CareerManagerScreenTracker");
                    canConfirmType = api?.GetNestedType("OnCanConfirm");
                    awake = api?.GetEvent("CareerManagerAwake");
                    tryAdd = trackerType?.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "TryAddToMainScreen" && m.IsGenericMethodDefinition && m.GetParameters().Length == 4);
                    if (api != null && (trackerType == null || canConfirmType == null || awake == null || tryAdd == null))
                        Main.Warn("Career Manager API: its tracker, event or TryAddToMainScreen not found; Contracts adds its own menu line");
                }
                var mod = UnityModManagerNet.UnityModManager.FindMod(ModId);
                return mod != null && mod.Active && awake != null && tryAdd != null && canConfirmType != null;
            }
        }

        /// <summary>
        /// With Career Manager API active, listens for its kiosks (once) and returns true: the
        /// kiosk's Contracts entry is added as the API starts it. Otherwise false.
        /// </summary>
        public static bool Hook()
        {
            if (!Active)
                return false;
            if (hooked)
                return true;
            try
            {
                // Its delegate takes its tracker type; a handler taking MonoBehaviour binds to it.
                var handler = Delegate.CreateDelegate(awake.EventHandlerType,
                    typeof(CareerManagerApi).GetMethod(nameof(OnKiosk), BindingFlags.NonPublic | BindingFlags.Static));
                awake.AddEventHandler(null, handler);
                hooked = true;
                Main.Log("Career Manager API: Contracts goes on its kiosk menus");
                return true;
            }
            catch (Exception e)
            {
                Main.Error($"Career Manager API: listening for its kiosks failed; Contracts adds its own menu line: {e.Message}");
                awake = null;
                return false;
            }
        }

        private static void OnKiosk(MonoBehaviour tracker, string location, StationController station, TrainCar trainCar)
        {
            try
            {
                var main = tracker == null ? null : tracker.GetComponent<CareerManagerMainScreen>();
                KioskMenu.ExtendThrough(main, () => AddEntry(tracker, main));
            }
            catch (Exception e)
            {
                Main.Error($"kiosk {location}: adding Contracts through Career Manager API failed: {e}");
            }
        }

        /// <summary>The Contracts entry on the API's menu; the list screen the API made for it, or null.</summary>
        private static ContractsScreen AddEntry(MonoBehaviour tracker, CareerManagerMainScreen main)
        {
            // The API makes the entry's screen on a new object under the stock screens' parent.
            var parent = main.feesScreen.transform.parent;
            var before = new HashSet<ContractsScreen>(parent.GetComponentsInChildren<ContractsScreen>(true));
            var canConfirm = Delegate.CreateDelegate(canConfirmType,
                typeof(CareerManagerApi).GetMethod(nameof(CanOpen), BindingFlags.NonPublic | BindingFlags.Static));
            var added = (bool)tryAdd.MakeGenericMethod(typeof(ContractsScreen))
                .Invoke(tracker, new object[] { KioskMenu.Entry, canConfirm, null, null });
            return added ? parent.GetComponentsInChildren<ContractsScreen>(true).FirstOrDefault(s => !before.Contains(s)) : null;
        }

        /// <summary>The API's check before it opens an entry: a Multiplayer client's contracts are the host's.</summary>
        private static bool CanOpen(out string why)
        {
            why = Multiplayer.IsClient ? "Contracts are managed by the host." : string.Empty;
            return !Multiplayer.IsClient;
        }
    }
}
