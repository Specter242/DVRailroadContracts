using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Contracts.Mod.Kiosk;
using DV.ServicePenalty.UI;
using DV.Utils;
using UnityEngine;

namespace Contracts.Mod.Spike
{
    /// <summary>
    /// Spike S6, run only when <c>spike.txt</c> sits in the mod's folder (never shipped). Once
    /// the game has loaded, at the Machine Factory's kiosk and at each interchange office's
    /// kiosk, the player stands at the screen and the spike presses the kiosk's buttons as a
    /// player would, checking each screen and capturing it (<c>spike-*.png</c>):
    /// the main menu's Contracts entry, the list, an offer, taking it through the confirm
    /// screen, the taken contract, raising it, and the stock entries still answering. At a
    /// kiosk whose only offer the player took elsewhere (the bridge, one contract for both
    /// interchanges), it ends that contract instead, which offers it again. It also surveys
    /// every kiosk in the scene: its place and its menu lines. It writes <c>spike.log</c> and
    /// quits afterwards if the trigger says "quit".
    /// </summary>
    internal sealed class KioskSpike : MonoBehaviour
    {
        private static string folder;
        private static bool quitAfter;
        private StreamWriter log;
        private int problems;
        private bool shopsChecked;
        private static readonly List<string> errors = new List<string>();

        public static void Install(string modFolder)
        {
            folder = modFolder;
            var trigger = Path.Combine(folder, "spike.txt");
            if (!File.Exists(trigger))
                return;
            quitAfter = File.ReadAllText(trigger).IndexOf("quit", StringComparison.OrdinalIgnoreCase) >= 0;
            var go = new GameObject("[contracts spike]");
            DontDestroyOnLoad(go);
            var spike = go.AddComponent<KioskSpike>();
            Application.logMessageReceived += (condition, stack, type) =>
            {
                if ((type == LogType.Exception || type == LogType.Error) && errors.Count < 200)
                    errors.Add($"{type}: {(condition ?? "").Split('\n')[0]} @ {(stack ?? "").Split('\n').FirstOrDefault()?.Trim()}");
            };
            var started = false;
            WorldStreamingInit.LoadingFinished += () =>
            {
                if (started)
                    return;
                started = true;
                spike.StartCoroutine(spike.Run());
            };
            Main.Log("spike S6 requested");
        }

        private void Line(string text)
        {
            Main.Log("spike: " + text);
            log?.WriteLine(text);
            log?.Flush();
        }

        private void Problem(string text)
        {
            problems++;
            Line("PROBLEM " + text);
        }

        private IEnumerator Run()
        {
            log = new StreamWriter(Path.Combine(folder, "spike.log"), false);
            Line($"spike S6 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Line($"multiplayer: {Kiosk.Multiplayer.Describe()}");
            Line("mods: " + string.Join(", ", UnityModManagerNet.UnityModManager.modEntries.Where(m => m.Enabled && m.Active).Select(m => $"{m.Info.Id} {m.Info.Version}")));
            yield return new WaitForSeconds(8f);
            var errorsAtLoad = errors.Count;
            Survey();
            var kiosks = new List<(string Name, DisplayScreenSwitcher Kiosk)>();
            var mf = GameObject.Find("Offices/Offices_w3/MachineFactory");
            if (mf != null)
                kiosks.Add(("MF", mf.GetComponentInChildren<DisplayScreenSwitcher>(true)));
            var offices = GameObject.Find("Offices/Offices_w3");
            if (offices != null)
                foreach (Transform child in offices.transform)
                    if (child.name.StartsWith(KioskPlaces.InterchangePrefix, StringComparison.Ordinal))
                        kiosks.Add((child.name.Substring(KioskPlaces.InterchangePrefix.Length), child.GetComponentInChildren<DisplayScreenSwitcher>(true)));
            Line("kiosks to test: " + string.Join(", ", kiosks.Select(k => k.Name)));
            foreach (var (name, kiosk) in kiosks)
            {
                if (kiosk == null)
                {
                    Problem($"{name}: no kiosk");
                    continue;
                }
                yield return Exercise(name, kiosk);
            }
            Line($"errors logged while loading: {errorsAtLoad}; while testing: {errors.Count - errorsAtLoad}");
            foreach (var e in errors.Skip(errorsAtLoad).Distinct().Take(40))
                Line("  " + e);
            if (errors.Skip(errorsAtLoad).Any(e => e.Contains("Contracts") || e.Contains("ServicePenalty")))
                Problem("errors from the kiosk or this mod while testing");
            Line($"SPIKE DONE: {problems} problem(s)");
            log.Dispose();
            log = null;
            if (quitAfter)
            {
                yield return new WaitForSeconds(2f);
                Application.Quit();
            }
        }

        /// <summary>Every kiosk in the scene: whether its main menu took the Contracts entry, and on which line.</summary>
        private void Survey()
        {
            var mains = Resources.FindObjectsOfTypeAll<CareerManagerMainScreen>().Where(m => m.gameObject.scene.IsValid()).ToList();
            var extended = mains.Where(m => KioskMenu.TryGet(m, out _)).ToList();
            Line($"kiosks in the scene: {mains.Count}, with Contracts: {extended.Count}");
            foreach (var m in mains.OrderBy(m => KioskMenu.Where(m)))
            {
                var place = KioskPlaces.Of(m.screenSwitcher);
                var line = KioskMenu.TryGet(m, out var x) ? x.Line.name : "none";
                var offers = KioskMenu.Desk?.Offers(place).Count ?? 0;
                Line($"  {KioskMenu.Where(m)}: place {place?.Id ?? "none"}{(place?.IsInterchange == true ? " (interchange)" : "")}; " +
                     $"menu lines {m.fees?.name}, {m.licenses?.name}, {m.ownedVehicles?.name}, {m.stats?.name}, {line}; offers {offers}");
                if (place == null)
                    Problem($"{KioskMenu.Where(m)}: serves no place");
            }
            if (extended.Count != mains.Count)
                Problem($"{mains.Count - extended.Count} kiosk(s) without the Contracts entry");
            if (KioskMenu.Desk is SpikeDesk desk)
            {
                Line($"industries: {desk.Industries.Count}: {string.Join(", ", desk.Industries.Keys.OrderBy(k => k))}");
                foreach (var id in new[] { "CMS", "CP", "SM", "MF", "MF-TOWN", "CS", "CS-FISH", "HB-OIL", "HB-GAS", "HB-IMPORT", "HB-EXPORT", "HB-TOWN", "HMB" })
                    Line("  " + desk.Describe(id));
                if (!desk.Industries.TryGetValue("CMS", out var cms) || !cms.Has(Core.Industries.LineRole.Product)
                    || !cms.Has(Core.Industries.LineRole.Material) || !cms.Has(Core.Industries.LineRole.Waste))
                    Problem("the coal mine's contract lacks products, materials or waste");
            }
        }

        private IEnumerator Exercise(string name, DisplayScreenSwitcher kiosk)
        {
            var main = kiosk.GetComponentInChildren<CareerManagerMainScreen>(true);
            if (main == null || !KioskMenu.TryGet(main, out var x))
            {
                Problem($"{name}: the kiosk has no Contracts entry");
                yield break;
            }
            // Stand at the screen, as a player does (the kiosk returns to its idle screen when
            // the player walks off), and let the office around it wake.
            Vector3 Facing()
            {
                var f = kiosk.transform.TransformDirection(Vector3.left);
                f.y = 0f;
                return f.normalized;
            }
            Vector3 Screen() => kiosk.transform.TransformPoint(new Vector3(-0.10f, 1.32f, -0.08f));
            var stand = Screen() + Facing() * 0.8f;
            stand.y = kiosk.transform.position.y + 0.15f;
            PlayerManager.TeleportPlayer(stand, Quaternion.LookRotation(-Facing(), Vector3.up), null, true);
            yield return new WaitForSeconds(10f);
            Line($"{name}: player {(PlayerManager.PlayerTransform.position - Screen()).magnitude:F2} m from the screen; screen {kiosk.CurrentScreen?.GetType().Name}");
            if (!(kiosk.CurrentScreen is CareerManagerMainScreen))
                kiosk.SetActiveDisplay(main);

            IEnumerator Press(InputAction a, int times = 1)
            {
                for (var i = 0; i < times; i++)
                {
                    kiosk.HandleInput(a);
                    yield return null;
                }
                yield return new WaitForSeconds(0.3f);
            }
            IEnumerator Shot(string what) => Capture($"spike-{name.Replace(' ', '-')}-{what}", Screen() + Facing() * 0.55f, Screen());
            string Menu() => string.Join(" | ", new[] { main.fees, main.licenses, main.ownedVehicles, main.stats, x.Line }.Select(t => t.text));

            // 1. The main menu: five entries, Contracts last, reached with Down x4 (and with Up from Fees).
            Line($"{name}: main menu: {Menu()}");
            if (x.Line.text != KioskMenu.Entry)
                Problem($"{name}: the fifth line reads '{x.Line.text}'");
            yield return Press(InputAction.Up);
            Line($"{name}: up from Fees selects {main.CurrentSelection} ({main.GetCurrentSelection()})");
            if (main.CurrentSelection != 4)
                Problem($"{name}: up from Fees did not wrap to Contracts");
            yield return Press(InputAction.Down);
            yield return Press(InputAction.Down, 4);
            Line($"{name}: down x4 selects {main.CurrentSelection} ({main.GetCurrentSelection()})");
            if (main.CurrentSelection != 4)
                Problem($"{name}: down x4 did not reach Contracts");
            yield return Shot("main");

            var ask = kiosk.GetComponentInChildren<CareerManagerCustomActionChoiceScreen>(true);

            // 2. The Contracts list.
            yield return Press(InputAction.Confirm);
            if (!ReferenceEquals(kiosk.CurrentScreen, x.List))
            {
                Problem($"{name}: Contracts opened {kiosk.CurrentScreen?.GetType().Name}");
                yield break;
            }
            Line($"{name}: list '{x.List.Place?.Id}': {x.List.Entries.Count} entries; shown: {string.Join("; ", x.List.Shown())}");
            Line($"{name}: list info line: {Text(x, l => l.Info)}");
            yield return Shot("list");

            // 2b. The shops, at the first kiosk (a Discord report, 2026-11-03: a coach set the wallet
            //     couldn't cover gave a refusal cut off at the info line's edge). With $1,000, a coach
            //     set is refused at once on the info screen, in full: the price, the cash, and how much
            //     is short; any button goes back to the shop. With plenty, a locomotive is bought
            //     through the confirm screen, and its result is shown in full too.
            var info = kiosk.GetComponentInChildren<CareerManagerInfoScreen>(true);
            var inventory = SingletonBehaviour<DV.InventorySystem.Inventory>.Instance;
            if (!shopsChecked && info != null && inventory != null)
            {
                shopsChecked = true;
                var cash = inventory.PlayerMoney;
                IEnumerator To(string id)
                {
                    var target = x.List.Entries.ToList().FindIndex(e => e.Id == id);
                    var at = x.List.IndexOfFirstDisplayedEntry + x.List.CurrentSelection;
                    if (target >= 0 && target != at)
                        yield return Press(target > at ? InputAction.Down : InputAction.Up, Math.Abs(target - at));
                }
                string Paragraph() => $"'{info.title.text}' / '{info.paragraph.text.Replace("\n", " | ")}' (overflowing {info.paragraph.isTextOverflowing})";

                inventory.SetMoney(1_000);
                yield return To(Runtime.RuntimeDesk.CoachShopId);
                yield return Press(InputAction.Confirm);
                Line($"{name}: coach shop: {string.Join("; ", x.Detail.Shown())}; info {Text(x, l => l.Info)}");
                yield return Press(InputAction.Confirm);
                Line($"{name}: a coach set with $1,000: screen {kiosk.CurrentScreen?.GetType().Name}: {Paragraph()}");
                if (!ReferenceEquals(kiosk.CurrentScreen, info) || !info.paragraph.text.Contains("Not enough money")
                    || !info.paragraph.text.Contains("You have $1,000") || info.paragraph.isTextOverflowing)
                    Problem($"{name}: a coach set the wallet can't cover was not refused in full on the info screen");
                yield return Shot("shop-short");
                yield return Press(InputAction.Confirm);
                if (!ReferenceEquals(kiosk.CurrentScreen, x.Detail))
                    Problem($"{name}: the refusal did not go back to the shop (screen {kiosk.CurrentScreen?.GetType().Name})");
                yield return Press(InputAction.Cancel);

                inventory.SetMoney(10_000_000);
                yield return To(Runtime.RuntimeDesk.ShopId);
                yield return Press(InputAction.Confirm);
                var firstRow = x.Detail.Shown().FirstOrDefault();
                Line($"{name}: locomotive shop, first row {firstRow}; info {Text(x, l => l.Info)}");
                yield return Press(InputAction.Confirm);
                Line($"{name}: buying: confirm screen '{ask.title.text}' / '{ask.infoLine.text}' (overflowing {ask.infoLine.isTextOverflowing})");
                if (!ReferenceEquals(kiosk.CurrentScreen, ask))
                    Problem($"{name}: buying a locomotive did not ask first");
                yield return Press(InputAction.Confirm);
                var spent = 10_000_000 - inventory.PlayerMoney;
                Line($"{name}: bought: screen {kiosk.CurrentScreen?.GetType().Name}: {Paragraph()}; ${spent:N0} spent");
                if (!ReferenceEquals(kiosk.CurrentScreen, info) || info.paragraph.isTextOverflowing
                    || !(info.paragraph.text.Contains(" paid. You have $") && spent > 0 || info.paragraph.text.Contains("Nothing was paid") && spent < 1))
                    Problem($"{name}: the purchase's result was not shown in full on the info screen, as paid");
                yield return Shot("shop-bought");
                yield return Press(InputAction.Confirm);
                if (!ReferenceEquals(kiosk.CurrentScreen, x.Detail))
                    Problem($"{name}: the purchase's result did not go back to the shop (screen {kiosk.CurrentScreen?.GetType().Name})");
                yield return Press(InputAction.Cancel);
                inventory.SetMoney(cash);
                if (!ReferenceEquals(kiosk.CurrentScreen, x.List))
                    Problem($"{name}: cancel from a shop did not go back to the list");
            }
            // 3. The first offer, taken at level 2 through the confirm screen. The offers come after
            //    the shops and the player's contracts (D22).
            var first = x.List.Entries.ToList().FindIndex(e => e.IsOffer && Runtime.RuntimeDesk.ShopOf(e.Id) == null);
            var hasOffer = first >= 0;
            ContractView taken = null;
            if (hasOffer)
            {
                var offer = x.List.Entries[first];
                var from = x.List.IndexOfFirstDisplayedEntry + x.List.CurrentSelection;
                if (first != from)
                    yield return Press(first > from ? InputAction.Down : InputAction.Up, Math.Abs(first - from));
                yield return Press(InputAction.Confirm);
                Line($"{name}: offer '{x.Detail.Contract?.Short}': {string.Join("; ", x.Detail.Shown())}");
                if (!ReferenceEquals(kiosk.CurrentScreen, x.Detail))
                    Problem($"{name}: confirm on an offer opened {kiosk.CurrentScreen?.GetType().Name}");
                yield return Shot("offer");
                yield return Press(InputAction.Down);
                yield return Press(InputAction.Confirm);
                Line($"{name}: confirm screen: {kiosk.CurrentScreen?.GetType().Name}: '{ask.title.text}' / '{ask.infoLine.text}' (overflowing {ask.infoLine.isTextOverflowing})");
                if (!ReferenceEquals(kiosk.CurrentScreen, ask))
                    Problem($"{name}: taking did not ask first");
                if (ask.infoLine.isTextOverflowing || !ask.infoLine.text.Contains("06:00"))
                    Problem($"{name}: taking a contract did not say, in full, when its cars come");
                yield return Shot("confirm");
                // Cancel first: back on the offer, nothing taken.
                yield return Press(InputAction.Cancel);
                if (!ReferenceEquals(kiosk.CurrentScreen, x.Detail) || KioskMenu.Desk.Mine().Any(c => c.Id == offer.Id))
                    Problem($"{name}: cancel on the confirm screen did not go back untaken");
                yield return Press(InputAction.Confirm);
                yield return Press(InputAction.Confirm);
                taken = KioskMenu.Desk.Mine().FirstOrDefault(c => c.Id == offer.Id);
                Line($"{name}: after confirm: screen {kiosk.CurrentScreen?.GetType().Name}; taken {(taken == null ? "no" : $"level {taken.Level} asking {taken.RequestedLevel}")}");
                Line($"{name}: list now: {string.Join("; ", x.List.Shown())}");
                if (taken == null || taken.RequestedLevel != 2 || !ReferenceEquals(kiosk.CurrentScreen, x.List))
                    Problem($"{name}: taking at level 2 did not end on the list with the contract taken at level 2");
                yield return Shot("taken");
            }
            else
            {
                Line($"{name}: nothing on offer here; the list holds the player's {KioskMenu.Desk.Mine().Count} contract(s)");
                if (KioskMenu.Desk.Mine().Count == 0)
                {
                    Problem($"{name}: no offers and no contracts in the list");
                    yield return Press(InputAction.Cancel);
                    yield break;
                }
            }

            // 4. The player's newest contract: the one just taken here, else the last of theirs (the
            //    list holds the shops, then the player's contracts, then every offer: D22). One just
            //    taken here has not started, so it can't be raised yet (D16): its first row lowers it
            //    to level 1. Otherwise it is ended, and is offered again where the desk offers it.
            var listed = x.List.Entries.ToList();
            var target = taken != null ? listed.FindIndex(e => !e.IsOffer && e.Id == taken.Id)
                : listed.FindLastIndex(e => !e.IsOffer && Runtime.RuntimeDesk.ShopOf(e.Id) == null);
            var at = x.List.IndexOfFirstDisplayedEntry + x.List.CurrentSelection;
            if (target >= 0 && target != at)
                yield return Press(target > at ? InputAction.Down : InputAction.Up, Math.Abs(target - at));
            var last = x.List.IndexOfFirstDisplayedEntry + x.List.CurrentSelection;
            Line($"{name}: the player's newest contract is entry {target}; selected entry {last} of {x.List.Entries.Count}: {x.List.Entries[last].Short}");
            yield return Press(InputAction.Confirm);
            var own = x.Detail.Contract;
            Line($"{name}: own contract '{own?.Short}': title {Text(x, l => l.Title)}; {string.Join("; ", x.Detail.Shown())}; info {Text(x, l => l.Info)}");
            if (own != null && !own.IsOffer && own.Level < 1 && !Text(x, l => l.Info).Contains("Starts at midnight"))
                Problem($"{name}: a contract not yet started does not say when it starts and its cars come");
            yield return Shot("mine");
            if (own == null || own.IsOffer)
                Problem($"{name}: the last entry is not the player's contract");
            else if (taken != null)
            {
                if (x.Detail.Shown().Any(r => r.StartsWith("Raise", StringComparison.Ordinal)))
                    Problem($"{name}: a contract not yet started offers a raise");
                yield return Press(InputAction.Confirm);   // "Lower to level 1"
                yield return Press(InputAction.Confirm);
                Line($"{name}: after lowering: asking {taken.RequestedLevel}; list: {string.Join("; ", x.List.Shown())}");
                if (taken.RequestedLevel != 1)
                    Problem($"{name}: lowering did not ask for level 1");
            }
            else
            {
                yield return Press(InputAction.Up, 2);     // past the last row (the quick-delivery bonus) to "End contract"
                yield return Press(InputAction.Confirm);
                Line($"{name}: confirm screen: '{ask.title.text}' / '{ask.infoLine.text}'");
                yield return Shot("end");
                yield return Press(InputAction.Confirm);
                var gone = !KioskMenu.Desk.Mine().Contains(own);
                var again = x.List.Entries.Any(e => e.IsOffer && e.Id == own.Id);
                var offeredHere = KioskMenu.Desk.Offers(x.List.Place).Any(o => o.Id == own.Id);
                Line($"{name}: after ending: {(gone ? "gone" : "still held")}; offered here again: {again} (the desk offers it here: {offeredHere}); screen {kiosk.CurrentScreen?.GetType().Name}; list: {string.Join("; ", x.List.Shown())}");
                if (!gone || again != offeredHere || !ReferenceEquals(kiosk.CurrentScreen, x.List))
                    Problem($"{name}: ending did not give the contract up and list it as the desk offers it");
                yield return Shot("ended");
            }

            // 5. Back to the main menu; the stock entries still answer (Stats is the fourth).
            yield return Press(InputAction.Cancel);
            Line($"{name}: cancel from the list: {kiosk.CurrentScreen?.GetType().Name}; selection {main.CurrentSelection}");
            if (!ReferenceEquals(kiosk.CurrentScreen, main))
                Problem($"{name}: cancel from the list did not return to the main menu");
            yield return Press(InputAction.Down, 3);
            yield return Press(InputAction.Confirm);
            Line($"{name}: down x3, confirm: {kiosk.CurrentScreen?.GetType().Name}");
            if (!(kiosk.CurrentScreen is CareerManagerStatsScreen))
                Problem($"{name}: Stats did not open from the fourth entry");
            yield return Press(InputAction.Cancel);
            if (!(kiosk.CurrentScreen is CareerManagerMainScreen))
                Problem($"{name}: cancel from Stats did not return to the main menu");
            // A contract taken here stays the player's, to be listed (and ended) at the next kiosk.
            Line($"{name}: done; the player's contracts: {string.Join(", ", KioskMenu.Desk.Mine().Select(c => c.Short))}");
        }

        private static string Text(KioskMenu.Extension x, Func<KioskLines, TMPro.TextMeshPro> pick)
        {
            var lines = Traverse(x.List);
            return lines == null ? "?" : pick(lines).text;
        }

        private static KioskLines Traverse(ContractsScreen s) => HarmonyLib.Traverse.Create(s).Field("lines").GetValue<KioskLines>();

        private IEnumerator Capture(string file, Vector3 eye, Vector3 target)
        {
            var cam = PlayerManager.PlayerCamera;
            if (cam == null)
            {
                Problem("no player camera");
                yield break;
            }
            var t = cam.transform;
            var parent = t.parent;
            var lp = t.localPosition;
            var lr = t.localRotation;
            var fov = cam.fieldOfView;
            var rot = Quaternion.LookRotation(target - eye, Vector3.up);
            Camera.CameraCallback hold = c =>
            {
                if (c == cam)
                    c.transform.SetPositionAndRotation(eye, rot);
            };
            t.SetParent(null, true);
            cam.fieldOfView = 50f;
            Camera.onPreCull += hold;
            for (var start = Time.unscaledTime; Time.unscaledTime - start < 1.5f;)
            {
                t.SetPositionAndRotation(eye, rot);
                yield return null;
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(folder, file + ".png"));
            for (var start = Time.unscaledTime; Time.unscaledTime - start < 1f;)
            {
                t.SetPositionAndRotation(eye, rot);
                yield return null;
            }
            Camera.onPreCull -= hold;
            t.SetParent(parent, false);
            t.localPosition = lp;
            t.localRotation = lr;
            cam.fieldOfView = fov;
            Line($"view {file}.png");
        }
    }
}
