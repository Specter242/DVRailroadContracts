using System;

namespace Contracts.Core.Shop
{
    /// <summary>
    /// The kiosk's offer for a vehicle the player bought (owner, 2026-11-07, on clearing bought
    /// trains: "You should be re-railing them or selling them"): half what was paid for it, less
    /// what its repairs and refills cost now (the career manager's Owned Vehicles screen), and
    /// never below a tenth of what was paid, its scrap value, so a wreck still fetches something.
    /// </summary>
    public static class VehicleSale
    {
        /// <summary>The share of what was paid that a vehicle in good order fetches.</summary>
        public const double Share = 0.5;

        /// <summary>The share of what was paid that any vehicle fetches: its scrap value.</summary>
        public const double Scrap = 0.1;

        /// <summary>The offer in whole dollars for vehicles bought for <paramref name="paid"/> whose repairs and refills cost <paramref name="owed"/> now.</summary>
        public static double Offer(double paid, double owed)
        {
            paid = Math.Max(0.0, paid);
            owed = Math.Max(0.0, owed);
            return Math.Round(Math.Max(Scrap * paid, Share * paid - owed), MidpointRounding.AwayFromZero);
        }

        /// <summary>Why a vehicle can't be bought now, for the kiosk's info screen: the money falls short, by how much.</summary>
        public static string ShortOf(string name, double price, double money) =>
            $"Not enough money for {name}.\n\nIt costs ${price:N0}. You have ${money:N0}, ${Math.Max(0.0, price - money):N0} short.";

        /// <summary>
        /// The kiosk's warning before the player's only locomotive is sold (jkomodo, Nexus,
        /// 2026-11-07: "now have no train and a big bill"): that it is the last, and whether the
        /// cash after the sale buys the cheapest one in the shop.
        /// </summary>
        public static string LastLocomotive(double cashAfter, double cheapest, string cheapestName) =>
            cashAfter + 0.5 >= cheapest
                ? " It is your only locomotive: without one you can't move a car."
                : $" It is your only locomotive, and you couldn't buy another: you'd have ${cashAfter:N0}, and the cheapest, the {cheapestName}, is ${cheapest:N0}. Without one you can't move a car.";
    }
}
