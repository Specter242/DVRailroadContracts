using System;
using System.Linq;
using Contracts.Core.Shop;
using Contracts.Mod.Runtime;
using DV.UI;
using DV.UI.PresetEditors;
using DV.UIFramework;
using HarmonyLib;
using UnityEngine;

namespace Contracts.Mod.Menu
{
    /// <summary>
    /// The new-career screen's "Starting locomotive" row (owner, 2026-11-23: "Can we do this in a
    /// more elegant way than a universal setting in the Unity menu?", then "Let's do this and we can
    /// also add the JK3 as an option if installed"). It sits on the career panel below the
    /// difficulty, and like the Tutorial checkbox it shows only for a career not yet started. Its
    /// arrows choose among Starters.Offered (the DE2, DM3 and S060, and the JK3 when its mod is
    /// loaded). The choice is kept with that career (StarterChoice) as it changes, and again when
    /// the career starts.
    ///
    /// The row is a copy of the panel's own scenario row (free roam's, hidden in a career), so it
    /// looks and works as the game's selectors do. If the panel is not as this build expects, it
    /// has no row, the error is logged once, and a new career starts with the DE2.
    /// </summary>
    [HarmonyPatch(typeof(ContinueLoadNewControllerSingle), nameof(ContinueLoadNewControllerSingle.RefreshInterface))]
    internal static class StarterRow
    {
        internal const string RowName = "[contracts] starting locomotive";
        internal const string Label = "Starting locomotive";
        private static bool failed;

        private static void Postfix(ContinueLoadNewControllerSingle __instance)
        {
            try
            {
                Refresh(__instance);
            }
            catch (Exception e)
            {
                Fail($"the new-career screen's Starting locomotive row failed; new careers start with the DE2: {e}");
            }
        }

        internal static bool IsCareer(ContinueLoadNewControllerSingle panel) =>
            panel != null && Traverse.Create(panel).Field<string>("gameMode").Value == "Career";

        /// <summary>The career panel's row, if it has one.</summary>
        internal static Selector Of(ContinueLoadNewControllerSingle panel)
        {
            var template = panel?.scenarioSelectorLogic?.selector;
            var parent = template == null ? null : template.transform.parent;
            return parent == null ? null : parent.Find(RowName)?.GetComponent<Selector>();
        }

        /// <summary>Whether the panel shows a career that hasn't started (no saves): when the row is shown.</summary>
        internal static bool NotStarted(ContinueLoadNewControllerSingle panel)
        {
            var session = panel.CurrentThing;
            return session != null && session.Saves.Count == 0 && panel.mode2Root != null && panel.mode2Root.activeSelf &&
                   panel.tutorialCheckbox != null && panel.tutorialCheckbox.gameObject.activeSelf;
        }

        private static void Refresh(ContinueLoadNewControllerSingle panel)
        {
            if (!IsCareer(panel))
                return;
            var row = Of(panel) ?? Build(panel);
            if (row == null)
                return;
            var shown = NotStarted(panel);
            row.gameObject.SetActive(shown);
            if (!shown)
                return;
            if (row.GetLabelTMPro() != null && row.GetLabelTMPro().text != Label)
                row.SetLabel(Label);
            var offered = StarterChoice.Offered().ToList();
            row.SetValues(offered.Select(Starters.Name).ToList());
            row.SetSelectedIndex(offered.IndexOf(StarterChoice.Of(panel.CurrentThing)), fireEvent: false);
            row.ToggleInteractable(offered.Count > 1);
        }

        private static Selector Build(ContinueLoadNewControllerSingle panel)
        {
            var template = panel.scenarioSelectorLogic?.selector;
            if (template == null || template.transform.parent == null)
            {
                Fail("the career panel has no scenario row to copy; new careers start with the DE2");
                return null;
            }
            // Copied while inactive, so nothing of the copy wakes before the scenario row's own
            // logic is taken off it.
            var was = template.gameObject.activeSelf;
            template.gameObject.SetActive(false);
            GameObject copy;
            try
            {
                copy = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
            }
            finally
            {
                template.gameObject.SetActive(was);
            }
            copy.name = RowName;
            foreach (var logic in copy.GetComponentsInChildren<ASelectorLogic>(true))
                UnityEngine.Object.DestroyImmediate(logic);
            foreach (var mark in copy.GetComponentsInChildren<TMProAddMark>(true))
                UnityEngine.Object.DestroyImmediate(mark);
            copy.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
            var row = copy.GetComponent<Selector>();
            if (row == null)
            {
                UnityEngine.Object.Destroy(copy);
                Fail("the career panel's scenario row is not a selector; new careers start with the DE2");
                return null;
            }
            // The copied label and value keep the scenario row's localization (the game's own
            // component, and the localization plugin's), which would put "Scenario" back on it
            // as it is enabled: taken off, so the row says what it is.
            foreach (var text in new[] { "[texts]/[text label]", "[texts]/[text value] [noloc]" }.Select(path => copy.transform.Find(path)).Where(t => t != null))
                foreach (var localize in text.GetComponents<Component>().Where(c => c != null && c.GetType().Name == "Localize").ToList())
                    UnityEngine.Object.DestroyImmediate(localize);
            row.LocalizedLabel = false;
            row.SetLabel(Label);
            row.LocalizedValues = false;
            row.SelectionChanged += (_, index) => Chosen(panel, index);
            Main.Log($"starter row: added to the career panel ({StarterChoice.Offered().Count} choices)");
            return row;
        }

        private static void Chosen(ContinueLoadNewControllerSingle panel, int index)
        {
            try
            {
                var offered = StarterChoice.Offered();
                if (!NotStarted(panel) || index < 0 || index >= offered.Count)
                    return;
                StarterChoice.Keep(panel.CurrentThing, offered[index]);
            }
            catch (Exception e)
            {
                Fail($"keeping the career's starting locomotive failed: {e.Message}");
            }
        }

        /// <summary>The row's choice, kept with the career as it starts (one never changed included: the DE2 shown).</summary>
        internal static void KeepShown(ContinueLoadNewControllerSingle panel)
        {
            var row = Of(panel);
            if (row == null || !row.gameObject.activeSelf || !NotStarted(panel))
                return;
            var offered = StarterChoice.Offered();
            var chosen = row.SelectedIndex >= 0 && row.SelectedIndex < offered.Count ? offered[row.SelectedIndex] : Starters.Default;
            StarterChoice.Keep(panel.CurrentThing, chosen);
            Main.Log($"starter: the new career '{panel.CurrentThing.Name}' starts with the {Starters.Name(chosen)}");
        }

        private static void Fail(string text)
        {
            if (failed)
                return;
            failed = true;
            Main.Error(text);
        }
    }

    [HarmonyPatch(typeof(ContinueLoadNewControllerSingle), "OnStartNewSessionClicked")]
    internal static class StarterRowStart
    {
        private static void Prefix(ContinueLoadNewControllerSingle __instance)
        {
            try
            {
                if (StarterRow.IsCareer(__instance))
                    StarterRow.KeepShown(__instance);
            }
            catch (Exception e)
            {
                Main.Error($"keeping the career's starting locomotive at its start failed: {e.Message}");
            }
        }
    }
}
