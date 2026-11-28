using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Shop;
using DV.ThingTypes;
using DV.LocoRestoration;
using DV.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The game's restoration locomotives (tracker #45; owner, 2026-11-23: "Yes, do fix number 45.
    /// Is there a way to make it not purchased until after it's restored or something?"). A
    /// career begins with a wreck of each (LocoRestorationController), spawned as a unique,
    /// player-spawned car like the player's own, so the mod took them for the player's: it gave
    /// their licences at the first load, listed them at the kiosk to sell, and kept them from Clear.
    ///
    /// A wreck is nobody's until the game hands it over: once serviced (S9_LocoServiced) it
    /// becomes its garage's demonstrator (HomeGarageReference, its garage unlocked), which the
    /// kiosk never sells and the game brings back if it is lost. Until then the game keeps it
    /// itself: its cab locked until the player holds its licence and the restoration licence,
    /// and it can't be rerailed, coupled, serviced or deleted (Clear) before its step.
    /// </summary>
    internal static class RestorationWrecks
    {
        private static AccessTools.FieldRef<LocoRestorationController, TrainCar> locoField, secondCarField;
        private static bool fieldsTried, warned;
#if DEVTOOLS
        /// <summary>The new-career check's reproduction of 0.2.x (its "wreck-bug"): no wreck is told apart, and nothing is taken back.</summary>
        internal static bool SimulateOldBuild;
#endif

        /// <summary>The cars of every restoration not yet serviced: the wrecks, now.</summary>
        internal static HashSet<TrainCar> Cars()
        {
            var wrecks = new HashSet<TrainCar>();
#if DEVTOOLS
            if (SimulateOldBuild)
                return wrecks;
#endif
            try
            {
                Fields();
                JObject saved = null;
                foreach (var r in LocoRestorationController.allLocoRestorationControllers)
                {
                    if (r == null || r.State >= LocoRestorationController.RestorationState.S9_LocoServiced)
                        continue;
                    if (locoField != null && secondCarField != null)
                    {
                        Add(wrecks, locoField(r));
                        Add(wrecks, secondCarField(r));
                        continue;
                    }
                    // Without the fields (a game update renamed them): the cars the restoration saves by guid.
                    saved = saved ?? LocoRestorationController.GetSaveData();
                    if (saved?[r.SaveID] is JObject data)
                        foreach (var key in new[] { "loco", "secondCar" })
                            if (data[key] is JValue guid && guid.Type == JTokenType.String)
                                Add(wrecks, SingletonBehaviour<TrainCarRegistry>.Instance?.GetTrainCarByCarGuid((string)guid));
                }
            }
            catch (Exception e)
            {
                if (!warned)
                {
                    warned = true;
                    Main.Warn($"restoration: the game's restoration wrecks could not be read; the kiosk may list them as yours: {e.Message}");
                }
            }
            return wrecks;
        }

        internal static bool IsWreck(TrainCar car) => car != null && Cars().Contains(car);

        /// <summary>
        /// The licences 0.2.x gave free with the wrecks, taken back once in a career played
        /// before this build (Core WreckLicences: only those nothing but the bug explains); what
        /// was done, for the log.
        /// </summary>
        internal static List<string> TakeBackLicences()
        {
            var notes = new List<string>();
            var licences = SingletonBehaviour<LicenseManager>.Instance;
            if (licences == null)
                return notes;
            var restorations = LocoRestorationController.allLocoRestorationControllers.Where(r => r != null && r.locoLivery?.requiredLicense != null).ToList();
            var byId = restorations.GroupBy(r => r.locoLivery.requiredLicense.id).ToDictionary(g => g.Key, g => g.First().locoLivery.requiredLicense, StringComparer.Ordinal);
            var held = byId.Values.Where(licences.IsGeneralLicenseAcquired).Select(l => l.id).ToList();
            var wrecks = restorations.Select(r => new Wreck(r.locoLivery.requiredLicense.id, r.State <= LocoRestorationController.RestorationState.S1_UnlockedRestorationLicense)).ToList();
            var wreckCars = Cars();
            var owned = (SingletonBehaviour<CarSpawner>.Instance?.AllCars ?? new List<TrainCar>())
                .Where(c => c != null && c.uniqueCar && !wreckCars.Contains(c) && c.carLivery?.requiredLicense != null)
                .Select(c => c.carLivery.requiredLicense.id).Distinct().ToList();
            var starter = StarterChoice.Cars(StarterChoice.Current())?.FirstOrDefault()?.requiredLicense?.id;
            var startedWith = LicenseManager.TutorialGeneralLicenses.Where(l => l != null).Select(l => l.id).Concat(new[] { starter }).Where(id => id != null).Distinct().ToList();
            var takeBack = WreckLicences.ToTakeBack(held, wrecks, owned, startedWith);
            notes.Add($"licenses: checked the wrecks' licences held ({(held.Count == 0 ? "none" : string.Join(", ", held))}) once: " +
                      $"owned locomotives need {(owned.Count == 0 ? "none" : string.Join(", ", owned))}; a career starts with {string.Join(", ", startedWith)}; " +
                      $"wrecks being restored {string.Join(", ", wrecks.Where(w => !w.Untouched).Select(w => w.Licence).DefaultIfEmpty("none"))}");
            foreach (var id in takeBack)
            {
                licences.RemoveGeneralLicense(byId[id]);
                var booklets = RemoveBooklets(byId[id]);
                notes.Add($"licenses: {id} taken back: given free with the game's {id} restoration wreck by an earlier build, with no {id} of yours and no restoration begun (#45); " +
                          $"its booklet{(booklets == 1 ? "" : "s")} removed: {booklets}");
            }
            if (takeBack.Count == 0)
                notes.Add("licenses: no wreck licence to take back");
            return notes;
        }

        /// <summary>
        /// A taken-back licence's booklets, wherever the player keeps them: the game prints one for
        /// every licence held at each load (into Lost and Found), so the bug's licences have them.
        /// Removed as the game removes an item: from the inventory's slot, else destroyed.
        /// </summary>
        private static int RemoveBooklets(GeneralLicenseType_v2 licence)
        {
            var prefab = licence.licensePrefab;
            var name = prefab == null ? null : prefab.GetComponent<InventoryItemSpec>()?.ItemPrefabName ?? prefab.name;
            if (name == null)
                return 0;
            var inventory = SingletonBehaviour<DV.InventorySystem.Inventory>.Instance;
            var booklets = (SingletonBehaviour<StorageController>.Instance?.allStorages ?? new List<StorageBase>())
                .Where(s => s != null).SelectMany(s => s.GetStorageItemList()).Where(i => i != null && i.InventorySpecs != null && i.InventorySpecs.ItemPrefabName == name)
                .Select(i => i.gameObject).Distinct().ToList();
            foreach (var go in booklets)
            {
                if (inventory != null && inventory.IndexOf(go) >= 0)
                    inventory.DestroyItem(go);
                else
                    UnityEngine.Object.Destroy(go);
            }
            return booklets.Count;
        }

        private static void Add(HashSet<TrainCar> set, TrainCar car)
        {
            if (car != null)
                set.Add(car);
        }

        private static void Fields()
        {
            if (fieldsTried)
                return;
            fieldsTried = true;
            try
            {
                locoField = AccessTools.FieldRefAccess<LocoRestorationController, TrainCar>("loco");
                secondCarField = AccessTools.FieldRefAccess<LocoRestorationController, TrainCar>("secondCar");
            }
            catch (Exception e)
            {
                locoField = secondCarField = null;
                Main.Warn($"restoration: the restoration's cars are read from its save instead: {e.Message}");
            }
        }
    }
}
