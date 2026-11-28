using System;
using System.Collections;
using System.Linq;
using DV.UI;
using DV.UI.PresetEditors;
using DV.UIFramework;
using UnityEngine;
using UnityModManagerNet;

namespace Contracts.Mod
{
    /// <summary>
    /// Mods Railroad Contracts can't run with (owner, 2026-11-24, on tracker #60: "incompatible is
    /// fine"). Unity Mod Manager has no way to mark a mod incompatible (its Info.json knows only
    /// requirements and load order), so the mod checks at load: with one of these enabled it
    /// stays off, says why in its log, and shows the game's own OK box in the main menu once.
    /// - **CareerRework** (CJ187, Nexus 1153): it takes over a new career's start (its own
    ///   starter locomotives, licences and money) and its licence progression, as Contracts does;
    ///   together Contracts' free licences skip CareerRework's progression, and both put a starter
    ///   on SM's locomotive track.
    /// </summary>
    internal static class Incompatible
    {
        internal sealed class Mod
        {
            public string Id, Name, Why;
        }

        private static readonly Mod[] Mods =
        {
            new Mod { Id = "CareerRework", Name = "CareerRework", Why = "both change how a career starts and how its licenses are earned" },
        };

        /// <summary>The first incompatible mod Unity Mod Manager runs (enabled), or null.</summary>
        internal static Mod Find()
        {
            foreach (var mod in Mods)
            {
                var entry = UnityModManager.FindMod(mod.Id);
                if (entry != null && entry.Enabled)
                    return mod;
            }
            return null;
        }

        internal static string Notice(Mod mod) =>
            $"Railroad Contracts is off. It can't run with {mod.Name}: {mod.Why}.\n\n" +
            "Turn one of them off in Unity Mod Manager (Ctrl+F10), then restart the game.";

        /// <summary>The notice in the main menu, once this game start, in the game's own OK box.</summary>
        internal static void ShowInMainMenu(string text)
        {
            var go = new GameObject("[contracts incompatible notice]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<MenuNotice>().Text = text;
        }

        private sealed class MenuNotice : MonoBehaviour
        {
            public string Text;

            private IEnumerator Start()
            {
                // The career panel carries the menu's OK box; wait for the menu (up to ten minutes).
                for (var waited = 0f; waited < 600f; waited += 1f)
                {
                    var panel = Resources.FindObjectsOfTypeAll<ContinueLoadNewControllerSingle>()
                        .FirstOrDefault(p => p != null && p.gameObject.scene.IsValid() && p.okPopupPrefab != null);
                    PopupManager popups = null;
                    if (panel != null)
                        panel.FindPopupManager(ref popups);
                    if (popups != null && popups.CanShowPopup())
                    {
                        try
                        {
                            popups.ShowPopup(panel.okPopupPrefab, new PopupLocalizationKeys { labelKey = Text, positiveKey = "OK" }, null, keepLiteralData: true);
                            Main.Log("incompatible: the notice was shown in the main menu");
                        }
                        catch (Exception e)
                        {
                            Main.Error($"incompatible: the main menu notice could not be shown: {e.Message}");
                        }
                        Destroy(gameObject);
                        yield break;
                    }
                    yield return new WaitForSecondsRealtime(1f);
                }
                Destroy(gameObject);
            }
        }
    }
}
