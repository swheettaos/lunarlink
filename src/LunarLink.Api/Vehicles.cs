using System;
using MoonCleaners;
using Steamworks;

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
        static double PriceOf(int vehicle)
        {
            try { foreach (var o in MoonWorld.GarageOffers) if (o.vehicle == vehicle) return Il2CppSystem.Decimal.ToDouble(o.price); }
            catch (System.Exception caught) { Quiet.Note(caught); }
            return -1;
        }

        public static string List(MoonWorld w)
        {
            var o = new JsonOut().Obj().Bool("ok", true).Arr("owned");
            foreach (var kv in w.ownedVehicles)
            {
                var v = kv.Value;
                bool spawned = false; try { spawned = w.garageVehicles.ContainsKey(kv.Key); } catch (System.Exception caught) { Quiet.Note(caught); }
                o.Obj().Str("storage", kv.Key).Str("owner", v.owner).Str("ownerName", v.ownerName).Num("garage", v.garage).Num("vehicle", v.vehicle)
                 .Bool("inWorld", v.inWorld).Bool("spawned", spawned).Num("price", PriceOf(v.vehicle)).End();
            }
            // the shop's offers, read whole before any is written (a failure leaves no half entry)
            var offers = new System.Collections.Generic.List<(int vehicle, double price, string summary)>();
            try { foreach (var g in MoonWorld.GarageOffers) offers.Add((g.vehicle, Il2CppSystem.Decimal.ToDouble(g.price),
                g.summary)); } catch (System.Exception caught) { Quiet.Note(caught); }
            o.End().Arr("offers");
            foreach (var g in offers) o.Obj().Num("vehicle", g.vehicle).Num("price", g.price).Str("summary", g.summary).End();
            double money = 0; try { money = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance); } catch (System.Exception caught) { Quiet.Note(caught); }
            return o.End().Num("money", money).Num("revision", w.OwnedVehiclesRevision);
        }

        public static string Remove(MoonWorld w, string storage, bool refund)
        {
            if (string.IsNullOrEmpty(storage) || !w.ownedVehicles.ContainsKey(storage)) return Api.Result(false, "no owned vehicle with that storage id");
            var v = w.ownedVehicles.get_Item(storage); double price = PriceOf(v.vehicle);
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
            try { w.OwnedVehiclesRevision = w.OwnedVehiclesRevision + 1; } catch (System.Exception caught) { Quiet.Note(caught); }
            try { w.GarageChanged?.Invoke(v.garage); } catch (System.Exception caught) { Quiet.Note(caught); }
            if (refund && price > 0)
            {
                var me = SteamUser.GetSteamID().m_SteamID.ToString();
                w.wallets.set_Item(me, new Il2CppSystem.Decimal(Il2CppSystem.Decimal.ToDouble(w.wallets.Balance) + price));
            }
            double after = Il2CppSystem.Decimal.ToDouble(w.wallets.Balance);
            return new JsonOut().Obj().Bool("ok", true).Bool("despawned", despawned).Num("price", price).Num("moneyBefore",
                before).Num("moneyAfter", after).Str("owner", v.owner);
        }
    }
}
