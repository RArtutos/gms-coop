using Steamworks;

namespace GMSCoop
{
    /// <summary>
    /// Steam lobby: host creates a friends-only lobby, friends join through the Steam overlay
    /// (invite), the friends list ("Join game", rich presence) or by launching from an invite
    /// (+connect_lobby on the command line). Uses the Steamworks.NET copy that ships inside
    /// Assembly-CSharp-firstpass.
    /// </summary>
    internal static class SteamLobby
    {
        private static Callback<LobbyCreated_t> _created;
        private static Callback<GameLobbyJoinRequested_t> _joinRequested;
        private static Callback<GameRichPresenceJoinRequested_t> _richJoinRequested;
        private static Callback<LobbyEnter_t> _entered;
        private static Callback<LobbyChatUpdate_t> _chatUpdate;

        internal static CSteamID Lobby;

        internal static void Init()
        {
            _created = Callback<LobbyCreated_t>.Create(OnCreated);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _richJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnRichJoinRequested);
            _entered = Callback<LobbyEnter_t>.Create(OnEntered);
            _chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
            JoinFromConnectString(string.Join(" ", System.Environment.GetCommandLineArgs()));
        }

        /// <summary>
        /// Joins the lobby named in a "+connect_lobby &lt;id&gt;" string: the command line when the game
        /// was launched from an invite, or the rich-presence connect string from "Join game".
        /// </summary>
        private static bool JoinFromConnectString(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(' ');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] != "+connect_lobby") continue;
                if (!ulong.TryParse(parts[i + 1], out ulong lobby) || lobby == 0) return false;
                Join(new CSteamID(lobby));
                return true;
            }
            return false;
        }

        private static void Join(CSteamID lobby)
        {
            if (Net.Active)
            {
                Plugin.Instance.SetStatus("Ya estás en una partida co-op. Sal con F7 para unirte a otra.");
                return;
            }
            Plugin.Instance.SetStatus("Uniéndote a la partida de Steam...");
            SteamMatchmaking.JoinLobby(lobby);
        }

        internal static void Host()
        {
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 4);
        }

        private static void OnCreated(LobbyCreated_t e)
        {
            if (e.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Instance.SetStatus("No se pudo crear la partida de Steam: " + e.m_eResult);
                return;
            }
            if (Net.Active)
            {
                // F8 pressed twice before Steam answered, or a session started meanwhile: keep the first.
                SteamMatchmaking.LeaveLobby(new CSteamID(e.m_ulSteamIDLobby));
                return;
            }
            Lobby = new CSteamID(e.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(Lobby, "gmscoop_host", SteamUser.GetSteamID().ToString());
            // "Join game" from a friend's Steam list / invite launches the game with this command line.
            SteamFriends.SetRichPresence("connect", "+connect_lobby " + Lobby.m_SteamID);
            Net.StartSteamHost();
            // The lobby id is also a join code: friends paste it in the launcher (GMS Co-op.exe), which starts
            // the game with +connect_lobby <id>. Copied so the host can paste it in a chat right away.
            try { UnityEngine.GUIUtility.systemCopyBuffer = Lobby.m_SteamID.ToString(); } catch { }
            Plugin.Log.LogInfo("Steam lobby code: " + Lobby.m_SteamID);
            Plugin.Instance.SetStatus("Partida de Steam creada. Código copiado: pégalo a tus amigos, o invítalos con Shift+Tab. (F7: salir)");
        }

        private static void OnJoinRequested(GameLobbyJoinRequested_t e) => Join(e.m_steamIDLobby);

        private static void OnRichJoinRequested(GameRichPresenceJoinRequested_t e)
        {
            if (!JoinFromConnectString(e.m_rgchConnect))
                Plugin.Log.LogWarning("Steam join request without a lobby: " + e.m_rgchConnect);
        }

        private static void OnEntered(LobbyEnter_t e)
        {
            var lobby = new CSteamID(e.m_ulSteamIDLobby);
            if ((EChatRoomEnterResponse)e.m_EChatRoomEnterResponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Plugin.Instance.SetStatus("No se pudo entrar en la partida de Steam: " + (EChatRoomEnterResponse)e.m_EChatRoomEnterResponse);
                return;
            }
            if (Net.Active)
            {
                // Host entering its own lobby: fine. Any other lobby while in a session: leave it.
                if (lobby != Lobby) SteamMatchmaking.LeaveLobby(lobby);
                return;
            }
            // Our own new lobby, entered before LobbyCreated_t arrived (Steam does not order the two
            // callbacks): OnCreated starts the host. Never treat it as a lobby to join.
            if (SteamMatchmaking.GetLobbyOwner(lobby) == SteamUser.GetSteamID()) return;
            Lobby = lobby;

            string hostStr = SteamMatchmaking.GetLobbyData(Lobby, "gmscoop_host");
            if (!ulong.TryParse(hostStr, out ulong hostId))
            {
                Plugin.Instance.SetStatus("Esa partida de Steam no es de GMS Co-op");
                SteamMatchmaking.LeaveLobby(Lobby);
                Lobby = CSteamID.Nil;
                return;
            }
            Net.StartSteamClient(new CSteamID(hostId));
            Plugin.Instance.SetStatus("Conectando con el host " + SteamFriends.GetFriendPersonaName(new CSteamID(hostId)) + "...");
        }

        private static void OnChatUpdate(LobbyChatUpdate_t e)
        {
            if (e.m_ulSteamIDLobby != Lobby.m_SteamID) return;
            var user = new CSteamID(e.m_ulSteamIDUserChanged);
            var change = (EChatMemberStateChange)e.m_rgfChatMemberStateChange;
            if ((change & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                // Accept now: its P2P request can arrive before the lobby membership reaches us,
                // which would otherwise reject the session and lose its Hello.
                if (Net.Active && Net.IsHost) SteamNetworking.AcceptP2PSessionWithUser(user);
                return; // the peer is registered by its Hello message
            }
            Net.OnPeerLeft(user.m_SteamID);
        }
    }
}
