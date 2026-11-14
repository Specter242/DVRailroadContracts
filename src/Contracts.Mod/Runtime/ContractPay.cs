using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Contracts.Core.Industries;
using Contracts.Core.Net;
using DV.InventorySystem;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Utils;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// Money on top of stock wages (design D7; Contracts.Core ContractTerms):
    ///
    /// - **The quick-delivery bonus** goes into a contract job's pay. When the job is done, each
    ///   of its contract cars adds the bonus of its contract's level on its share of the job's
    ///   base wage, by the age of its bill; a passenger train, on its whole base wage by how long
    ///   it has been due. The validator prints it with the job's time bonus.
    /// - **A penalty** comes out of the wallet. The game keeps no negative cash, so what the
    ///   wallet can't cover is owed (saved), and taken as cash comes in.
    /// - **A joined player's turn-in** in a Multiplayer session is paid straight into the
    ///   railroad's cash by the host's game, not in banknotes (Contracts.Core GuestPay).
    /// </summary>
    internal static class ContractPay
    {
        private sealed class Box
        {
            public double Amount;
        }

        /// <summary>The bonus each contract job just done earned, for the validator's payment.</summary>
        private static readonly ConditionalWeakTable<Job, Box> bonuses = new ConditionalWeakTable<Job, Box>();

        public static double Money => SingletonBehaviour<Inventory>.Instance?.PlayerMoney ?? 0.0;

        /// <summary>Charges a penalty: from the wallet now, and the rest owed.</summary>
        public static void Charge(ContractState state, double amount, string memo)
        {
            if (state == null || amount <= 0.0)
                return;
            state.Owed = Math.Round(state.Owed + amount);
            var taken = Collect(state);
            Main.Log($"contracts: penalty ${amount:N0} for {memo}; ${taken:N0} paid from the wallet, ${state.Owed:N0} owed");
        }

        /// <summary>Takes what is owed from the wallet, as far as it goes; returns what it took.</summary>
        public static double Collect(ContractState state)
        {
            var inventory = SingletonBehaviour<Inventory>.Instance;
            if (state == null || state.Owed < 1.0 || inventory == null)
                return 0.0;
            var take = Math.Floor(Math.Min(state.Owed, inventory.PlayerMoney));
            if (take < 1.0 || !inventory.RemoveMoney(take))
                return 0.0;
            state.Owed = Math.Max(0.0, Math.Round(state.Owed - take));
            if (state.Owed < 1.0)
                Main.Log("contracts: penalties paid off");
            return take;
        }

        /// <summary>The bonus a contract job earns if done now: its cars' (or its train's) share, by their bills' ages.</summary>
        private static double BonusFor(ContractRuntime rt, Job job, out int cars)
        {
            cars = 0;
            var now = ContractRuntime.Clock;
            if (rt == null || job == null || now == null)
                return 0.0;
            var wage = (double)job.GetBasePaymentForTheJob();
            if (wage <= 0.0)
                return 0.0;
            var train = PassengerRuns.Held(rt.State).Select(c => (Contract: c, Run: c.Running.FirstOrDefault(r => r.Job == job.ID)))
                .FirstOrDefault(x => x.Run != null);
            if (train.Run != null)
            {
                cars = 1;
                return ContractTerms.Bonus(wage, train.Contract.Level, now.Value - train.Run.Opened);
            }
            var jobCars = ContractRuntime.JobCars(job).Where(c => c != null).ToList();
            if (jobCars.Count == 0)
                return 0.0;
            var share = wage / jobCars.Count;
            var total = 0.0;
            foreach (var car in jobCars)
            {
                var w = rt.State.Of(car.carGuid);
                var contract = w?.Opened == null ? null : rt.State.Contracts.FirstOrDefault(c => c.Id == w.Contract);
                if (contract == null)
                    continue;
                var bonus = ContractTerms.Bonus(share, contract.Level, now.Value - w.Opened.Value);
                if (bonus > 0.0)
                {
                    total += bonus;
                    cars++;
                }
            }
            return total;
        }

        /// <summary>
        /// A job is done: a contract job's bonus is reckoned now, while its bills are still open
        /// (the chain's completion closes them).
        /// </summary>
        [HarmonyPatch(typeof(JobsManager), nameof(JobsManager.CompleteTheJob))]
        private static class Completing
        {
            private static void Prefix(Job job)
            {
                try
                {
                    var rt = ContractRuntime.Instance;
                    if (!ContractRuntime.Active || job == null || !rt.IsContractJob(job))
                        return;
                    var bonus = BonusFor(rt, job, out var cars);
                    if (bonus < 1.0)
                        return;
                    bonuses.Remove(job);
                    bonuses.Add(job, new Box { Amount = bonus });
                    Main.Log($"contracts: job {job.ID} earns a quick-delivery bonus of ${bonus:N0} ({cars} car(s))");
                }
                catch (Exception e)
                {
                    Main.Error($"contracts: the quick-delivery bonus for {job?.ID} failed (none paid): {e.Message}");
                }
            }
        }

#if DEVTOOLS
        /// <summary>The probe's: payments are taken as a joined player's turn-ins, on a host whose player is elsewhere.</summary>
        internal static bool AsGuestTurnIn;
#endif

        /// <summary>
        /// The validator prints a done job's pay as banknotes. On the host of a Multiplayer
        /// session a joined player's turn-in runs here too, in the host's copy of the office
        /// where that player stands, and the banknotes come out where they can't take them. Such
        /// a job's wage goes straight into the railroad's cash, which Multiplayer shows to all
        /// (GuestPay: a turn-in at an office the host's own player is not in). Anything else,
        /// and any failure here, leaves the game to pay as it does.
        /// </summary>
        [HarmonyPatch(typeof(MoneyPrinterJobValidator), nameof(MoneyPrinterJobValidator.PrintPayment))]
        private static class GuestTurnIn
        {
            private static bool Prefix(MoneyPrinterJobValidator __instance, Job job)
            {
                var paid = false;
                try
                {
                    var hosts = Kiosk.Multiplayer.HostsOthers;
                    var me = PlayerManager.PlayerTransform;
                    var away = me != null && __instance != null
                        ? (double?)Vector3.Distance(me.position, __instance.transform.position) : null;
#if DEVTOOLS
                    if (AsGuestTurnIn)
                        (hosts, away) = (true, null);
#endif
                    var inventory = SingletonBehaviour<Inventory>.Instance;
                    if (job == null || inventory == null || !GuestPay.Direct(hosts, away))
                        return true;
                    var wage = (double)job.GetWageForTheJob();
                    if (wage > 0.0)
                        inventory.AddMoney(wage);
                    paid = true;
                    Main.Log($"contracts: job {job.ID} was turned in by a player who joined ({(away.HasValue ? $"the host is {away.Value:N0} m away" : "the host's place not known")}): " +
                             $"its ${wage:N0} went straight into the railroad's cash");
                    return false;
                }
                catch (Exception e)
                {
                    Main.Error($"contracts: paying {job?.ID} straight into the cash failed{(paid ? "" : " (the game pays it in banknotes)")}: {e.Message}");
                    return !paid;
                }
            }
        }

        /// <summary>The validator pays a done job its time bonus; a contract job's quick-delivery bonus comes with it.</summary>
        [HarmonyPatch(typeof(Job), nameof(Job.GetBonusPaymentForTheJob))]
        private static class Paying
        {
            private static void Postfix(Job __instance, ref float __result)
            {
                if (__instance != null && __instance.State == JobState.Completed && bonuses.TryGetValue(__instance, out var box))
                    __result += (float)box.Amount;
            }
        }
    }
}
