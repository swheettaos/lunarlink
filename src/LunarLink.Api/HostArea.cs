namespace LunarLink
{
    // Crew games: what the hosted game publishes to Steam, its name, and the players' vehicles.
    static class HostArea
    {
        internal static void Start(string state)
        {
            Api.Get("/lobby", "What the hosted game publishes to Steam: the crew name and every lobby key", r => Lobby.Data());
            Api.Post("/lobby/modify", "?name= sets the hosted game's name; or ?key=&value= sets one lobby key. Host only", r =>
                r.Query["name"] != null ? Lobby.Name(r.Query["name"]) : Lobby.Set(r.Query["key"], r.Query["value"]));
            Api.Get("/vehicles", "Every owned vehicle: storage id, owner, garage, type, in the world or stored, price", r => Vehicles.List(Api.World));
            Api.Post("/vehicles/remove", "?storage=[&refund=1] : take one owned vehicle away; refund=1 pays its garage price to the team", r =>
                Vehicles.Remove(Api.World, r.Query["storage"], r.Query["refund"] == "1"));
        }

    }
}
