using System;
using Contracts.Core.Data;

namespace Contracts.Core.Sim
{
    /// <summary>
    /// Pay for one move, in Railroader's shape (operations-design.md, 12):
    /// payScale × (50 + ⌈4√miles⌉ + ⌈tons/4⌉), whole dollars. Empties count
    /// no tons. The amount is written into the waybill when it is created.
    /// </summary>
    public static class Revenue
    {
        public static double Pay(double payScale, double miles, double tons)
        {
            // The small epsilon keeps a value computed as 16.000000000000004 from paying for 17.
            var distance = Math.Ceiling(4.0 * Math.Sqrt(Math.Max(0.0, miles)) - 1e-9);
            var weight = Math.Ceiling(Math.Max(0.0, tons) / 4.0 - 1e-9);
            return Math.Round(payScale * (50.0 + Math.Max(0.0, distance) + Math.Max(0.0, weight)));
        }

        /// <summary>Days of waybill age forgiven for a long move (RR OpsController.CalculateGraceDays).</summary>
        public static int GraceDays(double miles) => miles > 40.0 ? 2 : miles > 20.0 ? 1 : 0;

        /// <summary>Track miles between two destinations, from their billing mileposts.</summary>
        public static double Miles(OpsData data, IDestination origin, IDestination dest) =>
            origin == null || dest == null ? 0.0 : Math.Abs(data.Milepost(origin) - data.Milepost(dest));

        public static double ForMove(OpsData data, IDestination origin, IDestination dest, string loadId)
        {
            var tons = loadId != null && data.Loads.TryGetValue(loadId, out var load) ? load.TonsPerCar : 0.0;
            return Pay(data.Settings.PayScale, Miles(data, origin, dest), tons);
        }
    }
}
