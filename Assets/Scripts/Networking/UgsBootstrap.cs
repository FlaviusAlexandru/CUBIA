using System.Threading.Tasks;

namespace DataViz.Networking
{
    /// <summary>
    /// STUB — owner: session/state agent. Initialises Unity Gaming Services and signs in
    /// anonymously, once. Shared by SessionService (sessions/Relay) and VoiceService (Vivox).
    /// Must be safe to call concurrently and repeatedly; must support Multiplayer Play Mode
    /// virtual players (use a distinct authentication profile per MPPM player).
    /// </summary>
    public static class UgsBootstrap
    {
        public static bool IsSignedIn { get; private set; }

        /// <summary>Local player's UGS player id once signed in, else empty.</summary>
        public static string PlayerId { get; private set; } = "";

        public static Task EnsureSignedInAsync() => Task.CompletedTask;
    }
}
