using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// Persistent Jobs' "Suspend far away cars" (its FarCarOpt, on by default) takes the cars
    /// standing on a station's own tracks out of the world while the player is far from it, to
    /// bring them back when the player returns. Contract cars and the player's coaches are the
    /// railroad's working fleet, left where the player leaves them: they are never suspended (a
    /// Nexus bug report, 2026-11-04, "Cars Despawning in Stations": cars left on a station track
    /// were gone after a drive to another station; owner, 2026-11-05: "Looks good. Continue").
    /// Its three ways in are guarded: the check of a track's trainsets, the list it suspends, and
    /// the one car. Patched by name: the types are another mod's, and without it nothing is patched.
    /// </summary>
    internal static class FarCarGuard
    {
        /// <summary>Off only for the probe, to check a contract car Persistent Jobs suspends keeps its bill.</summary>
        internal static bool Guarding = true;

        private static bool Protected(TrainCar car) => car != null && car.logicCar != null && CareerRules.Protected(car.logicCar);

        private static MethodBase Method(string name) => PersistentJobs.FarCars == null ? null : AccessTools.Method(PersistentJobs.FarCars, name);

        /// <summary>A trainset on a far station's track: not suspended if it holds a contract car or a coach of the player's.</summary>
        [HarmonyPatch]
        private static class TrainsetOnTrack
        {
            private static bool Prepare() => Method("IsTrainsetValid") != null;
            private static MethodBase TargetMethod() => Method("IsTrainsetValid");

            private static bool Prefix(IEnumerable<TrainCar> __0, ref bool __result)
            {
                try
                {
                    if (!Guarding || __0 == null || !__0.Any(Protected))
                        return true;
                    __result = false;
                    return false;
                }
                catch (Exception e)
                {
                    Main.Error($"far cars: checking a trainset failed: {e.Message}");
                    return true;
                }
            }
        }

        /// <summary>The cars about to be suspended: those of a trainset holding a contract car or a coach of the player's are left out.</summary>
        [HarmonyPatch]
        private static class CarsToSuspend
        {
            private static bool Prepare() => Method("SuspendCars") != null;
            private static MethodBase TargetMethod() => Method("SuspendCars");

            private static void Prefix(ref List<TrainCar> __0)
            {
                try
                {
                    if (!Guarding || __0 == null || !__0.Any(Protected))
                        return;
                    var kept = new HashSet<TrainCar>(__0.Where(Protected).SelectMany(c => c.trainset?.cars ?? new List<TrainCar> { c }));
                    __0 = __0.Where(c => !kept.Contains(c)).ToList();
                }
                catch (Exception e)
                {
                    Main.Error($"far cars: sorting the cars to suspend failed: {e.Message}");
                }
            }
        }

        /// <summary>One car (its console command): not a contract car or a coach of the player's.</summary>
        [HarmonyPatch]
        private static class OneCar
        {
            private static bool Prepare() => Method("SuspendCar") != null;
            private static MethodBase TargetMethod() => Method("SuspendCar");

            private static bool Prefix(TrainCar __0, ref bool __result)
            {
                if (!Guarding || !Protected(__0))
                    return true;
                __result = false;
                return false;
            }
        }
    }
}
