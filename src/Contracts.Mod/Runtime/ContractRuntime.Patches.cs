using System;
using DV.Logic.Job;
using HarmonyLib;

namespace Contracts.Mod.Runtime
{
    internal sealed partial class ContractRuntime
    {
        // ------------------------------------------------------------ patches

        /// <summary>
        /// A save gathers the cars and jobs before the mod's own data (SaveGameManager calls
        /// OnInternalDataUpdate last): the service under way is finished first, so the save holds
        /// all of it.
        /// </summary>
        [HarmonyPatch(typeof(SaveGameManager), "UpdateInternalData")]
        private static class Saving
        {
            private static void Prefix()
            {
                try
                {
                    Instance?.FinishService();
                }
                catch (Exception e)
                {
                    Main.Error($"contracts: finishing the service before a save failed: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(Track), nameof(Track.OverrideTrackID))]
        private static class TrackIdOverridden
        {
            private static void Postfix() => ClearStationTrackCache();
        }

        [HarmonyPatch(typeof(JobChainController), "OnLastJobInChainCompleted")]
        private static class ChainCompleted
        {
            // First: Persistent Jobs' own prefix then finds the contract cars gone and skips.
            [HarmonyPriority(Priority.First)]
            private static void Prefix(JobChainController __instance, Job lastJobInChain)
            {
                try
                {
                    if (Instance != null && Instance.setUp && __instance?.carsForJobChain != null)
                        Instance.OnChainCompleted(__instance, lastJobInChain);
                }
                catch (Exception e)
                {
                    Main.Error($"contracts: writing the next job after {lastJobInChain?.ID} failed: {e}");
                }
            }
        }
    }
}
