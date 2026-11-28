using System;
using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Shop;
using DV.Items;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// A steam start's lighter and shovel (owner, 2026-11-21: "I want a steam start too"; the JK3
    /// too, 2026-11-23). The game's Basic starting items (its default) have neither, so a new
    /// career on an S060 or a JK3 could not light or fire it; its Expanded and Engineer ones have
    /// both. Added to the new career's starting items, as the game's own are given, and only once:
    /// later loads leave them as any item the player owns.
    /// </summary>
    [HarmonyPatch(typeof(StartingItemsController), "GetStartingItemsPrefabs")]
    internal static class StarterKit
    {
        internal const string GivenKey = "Contracts_StarterKitGiven";

        private static void Postfix(SaveGameData data, ref List<StartingItems.StartingItem> __result)
        {
            try
            {
                if (data == null || __result == null || data.GetBool(GivenKey) == true || !NewCareer())
                    return;
                var starter = StarterChoice.Current();
                __result = WithKit(__result, starter, out var added);
                data.SetBool(GivenKey, true);
                if (added.Count > 0)
                    Main.Log($"starter: a {Starters.Name(starter)} start comes with {string.Join(" and ", added)}");
            }
            catch (Exception e)
            {
                Main.Error($"starter: the steam start's lighter and shovel could not be added: {e.Message}");
            }
        }

        /// <summary>A career's starting items with what its starter lacks (Starters.KitMissing) added; a new list if anything was.</summary>
        internal static List<StartingItems.StartingItem> WithKit(List<StartingItems.StartingItem> items, StarterLocomotive starter, out List<string> added)
        {
            added = new List<string>();
            var missing = Starters.KitMissing(starter, items.Select(i => i.item != null ? i.item.ItemPrefabName : null).Where(n => n != null));
            if (missing.Count == 0)
                return items;
            var result = new List<StartingItems.StartingItem>(items);
            foreach (var name in missing)
            {
                var spec = Resources.Load<GameObject>(name)?.GetComponent<InventoryItemSpec>();
                if (spec == null)
                    continue;
                result.Add(new StartingItems.StartingItem { item = spec, preferredRelativeSlot = 0, backpackPriority = true, allowDuplicates = false });
                added.Add(name);
            }
            return added.Count > 0 ? result : items;
        }

        private static bool NewCareer() => UnityEngine.Object.FindObjectOfType<AStartGameData>() is StartGameData_NewCareer;
    }
}
