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
                    Runtime.CareerRules.GuardPassengerJobs(harmony);
                    Runtime.CareerRules.GuardMultiplayerLocomotives(harmony);
                    Runtime.Passengers.Install(harmony);
                    Runtime.InterchangeStations.Install();
                    Runtime.InterchangeStations.GuardPersistentJobs(harmony);
                    Runtime.InterchangeBooklets.Install();
                    Runtime.InterchangeBooklets.GuardJobRange(harmony);
                    // Last: the runtime starts only in a mod that loaded whole.
                    WorldStreamingInit.LoadingFinished += Runtime.ContractRuntime.OnWorldLoaded;
                    StartMultiplayerPart(modEntry);
                }
#if DEVTOOLS
                Runtime.Probe.Install(modEntry.Path);
                Runtime.Survey.Install(modEntry.Path);
#endif
                return true;
            }
            catch (Exception e)
            {
                // Half a set of patches is worse than none: stations kept from spawning with no
                // contracts to fill them. The game goes on as without the mod.
                Error($"failed to load; its patches are taken off, and the game runs without it: {e}");
                try
                {
                    harmony?.UnpatchAll(modEntry.Info.Id);
                }
                catch (Exception undo)
                {
                    Error($"taking the patches off failed: {undo.Message}");
                }
                return false;
            }
        }

        private const string MultiplayerPart = "Contracts.Multiplayer.dll";

        /// <summary>
        /// Loads the mod's Multiplayer part when the Multiplayer mod runs: the kiosk for players
        /// who joined (Kiosk.DeskNet). It is compiled against Multiplayer's API, so it is a file
        /// of its own, loaded only then; without Multiplayer the mod never touches it. Never
        /// throws: without it the mod works as a host's or alone, and a player who joined is told
        /// at the kiosk why it shows no contracts.
        /// </summary>
        private static void StartMultiplayerPart(UnityModManager.ModEntry mod)
        {
            try
            {
                var multiplayer = UnityModManager.FindMod("Multiplayer");
                if (multiplayer == null || !multiplayer.Active)
                {
                    Kiosk.DeskNet.AddOnProblem = "Multiplayer is not running";
                    return;
                }
                var path = System.IO.Path.Combine(mod.Path, MultiplayerPart);
                if (!System.IO.File.Exists(path))
                {
                    Kiosk.DeskNet.AddOnProblem = $"{MultiplayerPart} is missing from the mod's folder";
                    Warn($"Multiplayer is running but {MultiplayerPart} is missing: players who join get no Contracts menu at their kiosks");
                    return;
                }
                var start = System.Reflection.Assembly.LoadFrom(path).GetType("Contracts.Multiplayer.Integration")?.GetMethod("Start");
                if (start == null)
                {
                    Kiosk.DeskNet.AddOnProblem = $"{MultiplayerPart} is not this build's";
                    Error($"{MultiplayerPart} has no Integration.Start: players who join get no Contracts menu");
                    return;
                }
                start.Invoke(null, new object[] { mod });
                Kiosk.DeskNet.AddOnProblem = null;
            }
            catch (Exception e)
            {
                Kiosk.DeskNet.AddOnProblem = "its multiplayer part failed to start";
                Error($"the Multiplayer part failed to start; players who join get no Contracts menu: {e}");
            }
        }

        /// <summary>The mod's version, for the save's stamp.</summary>
        internal static string Version => Mod?.Info?.Version ?? "?";

        internal static void Log(string text) => Mod?.Logger.Log(text);
        internal static void Warn(string text) => Mod?.Logger.Warning(text);
        internal static void Error(string text) => Mod?.Logger.Error(text);
    }
}
