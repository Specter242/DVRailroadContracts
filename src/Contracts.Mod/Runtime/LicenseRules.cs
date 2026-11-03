using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Industries;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using DV.Utils;
using HarmonyLib;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// Licenses under the contracts (owner, 2026-11-02, design D19 and D20: "the player will
    /// have mandatory waybills that the player doesn't have licenses for also with so many one
    /// and two car jobs locked behind concurrent orders license. it doesn't make sense"):
    ///
    /// - **The contract is gated, not its bills.** An industry's contract can be taken only with
    ///   the licenses its cargo and their cars ask for (Economy.Licenses: Hazmat, Military,
    ///   Fragile), so a contract's jobs never need one the player lacks.
    /// - **A contract held without them** (taken before the gate) bills only the cargo the player
    ///   may carry (<see cref="Carrying"/>, the planner's filter); its bills for the rest are
    ///   withdrawn (ContractRuntime.WithdrawUncarried, owner, 2026-11-03: fix the save).
    /// - **Every career has the job types.** Every contract runs empty hauls (Logistical Haul),
    ///   shunting loads and unloads (Shunting) and transports (Freight Haul); a career the
    ///   contracts run in is given all three. Contract jobs keep their stock requirements.
    /// - **Contract jobs don't count toward the concurrent-job limit** at the job validator. A
    ///   contract job is never refused for it; any other job counts only the other jobs held.
    /// - Train length still caps each contract job's cars (ContractRuntime.MaxCarsPerJob).
    /// - **Owned vehicles are licensed** (<see cref="LicenseOwned"/>): a bought locomotive comes
    ///   with its license.
    /// </summary>
    internal static class LicenseRules
    {
        private static readonly JobLicenses[] JobTypes = { JobLicenses.FreightHaul, JobLicenses.Shunting, JobLicenses.LogisticalHaul };

        /// <summary>The job licenses the player holds (ids).</summary>
        internal static HashSet<string> HeldIds()
        {
            var licenses = SingletonBehaviour<LicenseManager>.Instance;
            return new HashSet<string>(licenses?.GetAcquiredJobLicenses()?.Where(l => l != null).Select(l => l.id) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
        }

        /// <summary>The licenses of an industry's traffic (or a city's passenger service) the player doesn't hold (ids).</summary>
        public static List<string> Missing(string industry)
        {
            if (PassengerService.CityOf(industry) != null)
                return Passengers.Missing(HeldIds());
            var needed = ContractRuntime.Instance?.Economy?.Licenses;
            if (needed == null || industry == null || !needed.TryGetValue(industry, out var ids))
                return new List<string>();
            var held = HeldIds();
            return ids.Where(id => !held.Contains(id)).ToList();
        }

        /// <summary>Whether the player may carry each cargo now: holds its licenses and its cars' (a snapshot of the licenses held).</summary>
        public static Func<string, bool> Carrying()
        {
            var needed = ContractRuntime.Instance?.Economy?.CargoLicenses;
            var held = HeldIds();
            return cargo => cargo == null || needed == null || !needed.TryGetValue(cargo, out var ids) || ids.All(held.Contains);
        }

        /// <summary>Null if the player holds every license an industry's contract needs; otherwise what it needs, for the kiosk's info line.</summary>
        public static string Blocker(string industry) => ContractLicenses.Blocker(Missing(industry));

        /// <summary>Gives the career the job-type licenses every contract's work uses, those it lacks.</summary>
        public static void GiveJobTypes(List<string> notes)
        {
            var licenses = SingletonBehaviour<LicenseManager>.Instance;
            if (licenses == null)
                return;
            foreach (var type in JobTypes)
            {
                var license = type.ToV2();
                if (license == null || licenses.IsJobLicenseAcquired(license))
                    continue;
                licenses.AcquireJobLicense(license);
                notes.Add($"licenses: {ContractLicenses.Name(license.id)} acquired (every contract runs its jobs)");
            }
        }

        /// <summary>
        /// Owning a vehicle licenses it (owner, 2026-11-03: "it seems a little excessive to require
        /// a license and a purchase"). The game locks the cab of a locomotive without its license,
        /// so a bought vehicle comes with it, and so does every one the player owns when a save
        /// loads (bought before this rule). Locomotives come only from the shop (D17).
        /// </summary>
        public static void LicenseOwned(IEnumerable<TrainCar> cars, List<string> notes)
        {
            var licenses = SingletonBehaviour<LicenseManager>.Instance;
            if (licenses == null || cars == null)
                return;
            foreach (var car in cars.Where(c => c != null && c.uniqueCar && c.carLivery?.requiredLicense != null))
            {
                var license = car.carLivery.requiredLicense;
                if (licenses.IsGeneralLicenseAcquired(license))
                    continue;
                licenses.AcquireGeneralLicense(license);
                var note = $"licenses: {license.id} acquired with the owned {car.carLivery.id} {car.ID}";
                if (notes != null)
                    notes.Add(note);
                else
                    Main.Log(note);
            }
        }

        // ------------------------------------------------------------ the concurrent-job limit

        /// <summary>The job whose overview the validator is processing, while it does.</summary>
        private static Job validating;

        [HarmonyPatch(typeof(JobValidator), nameof(JobValidator.ProcessJobOverview))]
        private static class Validating
        {
            private static void Prefix(JobOverview jobOverview) => validating = jobOverview != null ? jobOverview.job : null;

            private static void Finalizer() => validating = null;
        }

        [HarmonyPatch(typeof(LicenseManager), nameof(LicenseManager.GetNumberOfAllowedConcurrentJobs))]
        private static class ContractJobsDontCount
        {
            private static void Postfix(ref int __result)
            {
                try
                {
                    var job = validating;
                    var rt = ContractRuntime.Instance;
                    if (job == null || rt == null || !ContractRuntime.Active)
                        return;
                    if (rt.IsContractJob(job))
                    {
                        __result = int.MaxValue;
                        return;
                    }
                    var held = SingletonBehaviour<JobsManager>.Instance?.currentJobs?.Count(rt.IsContractJob) ?? 0;
                    __result = __result > int.MaxValue - held ? int.MaxValue : __result + held;
                }
                catch (Exception e)
                {
                    Main.Error($"licenses: the concurrent-job limit for {validating?.ID} failed (stock limit applies): {e.Message}");
                }
            }
        }
    }
}
