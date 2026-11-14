namespace LunarLink
{
    // LunarLink's endpoints, one hot module made of areas: World (what's in the world), Base (is a base working),
    // Build (changing the world, the build tool), Player (the player, camera, messages), Game (the session), Host
    // (crew games), Dev (reading the game's code) and Help (every endpoint, as JSON and as a page). /help and the API tab group the endpoints by area. The fps fixes
    // and timers are a module of their own (LunarLink.Perf), so reloading this never touches them.
    public class ApiModule
    {
        public void Start(string state)
        {
            WorldArea.Start(state); BaseArea.Start(state); BuildArea.Start(state); PlayerArea.Start(state);
            GameArea.Start(state); HostArea.Start(state); DevArea.Start(state); HelpArea.Start(state);
        }

        // the next copy is told it's a reload (the game-start chores don't run twice)
        public string Stop()
        {
            BuildArea.Stop(); PlayerArea.Stop();
            return "reload";
        }

        public void Update() => BuildArea.Update();
        public void LateUpdate() => PlayerArea.LateUpdate();
    }
}
