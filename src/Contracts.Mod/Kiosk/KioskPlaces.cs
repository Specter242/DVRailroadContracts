using System;
using DV.ServicePenalty.UI;
using UnityEngine;

namespace Contracts.Mod.Kiosk
{
    /// <summary>
    /// Which place a kiosk serves. Most kiosks stand in an office under the game content's
    /// Offices/Offices_w3: a stock station's office, or an interchange office that the
    /// interchange mod adds ("[interchange office] MF West"). A stock office stands at its
    /// station controller's anchor. The Military Base's kiosk stands beside its station's
    /// anchor instead (Offices/MilitaryBase_office_anchor), so a kiosk outside Offices_w3
    /// serves the station nearest to it.
    /// </summary>
    internal static class KioskPlaces
    {
        public const string InterchangePrefix = "[interchange office] ";
        private const float StationReach = 60f;

        public static KioskPlace Of(DisplayScreenSwitcher kiosk)
        {
            if (kiosk == null)
                return null;
            var office = kiosk.transform;
            while (office.parent != null && office.parent.name != "Offices_w3")
                office = office.parent;
            if (office.parent == null)
                office = kiosk.transform;
            else if (office.name.StartsWith(InterchangePrefix, StringComparison.Ordinal))
            {
                var name = office.name.Substring(InterchangePrefix.Length);
                return new KioskPlace { Id = name, IsInterchange = true, Name = Runtime.InterchangeYards.CodeOf(name) };
            }
            StationController best = null;
            var bestSqr = StationReach * StationReach;
            foreach (var station in StationController.allStations ?? new System.Collections.Generic.List<StationController>())
            {
                if (station == null || station.stationInfo == null)
                    continue;
                var d = (station.transform.position - office.position).sqrMagnitude;
                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = station;
                }
            }
            return best == null ? null : new KioskPlace { Id = best.stationInfo.YardID, Name = best.stationInfo.YardID };
        }

        /// <summary>Whether the interchange mod built any office this load.</summary>
        public static bool AnyInterchangeOffice()
        {
            var offices = GameObject.Find("Offices/Offices_w3");
            if (offices == null)
                return false;
            foreach (Transform child in offices.transform)
                if (child.name.StartsWith(InterchangePrefix, StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
