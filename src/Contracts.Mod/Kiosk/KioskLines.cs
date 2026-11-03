using System.Collections;
using System.Collections.Generic;
using DV.ServicePenalty.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// The kiosk's own text lines that the Contracts screens write to, borrowed from the stock
    /// owned-vehicles list: its title, its rows (an ID and a value column each), the info line
    /// under them, and the scroll arrows. Every kiosk screen clears its lines when it closes,
    /// so screens share them.
    /// </summary>
    internal sealed class KioskLines
    {
        public TextMeshPro Title;
        public TextMeshPro Info;
        public readonly List<(TextMeshPro Id, TextMeshPro Value)> Rows = new List<(TextMeshPro, TextMeshPro)>();
        public GameObject ArrowUp, ArrowDown;

        public static KioskLines From(DisplayScreenSwitcher kiosk)
        {
            var owned = kiosk.GetComponentInChildren<CareerManagerOwnedVehiclesScreen>(true);
            if (owned == null)
                return null;
            var lines = new KioskLines
            {
                Title = owned.titleText,
                Info = owned.pressPrintInfo,
                ArrowUp = owned.scrollUpArrow,
                ArrowDown = owned.scrollDownArrow,
            };
            if (Traverse.Create(owned).Field("vehicleEntries").GetValue() is IList entries)
                foreach (var entry in entries)
                {
                    var e = Traverse.Create(entry);
                    var id = e.Field("id").GetValue<TextMeshPro>();
                    var value = e.Field("value").GetValue<TextMeshPro>();
                    if (id != null && value != null)
                        lines.Rows.Add((id, value));
                }
            return lines.Title != null && lines.Info != null && lines.Rows.Count > 0 ? lines : null;
        }

        public void ClearRows(int from = 0)
        {
            for (var i = from; i < Rows.Count; i++)
                Rows[i].Id.text = Rows[i].Value.text = string.Empty;
        }

        public void Color(int row, Color color)
        {
            if (row < 0 || row >= Rows.Count)
                return;
            Rows[row].Id.color = color;
            Rows[row].Value.color = color;
        }
    }
}
