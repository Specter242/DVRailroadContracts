using System.Collections.Generic;
using System.Linq;
using Contracts.Core.Shop;
using DV;
using DV.Common;
using DV.ThingTypes;
using DV.UserManagement;
using DV.Utils;
using Newtonsoft.Json.Linq;

namespace Contracts.Mod.Runtime
{
    /// <summary>
    /// A career's starting locomotive (Core Starters), as its row on the new-career screen chose it
    /// (Menu/StarterRow; owner, 2026-11-23): kept with the career, in its session's data
    /// (sessionData.json, <see cref="Key"/>), by name. A career begun before the row has none, and
    /// starts with the DE2, as every career did.
    /// </summary>
    internal static class StarterChoice
    {
        internal const string Key = "Contracts_StartingLocomotive";

        /// <summary>Whether the Jk3 mod's locomotive is loaded (by the custom car loader).</summary>
        internal static bool Jk3Installed() =>
            Globals.G?.Types?.carTypes != null &&
            Globals.G.Types.carTypes.Where(t => t != null && t.liveries != null).SelectMany(t => t.liveries)
                .Any(l => l != null && l.id == Starters.Jk3Livery && l.prefab != null);

        /// <summary>The choices offered now: the game's own, and the JK3 when its mod is loaded.</summary>
        internal static IReadOnlyList<StarterLocomotive> Offered() => Starters.Offered(Jk3Installed());

        /// <summary>The choice a career keeps; the default when it has none, or one no longer offered.</summary>
        internal static StarterLocomotive Of(IGameSession session) =>
            Starters.Valid(Saved(session), Offered());

        /// <summary>The choice as the career saved it, if it has one.</summary>
        internal static string Saved(IGameSession session) =>
            session?.GameData?[Key] is JValue value && value.Type == JTokenType.String ? (string)value : null;

        internal static void Keep(IGameSession session, StarterLocomotive starter)
        {
            if (session?.GameData != null)
                session.GameData[Key] = Starters.Name(starter);
        }

        /// <summary>The career being played's choice.</summary>
        internal static StarterLocomotive Current() =>
            Of(SingletonBehaviour<UserManager>.Instance?.CurrentUser?.CurrentSession);

        /// <summary>The cars a starter is delivered as: the game's own by name, the JK3 as the shop sells it (with its tender); null if not loaded.</summary>
        internal static List<TrainCarLivery> Cars(StarterLocomotive starter) =>
            starter == StarterLocomotive.JK3
                ? LocoShop.Catalogue(LocoShop.Kind.Locomotives).FirstOrDefault(i => i.Cars.Count > 0 && i.Cars[0].id == Starters.Jk3Livery)?.Cars
                : LocoShop.StockLocomotive(Starters.Name(starter));
    }
}
