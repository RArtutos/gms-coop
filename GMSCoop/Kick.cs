using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Host: Ctrl+F6 shows a numbered list of the players in the overlay; pressing a number (1-9) removes
    /// that player. The player gets a Bye with the reason (its PC ends the session and shows it); a moment
    /// later the host drops the connection (the same delay Net uses for refused peers, so the Bye arrives
    /// first). Ctrl+F6 again or Esc closes the list. While it is open the game gets no input (the number
    /// keys would also pick tool slots, Esc would open the pause menu).
    /// </summary>
    internal static class Kick
    {
        private const string Reason = "el host te sacó de la partida";
        private static bool _open;

        /// <summary>The list is shown: game input is paused (FocusInput), so 1-9 / Esc reach only the list.</summary>
        internal static bool Open => _open;
        private static readonly List<ulong> Listed = new List<ulong>();
        private static readonly List<KeyValuePair<ulong, float>> Pending = new List<KeyValuePair<ulong, float>>();

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            for (int i = Pending.Count - 1; i >= 0; i--)
            {
                if (now < Pending[i].Value) continue;
                ulong peer = Pending[i].Key;
                Pending.RemoveAt(i);
                if (Net.Active && Net.IsHost && Net.Peers.Contains(peer)) Net.OnPeerLeft(peer);
            }

            if (!Net.Active || !Net.IsHost) { _open = false; return; }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Input.GetKeyDown(KeyCode.F6))
            {
                _open = !_open;
                Listed.Clear();
                if (_open) Listed.AddRange(Net.Peers);
                return;
            }
            if (!_open) return;
            if (Input.GetKeyDown(KeyCode.Escape) || Listed.Count == 0) { _open = false; return; }
            for (int n = 1; n <= 9 && n <= Listed.Count; n++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha0 + n) && !Input.GetKeyDown(KeyCode.Keypad0 + n)) continue;
                KickPeer(Listed[n - 1]);
                _open = false;
                return;
            }
        }

        private static void KickPeer(ulong peer)
        {
            if (!Net.Peers.Contains(peer) || Net.Transport == null) return;
            string name = Net.NameOf(peer);
            var ms = Net.Writer(Msg.Bye);
            new BinaryWriter(ms).Write(Reason);
            Net.Transport.Send(peer, ms.ToArray(), true);
            Pending.Add(new KeyValuePair<ulong, float>(peer, Time.unscaledTime + 1.5f));
            Plugin.Instance.SetStatus("Sacaste a " + name + " de la partida");
        }

        internal static void AppendOverlay(System.Text.StringBuilder sb)
        {
            if (!_open) return;
            sb.Append("\n<color=#FFE14C><b>Sacar a un jugador</b> (pulsa su número; Esc o Ctrl+F6 cancela; el juego espera):</color>");
            for (int i = 0; i < Listed.Count && i < 9; i++)
                sb.Append("\n  <color=#FFE14C>[").Append(i + 1).Append("]</color> ").Append(Net.NameOf(Listed[i]));
        }
    }
}
