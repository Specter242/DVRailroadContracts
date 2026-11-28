using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Contracts.Core.Shop;
using Contracts.Mod.Menu;
using DV;
using DV.Common;
using DV.ThingTypes;
using DV.InventorySystem;
using DV.UI;
using DV.UI.PresetEditors;
using DV.UIFramework;
using DV.UserManagement;
using DV.Utils;
using HarmonyLib;
using TMPro;
using UnityEngine;
using static System.FormattableString;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// The new-career screen's Starting locomotive row, checked as a player uses it
    /// (newcareer.txt in the mod's folder, e.g. "S060 quit"; never shipped; run by
    /// scripts/run_spike.py --new-career in a copy of the game). It writes newcareer.log and views
    /// of the menu beside the mod:
    /// - in the main menu: a new career made on the career panel; its row shown, labelled, offering
    ///   the game's three and the JK3 only with its mod; each choice of its arrows kept with the
    ///   career as it is shown; no row for a career already started; where it sits among the
    ///   panel's other controls (nothing overlapped);
    /// - the career started with the Tutorial off, as a player starts one, with the choice;
    /// - in the world: the choice still kept with the career, and its locomotive delivered as the
    ///   new career's only one: whole, on one track and fully on it, on the rails, with its
    ///   licence; a steam start's lighter and shovel in the inventory on Basic starting items, and
    ///   no second ones on Expanded; none for a diesel start.
    /// Then the game quits, if newcareer.txt says "quit".
    /// </summary>
    internal sealed class NewCareerCheck : MonoBehaviour
    {
        internal const string CareerName = "Contracts new-career check";
        private static string folder;
        private static bool quitAfter;
        private static StarterLocomotive choice;
        private static bool jk3Expected;
        private static bool basicItems;
        private static bool restore;
        private static bool wreckBug;
        private static readonly List<string> errors = new List<string>();
        private StreamWriter log;
        private int problems;

        public static void Install(string modFolder)
        {
            folder = modFolder;
            var trigger = Path.Combine(folder, "newcareer.txt");
            if (!File.Exists(trigger))
                return;
            var words = File.ReadAllText(trigger).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            quitAfter = words.Contains("quit");
            jk3Expected = words.Contains("with-jk3");
            basicItems = words.Contains("basic");
            restore = words.Contains("restore");
            wreckBug = words.Contains("wreck-bug");
            RestorationWrecks.SimulateOldBuild = wreckBug;
            choice = Starters.Valid(words.FirstOrDefault(w => Enum.TryParse(w, out StarterLocomotive _)), Starters.Offered(true));
            Application.logMessageReceived += (condition, stack, type) =>
            {
                if ((type == LogType.Exception || type == LogType.Error) && errors.Count < 300)
                    errors.Add($"{type}: {(condition ?? "").Split('\n')[0]} @ {(stack ?? "").Split('\n').FirstOrDefault()?.Trim()}");
            };
            var go = new GameObject("[contracts new-career check]");
            DontDestroyOnLoad(go);
            var check = go.AddComponent<NewCareerCheck>();
            check.StartCoroutine(words.Contains("incompatible") ? check.OffCheck() : check.Run());
            Main.Log($"new-career check requested: {Starters.Name(choice)}");
        }

        private void Line(string text)
        {
            log?.WriteLine(text);
            log?.Flush();
            Main.Log("new-career check: " + text);
        }

        private void Problem(string text)
        {
            problems++;
            Line("PROBLEM: " + text);
        }

        private IEnumerator Run()
        {
            log = new StreamWriter(Path.Combine(folder, "newcareer.log"), false);
            Line($"new-career check {DateTime.Now:yyyy-MM-dd HH:mm:ss}: start with the {Starters.Name(choice)}; Jk3 expected {jk3Expected}; Basic starting items {basicItems}");
            ContinueLoadNewControllerSingle panel = null;
            for (var waited = 0f; waited < 300f; waited += 1f)
            {
                panel = Resources.FindObjectsOfTypeAll<ContinueLoadNewControllerSingle>()
                    .FirstOrDefault(p => p.gameObject.scene.IsValid() && StarterRow.IsCareer(p) && Traverse.Create(p).Property<bool>("IsInitialized").Value);
                if (panel != null)
                    break;
                yield return new WaitForSecondsRealtime(1f);
            }
            if (panel == null)
            {
                Problem("the main menu's career panel never came up");
                yield return Finish();
                yield break;
            }
            yield return new WaitForSecondsRealtime(3f);
            yield return Menu(panel);
            if (problems > 0 && !started)
            {
                yield return Finish();
                yield break;
            }
            yield return World();
            yield return Finish();
        }

        private bool started;

        /// <summary>
        /// With an incompatible mod enabled ("incompatible"; Incompatible): Contracts stays off, with
        /// none of its patches and no Starting locomotive row, and the main menu shows its notice.
        /// </summary>
        private IEnumerator OffCheck()
        {
            log = new StreamWriter(Path.Combine(folder, "newcareer.log"), false);
            Line($"incompatible check {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            ContinueLoadNewControllerSingle panel = null;
            for (var waited = 0f; panel == null && waited < 300f; waited += 1f)
            {
                panel = Resources.FindObjectsOfTypeAll<ContinueLoadNewControllerSingle>()
                    .FirstOrDefault(p => p.gameObject.scene.IsValid() && StarterRow.IsCareer(p) && Traverse.Create(p).Property<bool>("IsInitialized").Value);
                yield return new WaitForSecondsRealtime(1f);
            }
            if (panel == null)
            {
                Problem("the main menu's career panel never came up");
                yield return Finish();
                yield break;
            }
            PopupManager popups = null;
            Popup shown = null;
            for (var waited = 0f; shown == null && waited < 60f; waited += 1f)
            {
                panel.FindPopupManager(ref popups);
                shown = popups?.ActivePopup;
                yield return new WaitForSecondsRealtime(1f);
            }
            var text = shown == null ? null : string.Join(" | ", shown.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true).Select(t => t.text).Where(t => !string.IsNullOrWhiteSpace(t)));
            var entry = UnityModManagerNet.UnityModManager.FindMod("Contracts");
            var other = UnityModManagerNet.UnityModManager.FindMod("CareerRework");
            var patched = HarmonyLib.Harmony.GetAllPatchedMethods().Where(m => HarmonyLib.Harmony.GetPatchInfo(m)?.Owners?.Contains(Main.Mod?.Info?.Id) == true).ToList();
            Line($"off: CareerRework enabled {other?.Enabled}, active {other?.Active}; Contracts active {entry?.Active}, error on loading {entry?.ErrorOnLoading}; Contracts' patches {patched.Count}; " +
                 $"Starting locomotive row {(StarterRow.Of(panel) != null ? "present" : "none")}");
            Line($"off: main menu notice: {text ?? "none"}");
            if (entry == null || entry.Active || patched.Count > 0 || StarterRow.Of(panel) != null)
                Problem("off: Contracts is not off beside CareerRework");
            if (text == null || text.IndexOf("CareerRework", StringComparison.Ordinal) < 0)
                Problem("off: the main menu shows no notice naming CareerRework");
            yield return Capture("newcareer-incompatible.png");
            yield return Finish();
        }

        // ------------------------------------------------------------ the main menu

        private IEnumerator Menu(ContinueLoadNewControllerSingle panel)
        {
            Exception failed = null;
            Selector row = null;
            IGameSession session = null;
            try
            {
                ShowPanel(panel);
            }
            catch (Exception e)
            {
                Line($"showing the career panel failed: {e.Message}");
            }
            yield return new WaitForSecondsRealtime(2f);
            try
            {
                var offered = StarterChoice.Offered();
                Line($"menu: Jk3 loaded {StarterChoice.Jk3Installed()}; offered {string.Join(", ", offered.Select(Starters.Name))}");
                if (offered.Contains(StarterLocomotive.JK3) != jk3Expected)
                    Problem($"menu: the JK3 is {(jk3Expected ? "not offered with" : "offered without")} its mod");
                if (!offered.Contains(choice))
                    Problem($"menu: the {Starters.Name(choice)} is not offered");
                var before = panel.Things.Count;
                Traverse.Create(panel).Method("CreateNewImpl", new[] { typeof(string) }).GetValue(CareerName);
                session = panel.CurrentThing;
                Line($"menu: career made: '{session?.Name}' (id {session?.SessionID}), {panel.Things.Count} careers (was {before}), saves {session?.Saves.Count}");
                if (session == null || session.Name != CareerName || session.Saves.Count != 0)
                    Problem("menu: the new career was not made and shown");
                row = StarterRow.Of(panel);
                if (row == null)
                    Problem("menu: the career panel has no Starting locomotive row");
                else
                {
                    var label = row.GetLabelTMPro()?.text;
                    var values = row.GetValues().ToList();
                    Line($"menu: row shown {row.gameObject.activeInHierarchy}; label '{label}'; values {string.Join(", ", values)}; shows {Shown(row)}; " +
                         $"interactable {row.IsInteractable}; saved before any choice {StarterChoice.Saved(session) ?? "none"}");
                    if (!row.gameObject.activeInHierarchy || label != StarterRow.Label || !values.SequenceEqual(offered.Select(Starters.Name)) || !row.IsInteractable)
                        Problem("menu: the row is not shown as it should be (shown, labelled, offering every choice, usable)");
                    if (Shown(row) != Starters.Name(Starters.Default) || StarterChoice.Saved(session) != null)
                        Problem("menu: a new career's row does not start at the DE2 with nothing saved");
                    Layout(panel, row);
                }
            }
            catch (Exception e)
            {
                failed = e;
            }
            if (failed != null || row == null)
            {
                if (failed != null)
                    Problem($"menu: {failed}");
                yield break;
            }
            yield return Capture("newcareer-menu.png");
            // Each choice, by the row's right arrow, as a player clicks it: kept with the career as shown.
            var right = row.transform.Find("[arrow right]")?.GetComponent<ButtonDV>();
            var left = row.transform.Find("[arrow left]")?.GetComponent<ButtonDV>();
            if (right == null || left == null)
            {
                Problem("menu: the row has no arrows");
                yield break;
            }
            var seen = new List<string>();
            for (var i = 0; i < row.GetValues().Count; i++)
            {
                right.Click();
                yield return null;
                var shown = Shown(row);
                var saved = StarterChoice.Saved(session);
                seen.Add($"{shown} (saved {saved ?? "none"})");
                if (saved != shown)
                    Problem($"menu: the row shows the {shown}, the career keeps {saved ?? "nothing"}");
            }
            left.Click();
            yield return null;
            var back = Shown(row);
            seen.Add($"left: {back} (saved {StarterChoice.Saved(session) ?? "none"})");
            Line($"menu: right arrow through every choice: {string.Join("; ", seen)}");
            if (StarterChoice.Saved(session) != back)
                Problem("menu: the left arrow's choice is not kept with the career");
            for (var i = 0; i < row.GetValues().Count && Shown(row) != Starters.Name(choice); i++)
            {
                right.Click();
                yield return null;
            }
            // A career already started shows no row, and keeps what it had.
            failed = null;
            try
            {
                var startedIndex = panel.Things.ToList().FindIndex(t => t.Saves.Count > 0 && t.Name != CareerName);
                var other = startedIndex >= 0 ? panel.Things[startedIndex] : null;
                if (other == null)
                    Line("menu: no started career in this game to compare with");
                else
                {
                    var had = StarterChoice.Saved(other);
                    panel.ChangeSession(startedIndex);
                    var shownThere = StarterRow.Of(panel)?.gameObject.activeInHierarchy ?? false;
                    Line($"menu: started career '{other.Name}' ({other.Saves.Count} saves): row shown {shownThere}; keeps {had ?? "nothing"}, now {StarterChoice.Saved(other) ?? "nothing"}");
                    if (shownThere || StarterChoice.Saved(other) != had)
                        Problem("menu: a career already started shows the row or had its choice changed");
                    panel.ChangeSession(panel.Things.IndexOf(session));
                }
                var rowAgain = StarterRow.Of(panel);
                Line($"menu: back on '{panel.CurrentThing?.Name}': row shown {rowAgain?.gameObject.activeInHierarchy}, shows {(rowAgain == null ? "none" : Shown(rowAgain))}, saved {StarterChoice.Saved(session) ?? "none"}");
                if (panel.CurrentThing != session || rowAgain == null || !rowAgain.gameObject.activeInHierarchy || Shown(rowAgain) != Starters.Name(choice))
                    Problem("menu: coming back to the new career does not show its choice");
                // A difficulty with the game's Basic starting items (its default; "basic"), where a
                // steam start lacks the lighter and shovel: 0 Basic, 3 Auto (Basic in a career).
                if (basicItems)
                {
                    var basic = panel.CRUD.Difficulties.FirstOrDefault(d => d.StartingItems == 0) ?? panel.CRUD.Difficulties.FirstOrDefault(d => d.StartingItems == 3);
                    if (basic == null)
                        Problem("menu: no difficulty with Basic starting items");
                    else
                    {
                        panel.ChangeDifficulty(basic);
                        Line($"menu: difficulty '{basic.Name}' (starting items {basic.StartingItems}); row shown {StarterRow.Of(panel)?.gameObject.activeInHierarchy}, shows {Shown(StarterRow.Of(panel))}");
                        if (Shown(StarterRow.Of(panel)) != Starters.Name(choice))
                            Problem("menu: changing the difficulty changed the row's choice");
                    }
                }
                // The Tutorial off, as a career is started without it (the row stays).
                panel.tutorialCheckbox.isOn = false;
                Line($"menu: Tutorial off; row shown {StarterRow.Of(panel)?.gameObject.activeInHierarchy}; start button usable {panel.startNewSessionButton.IsInteractable}");
                if (!(StarterRow.Of(panel)?.gameObject.activeInHierarchy ?? false))
                    Problem("menu: the row is gone with the Tutorial off");
            }
            catch (Exception e)
            {
                failed = e;
            }
            if (failed != null)
            {
                Problem($"menu: {failed}");
                yield break;
            }
            yield return Capture("newcareer-chosen.png");
            // Started as a player starts one: the panel's Start, then the launcher's Run.
            failed = null;
            try
            {
                panel.startNewSessionButton.Click();
                Line($"menu: Start pressed; the career keeps {StarterChoice.Saved(session) ?? "nothing"}");
                if (StarterChoice.Saved(session) != Starters.Name(choice))
                    Problem($"menu: starting the career did not keep the {Starters.Name(choice)}");
            }
            catch (Exception e)
            {
                failed = e;
            }
            if (failed != null)
            {
                Problem($"menu: Start: {failed}");
                yield break;
            }
            LauncherController launcher = null;
            for (var waited = 0f; launcher == null && waited < 30f; waited += 0.5f)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                launcher = Resources.FindObjectsOfTypeAll<LauncherController>().FirstOrDefault(l => l.gameObject.scene.IsValid() && l.isActiveAndEnabled);
            }
            if (launcher == null)
            {
                Problem("menu: the launcher never showed after Start");
                yield break;
            }
            yield return new WaitForSecondsRealtime(1f);
            yield return Capture("newcareer-launcher.png");
            Line($"menu: launcher shows '{launcher.headerTMPro?.text}'; Run pressed");
            started = true;
            launcher.runButton.onClick.Invoke();
        }

        /// <summary>Opens the menus the career panel is in, outermost first, so it is on screen for the views.</summary>
        private void ShowPanel(ContinueLoadNewControllerSingle panel)
        {
            int Depth(Transform t)
            {
                var d = 0;
                for (; t != null; t = t.parent)
                    d++;
                return d;
            }
            foreach (var controller in Resources.FindObjectsOfTypeAll<UIMenuController>().Where(c => c.gameObject.scene.IsValid()).OrderBy(c => Depth(c.transform)))
            {
                var index = controller.controlledMenus.FindIndex(m => m != null && panel.transform.IsChildOf(m.transform));
                if (index >= 0 && controller.ActiveIndex != index)
                {
                    controller.SwitchMenu(index);
                    Line($"menu: opened menu {index} ({controller.controlledMenus[index].name}) of {controller.name}");
                }
            }
        }

        private static string Shown(Selector row) =>
            row.GetValues().Count > 0 && row.SelectedIndex >= 0 && row.SelectedIndex < row.GetValues().Count ? row.GetValues()[row.SelectedIndex] : null;

        /// <summary>Where the row sits: its rectangle and its neighbours', and whether it covers any control shown.</summary>
        private void Layout(ContinueLoadNewControllerSingle panel, Selector row)
        {
            Rect Of(RectTransform t)
            {
                var c = new Vector3[4];
                t.GetWorldCorners(c);
                return Rect.MinMaxRect(c.Min(v => v.x), c.Min(v => v.y), c.Max(v => v.x), c.Max(v => v.y));
            }
            string Show(Rect r) => Invariant($"({r.xMin:F0},{r.yMin:F0})-({r.xMax:F0},{r.yMax:F0})");
            var mine = Of((RectTransform)row.transform);
            var parent = row.transform.parent;
            Line($"layout: row under {parent.name} (sibling {row.transform.GetSiblingIndex()} of {parent.childCount}) at {Show(mine)}; components {string.Join(", ", row.GetComponents<Component>().Select(c => c.GetType().Name))}");
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                Line($"layout:   {i} {child.name} active {child.gameObject.activeSelf} {(child is RectTransform r ? Show(Of(r)) : "")}");
            }
            var controls = new List<Component> { panel.difficultySelectorLogic?.selector, panel.editDifficultyButton, panel.tutorialCheckbox, panel.startNewSessionButton, panel.continueButton, panel.loadButton, panel.newPresetButton }
                .Concat(panel.GetComponentsInChildren<Selector>(false)).Concat(panel.GetComponentsInChildren<UnityEngine.UI.Selectable>(false))
                .Where(c => c != null && c.gameObject.activeInHierarchy && c.transform is RectTransform && c.transform != row.transform && !c.transform.IsChildOf(row.transform))
                .Distinct().ToList();
            var covered = controls.Select(c => (c, r: Of((RectTransform)c.transform)))
                .Where(x => x.r.Overlaps(mine) && Rect.MinMaxRect(Math.Max(x.r.xMin, mine.xMin), Math.Max(x.r.yMin, mine.yMin), Math.Min(x.r.xMax, mine.xMax), Math.Min(x.r.yMax, mine.yMax)).height > 1f)
                .ToList();
            Line($"layout: {controls.Count} other controls shown; the row overlaps {(covered.Count == 0 ? "none" : string.Join(", ", covered.Select(x => $"{x.c.name} {Show(x.r)}")))}");
            if (covered.Count > 0)
                Problem("layout: the row overlaps another control on the career panel");
        }

        private IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            try
            {
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, name));
                Line($"view: {name}");
            }
            catch (Exception e)
            {
                Line($"view {name} failed: {e.Message}");
            }
            yield return new WaitForSecondsRealtime(1f);
        }

        // ------------------------------------------------------------ the world

        private IEnumerator World()
        {
            for (var waited = 0f; !(ContractRuntime.Active && AStartGameData.carsAndJobsLoadingFinished && WorldStreamingInit.IsLoaded) && waited < 600f; waited += 1f)
                yield return new WaitForSecondsRealtime(1f);
            if (!ContractRuntime.Active)
            {
                Problem("world: the career never loaded with the contracts running");
                yield break;
            }
            yield return new WaitForSecondsRealtime(10f);
            Exception failed = null;
            try
            {
                WorldChecks();
            }
            catch (Exception e)
            {
                failed = e;
            }
            if (failed != null)
                Problem($"world: {failed}");
            if (restore)
                yield return RestorationCheck.Restore(Line, Problem);
            if (wreckBug)
                yield return OldCareer();
            // The game's save after a new career's load (StartGameData_NewCareer.ShouldCreateSaveGameAfterLoad), then one more.
            yield return new WaitForSecondsRealtime(5f);
            try
            {
                SingletonBehaviour<SaveGameManager>.Instance?.Save(SaveType.Manual);
                Line("world: saved");
            }
            catch (Exception e)
            {
                Line($"world: saving failed: {e.Message}");
            }
            yield return new WaitForSecondsRealtime(5f);
        }

        /// <summary>
        /// A career as 0.2.x left it ("wreck-bug"): the wrecks' licences given free at its first load;
        /// then a DH4 bought at the kiosk and the S060 licence bought at the career manager (its
        /// booklet printed beside the player), for the next load's one-time check to keep (#45).
        /// </summary>
        private IEnumerator OldCareer()
        {
            var licences = SingletonBehaviour<LicenseManager>.Instance;
            string Held() => string.Join(", ", Globals.G.Types.generalLicenses.Where(licences.IsGeneralLicenseAcquired).Select(l => l.id));
            // Still as 0.2.x through the save, so this build's one-time check waits for the next load.
            Line($"old career: licences held after the first load, as 0.2.x gave them: {Held()}");
            Exception failed = null;
            try
            {
                var dh4 = LocoShop.Catalogue(LocoShop.Kind.Locomotives).FirstOrDefault(i => i.Name == "DH4");
                var inventory = SingletonBehaviour<DV.InventorySystem.Inventory>.Instance;
                inventory.AddMoney(dh4.Price + 1000.0);
                Line("old career: bought at the kiosk: " + LocoShop.Buy(dh4, null).Split('\n')[0]);
                var s060 = Globals.G.Types.generalLicenses.First(l => l.id == "S060");
                var player = PlayerManager.PlayerTransform;
                var booklet = DV.Booklets.BookletCreator.CreateLicense(s060, player.position + player.forward * 1.5f + Vector3.up, Quaternion.identity);
                Line($"old career: the S060 licence bought at the career manager (held already {licences.IsGeneralLicenseAcquired(s060)}): booklet {booklet?.name}");
            }
            catch (Exception e)
            {
                failed = e;
            }
            if (failed != null)
                Problem($"old career: {failed}");
            // Saved as 0.2.x would: the one-time check not yet run.
            ContractRuntime.Instance.State.WreckLicencesChecked = false;
            yield return new WaitForSecondsRealtime(3f);
            Line($"old career: licences held at the save: {Held()}");
        }

        private void WorldChecks()
        {
            var rt = ContractRuntime.Instance;
            var session = SingletonBehaviour<UserManager>.Instance?.CurrentUser?.CurrentSession;
            Line($"world: career '{session?.Name}' keeps {StarterChoice.Saved(session) ?? "nothing"}; starter given {rt.State.StarterGiven}, due {rt.State.StarterDue}");
            foreach (var n in rt.Notes.Where(n => n.StartsWith("starter", StringComparison.Ordinal) || n.StartsWith("licenses", StringComparison.Ordinal)))
                Line("note: " + n);
            if (session?.Name != CareerName || StarterChoice.Saved(session) != Starters.Name(choice))
                Problem("world: the career does not keep its choice");
            if (!rt.State.StarterGiven)
                Problem("world: the starter was not given");
            var cars = StarterChoice.Cars(choice);
            // The game's restoration wrecks are nobody's (RestorationWrecks, #45): checked on their own below.
            var units = VehicleSales.Owned().Where(u => u.Cars.Any(c => CarTypes.IsLocomotive(c.carLivery))).ToList();
            if (wreckBug)
            {
                // As 0.2.x the kiosk lists the wrecks too: the starter is checked among the rest.
                RestorationWrecks.SimulateOldBuild = false;
                var wreckCars = RestorationWrecks.Cars();
                RestorationWrecks.SimulateOldBuild = true;
                units = units.Where(u => !u.Cars.Any(wreckCars.Contains)).ToList();
            }
            Line($"world: owned locomotive units {units.Count}: {string.Join("; ", units.Select(u => string.Join(" + ", u.Cars.Select(c => $"{c.ID} {c.carLivery.id}"))))}; expected {string.Join(" + ", cars?.Select(l => l.id) ?? new string[0])}");
            if (wreckBug)
                Line("world: as 0.2.x, the wrecks taken for the player's (no restoration check)");
            else
                RestorationCheck.Start(rt.Notes, Line, Problem);
            var unit = units.FirstOrDefault();
            if (cars == null || units.Count != 1 || !unit.Cars.Select(c => c.carLivery).SequenceEqual(cars))
                Problem($"world: the new career's only locomotive is not the {Starters.Name(choice)} (whole)");
            if (unit != null)
            {
                var track = unit.Cars[0].logicCar.FrontBogieTrack;
                var fits = track != null && unit.Cars.All(c => c.logicCar.FrontBogieTrack == track && c.logicCar.RearBogieTrack == track) &&
                           unit.Cars.All(c => track.GetCarsFullyOnTrack().Contains(c.logicCar));
                var player = PlayerManager.PlayerTransform;
                var station = StationController.allStations?.Where(s => s != null).OrderBy(s => (s.transform.position - unit.Cars[0].transform.position).sqrMagnitude).FirstOrDefault();
                var licence = unit.Cars[0].carLivery.requiredLicense;
                var licensed = licence == null || SingletonBehaviour<LicenseManager>.Instance.IsGeneralLicenseAcquired(licence);
                Line(Invariant($"world: it stands on {track?.ID?.FullDisplayID} ({track?.length:F0} m) at {station?.stationInfo?.YardID}, ") +
                     Invariant($"{(player == null ? -1f : Vector3.Distance(player.position, unit.Cars[0].transform.position)):F0} m from the player; on one track and fully on it {fits}; ") +
                     $"on the rails {unit.Cars.All(c => !c.derailed)}; kept from Clear {unit.Cars.All(c => c.preventDelete)}; licence {licence?.id ?? "none"} {(licensed ? "held" : "NOT held")}");
                if (!fits || unit.Cars.Any(c => c.derailed) || !licensed || unit.Cars.Any(c => !c.preventDelete))
                    Problem("world: the starter does not stand whole on its track, on the rails, licensed and kept");
            }
            // The steam kit: the starting items the career got, then the lighters and shovels the player has.
            var data = SingletonBehaviour<SaveGameManager>.Instance?.data;
            var startingItems = data?.GetInt("Starting_items");
            var storage = SingletonBehaviour<StorageController>.Instance;
            int Count(StorageBase s, string name) => s == null ? 0 : s.GetStorageItemList().Count(i => i != null && i.InventorySpecs != null && i.InventorySpecs.ItemPrefabName == name);
            var inventory = Starters.SteamKit.ToDictionary(n => n, n => Count(storage?.StorageInventory, n));
            var lostAndFound = Starters.SteamKit.ToDictionary(n => n, n => Count(storage?.StorageLostAndFound, n));
            Line($"world: starting items {startingItems} (0 Basic, 1 Expanded, 2 Engineer); kit given {data?.GetBool(StarterKit.GivenKey)}; " +
                 $"in the inventory {string.Join(", ", inventory.Select(kv => $"{kv.Key} {kv.Value}"))}; lost and found {string.Join(", ", lostAndFound.Select(kv => $"{kv.Key} {kv.Value}"))}");
            // One of each for a steam start (the kit's on Basic items, the game's own on the others);
            // for a diesel start, the game's own only (none on Basic).
            var expected = Starters.IsSteam(choice) || startingItems != 0 ? 1 : 0;
            if (startingItems == null || Starters.SteamKit.Any(n => inventory[n] + lostAndFound[n] != expected))
                Problem($"world: the player has not exactly {expected} lighter and shovel for a {(Starters.IsSteam(choice) ? "steam" : "diesel")} start on these starting items");
        }

        private IEnumerator Finish()
        {
            if (errors.Count > 0)
            {
                Line($"errors logged: {errors.Count}");
                foreach (var e in errors.Distinct().Take(40))
                    Line("  " + e);
            }
            Line($"new-career check: {problems} problem(s)");
            Line("NEWCAREER DONE");
            log?.Close();
            log = null;
            yield return new WaitForSecondsRealtime(2f);
            if (quitAfter)
                Application.Quit();
        }
    }
}
