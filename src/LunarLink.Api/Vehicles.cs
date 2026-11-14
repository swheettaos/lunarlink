using System;
using System.Text;
using MoonCleaners;
using Steamworks;
using UnityEngine;

namespace LunarLink
{
    // Owned vehicles (for host tools: give a leaving or banned player's vehicles back to the garage and refund
    // them). Only when asked.
    //   /vehicles                         every owned vehicle: storage id, owner, garage, vehicle type, out in the world or
    //                                     stored, and its price from the garage offers
    //   /vehicles/remove?storage=&refund=1   EXPERIMENT: take one vehicle away (out of the world and off the owned list),
    //                                     and with refund=1 put its garage price back into the team money
    static class Vehicles
    {
        static double PriceOf(MoonWorld w, int vehicle)
        {
            try { foreach (var o in MoonWorld.GarageOffers) if (o.vehicle == vehicle) return Il2CppSystem.Decimal.ToDouble(o.price); } catch { }
            return -1;
        }

        public static string List(MoonWorld w)
        {
            var sb = new StringBuilder("{\"ok\":true,\"owned\":[");
            int n = 0;
            foreach (var kv in w.ownedVehicles)
            {
                var v = kv.Value; if (n++ > 0) sb.Append(',');
                bool spawned = false; try { spawned = w.garageVehicles.ContainsKey(kv.Key); } catch { }
                sb.Append("{\"storage\":").Append(J.S(kv.Key)).Append(",\"owner\":").Append(J.S(v.owner)).Append(",\"ownerName\":").Append(J.S(v.ownerName))
                  .Append(",\"garage\":").Append(v.garage).Append(",\"vehicle\":").Append(v.vehicle).Append(",\"inWorld\":").Append(v.inWorld ? "true" : "false")
                  .Append(",\"spawned\":").Append(spawned ? "true" : "false").Append(",\"price\":").Append(J.F(PriceOf(w, v.vehicle))).Append('}');
            }
            sb.Append("],\"offers\":[");
            int k = 0;
            try { foreach (var o in MoonWorld.GarageOffers) { if (k++ > 0) sb.Append(','); sb.Append("{\"vehicle\":").Append(o.vehicle).Append(",\"price\":").Append(J.F(Il2CppSystem.Decimal.ToDouble(o.price))).Append(",\"summary\":").Append(J.S(o.summary)).Append('}'); } } catch { }
            double money = 0; try { money = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance); } catch { }
            return sb.Append("],\"money\":").Append(J.F(money)).Append(",\"revision\":").Append(w.OwnedVehiclesRevision).Append('}').ToString();
        }

        public static string Remove(MoonWorld w, string storage, bool refund)
        {
            if (string.IsNullOrEmpty(storage) || !w.ownedVehicles.ContainsKey(storage)) return "{\"ok\":false,\"reason\":\"no owned vehicle with that storage id\"}";
            var v = w.ownedVehicles.get_Item(storage); double price = PriceOf(w, v.vehicle);
            double before = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance);
            bool despawned = false;
            try
            {
                if (w.garageVehicles.ContainsKey(storage))
                {
                    var pv = w.garageVehicles.get_Item(storage);
                    if (pv != null) UnityEngine.Object.Destroy(pv.gameObject);
                    w.garageVehicles.Remove(storage); despawned = true;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("vehicle despawn: " + e.Message); }
            w.ownedVehicles.Remove(storage);
            try { w.OwnedVehiclesRevision = w.OwnedVehiclesRevision + 1; } catch { }
            try { w.GarageChanged?.Invoke(v.garage); } catch { }
            if (refund && price > 0)
            {
                var me = SteamUser.GetSteamID().m_SteamID.ToString();
                w.wallets.set_Item(me, new Il2CppSystem.Decimal(Il2CppSystem.Decimal.ToDouble(w.wallets.Balance) + price));
            }
            double after = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance);
            return "{\"ok\":true,\"despawned\":" + (despawned ? "true" : "false") + ",\"price\":" + J.F(price) + ",\"moneyBefore\":" + J.F(before) + ",\"moneyAfter\":" + J.F(after) + ",\"owner\":" + J.S(v.owner) + "}";
        }
    }
}
