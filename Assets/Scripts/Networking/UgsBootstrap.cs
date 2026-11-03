using System;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using UnityEngine;

namespace DataViz.Networking
{
    /// <summary>
    /// Initialises Unity Gaming Services and signs in anonymously, once. Shared by SessionService
    /// (sessions/Relay) and VoiceService (Vivox). Safe to call concurrently and repeatedly: every
    /// caller awaits the same task; a failed attempt is forgotten so the next call retries.
    ///
    /// Each process needs its own authentication profile, otherwise two instances on one PC share
    /// the cached anonymous player id and the session service treats them as the same player.
    /// Profile, in order: "-ugsProfile &lt;name&gt;" on the command line; an MPPM virtual player
    /// (its own profile derived from its clone project path); otherwise the default profile.
    /// </summary>
    public static class UgsBootstrap
    {
        public static bool IsSignedIn { get; private set; }

        /// <summary>Local player's UGS player id once signed in, else empty.</summary>
        public static string PlayerId { get; private set; } = "";

        static Task s_SignIn;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Enter Play Mode without a domain reload keeps statics; start every play session clean.
            IsSignedIn = false;
            PlayerId = "";
            s_SignIn = null;
        }

        public static Task EnsureSignedInAsync()
        {
            if (s_SignIn == null || s_SignIn.IsFaulted || s_SignIn.IsCanceled)
                s_SignIn = SignInAsync();
            return s_SignIn;
        }

        static async Task SignInAsync()
        {
            string profile = ChooseProfile();

            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                var options = new InitializationOptions();
                if (profile != null)
                    options.SetProfile(profile);
                await UnityServices.InitializeAsync(options);
            }
            else
            {
                // Something else initialised UGS first: wait for it, then switch profile while still signed out.
                if (UnityServices.State == ServicesInitializationState.Initializing)
                    await UnityServices.InitializeAsync();
                if (profile != null && !AuthenticationService.Instance.IsSignedIn && AuthenticationService.Instance.Profile != profile)
                    AuthenticationService.Instance.SwitchProfile(profile);
            }

            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();

            PlayerId = AuthenticationService.Instance.PlayerId ?? "";
            IsSignedIn = true;
        }

        /// <summary>Profile name for this process, or null for the default profile.</summary>
        static string ChooseProfile()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-ugsProfile", StringComparison.OrdinalIgnoreCase))
                    return Sanitize(args[i + 1]);
            }

#if UNITY_EDITOR
            // MPPM virtual players run from their own clone of the project (Library/VP/<id>),
            // so the project path tells them apart from the main editor and from each other.
            if (!Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor)
                return "vp" + StableHash(Application.dataPath).ToString("x8");
#endif
            return null;
        }

        // Profiles allow letters, digits, '-' and '_', up to 30 characters.
        static string Sanitize(string raw)
        {
            var chars = new System.Text.StringBuilder();
            foreach (char c in raw ?? "")
            {
                if (chars.Length == 30) break;
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') chars.Append(c);
            }
            return chars.Length > 0 ? chars.ToString() : null;
        }

        // FNV-1a: unlike string.GetHashCode it is the same in every run, so a virtual player keeps its profile.
        static uint StableHash(string s)
        {
            uint hash = 2166136261;
            foreach (char c in s)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }
}
