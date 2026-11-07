using System;
using Contracts.Mod.Kiosk;
#if DEVTOOLS
using Contracts.Mod.Spike;
#endif
using HarmonyLib;
using UnityModManagerNet;

namespace Contracts.Mod
{
    /// <summary>
    /// The runtime mod: the Contracts menu in every career manager kiosk (design D9), and the
    /// contracts at work in the game (Runtime/): the career's rules, the interchange service,
    /// and the jobs for contract cars.
    /// </summary>
    public static class Main
    {
        internal static UnityModManager.ModEntry Mod;
        private static Harmony harmony;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            try
            {
#if DEVTOOLS
                // Spike S6 (spike.txt in the mod's folder; development builds only) runs on its
                // stand-in desk; otherwise the contracts run (Runtime/ContractRuntime) behind the kiosk.
                var spike = System.IO.File.Exists(System.IO.Path.Combine(modEntry.Path, "spike.txt"));
                KioskMenu.Desk = spike ? new SpikeDesk() : (IContractDesk)new Runtime.RuntimeDesk();
#else
                const bool spike = false;
                KioskMenu.Desk = new Runtime.RuntimeDesk();
#endif
                harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(typeof(Main).Assembly);
#if DEVTOOLS
                KioskSpike.Install(modEntry.Path);
#endif
                if (!spike)
                {
                    WorldStreamingInit.LoadingFinished += Runtime.ContractRuntime.OnWorldLoaded;
                    Runtime.CareerRules.GuardPassengerJobs(harmony);
                    Runtime.Passengers.Install(harmony);
                    Runtime.InterchangeStations.Install();
                    Runtime.InterchangeStations.GuardPersistentJobs(harmony);
                    Runtime.InterchangeBooklets.Install();
                    Runtime.InterchangeBooklets.GuardJobRange(harmony);
                }
#if DEVTOOLS
                Runtime.Probe.Install(modEntry.Path);
                Runtime.Survey.Install(modEntry.Path);
#endif
                return true;
            }
            catch (Exception e)
            {
                Error($"failed to load: {e}");
                return false;
            }
        }

        internal static void Log(string text) => Mod?.Logger.Log(text);
        internal static void Warn(string text) => Mod?.Logger.Warning(text);
        internal static void Error(string text) => Mod?.Logger.Error(text);
    }
}
