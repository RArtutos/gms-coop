using System;
using System.Text;

namespace GMSCoop
{
    /// <summary>One entry point for the session extras (saves/backups, host-lost prompt, kick list, newer-world offer).</summary>
    internal static class CoopUi
    {
        private static float _nextError;

        /// <summary>A prompt that reads plain keys (G/M, U/N, 1-9) is shown: FocusInput blocks the game's input meanwhile.</summary>
        internal static bool ModalOpen => CoopSaves.PromptOpen || Kick.Open || CoopSession.PromptOpen;

        internal static void Tick()
        {
            Run(CoopSaves.Tick);
            Run(Kick.Tick);
            Run(CoopSession.Tick);
        }

        internal static void AppendOverlay(StringBuilder sb)
        {
            try
            {
                CoopSaves.AppendOverlay(sb);
                Kick.AppendOverlay(sb);
                CoopSession.AppendOverlay(sb);
            }
            catch (Exception ex) { Log(ex); }
        }

        private static void Run(Action a)
        {
            try { a(); } catch (Exception ex) { Log(ex); }
        }

        private static void Log(Exception ex)
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextError) return;
            _nextError = now + 10f;
            Plugin.Log.LogError("Co-op extras failed: " + ex);
        }
    }
}
