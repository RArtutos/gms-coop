using SaveSystem;

namespace GMSCoop
{
    /// <summary>Small helpers about the game's own state.</summary>
    internal static class GameState
    {
        /// <summary>
        /// True while the game is loading or restoring a save. Values the game writes during a load
        /// (money, terrain, tutorial step) come from the save file, not from the player, and must
        /// never be broadcast as changes.
        /// </summary>
        internal static bool IsLoading
        {
            get
            {
                if (!Singleton<CheckPointManagerBase>.IsInstanced()) return true;
                return Singleton<CheckPointManagerBase>.Instance.GetLoadingState() != LoadingState.ReadyToPlay;
            }
        }

        private static int _frame = -1, _scene, _version;
        private static bool _wasLoading = true;

        /// <summary>
        /// Changes whenever the world this PC plays is replaced: a new active scene (also a reload of the
        /// same scene: new handle), the main menu, or the start of a save load (CheckPointManager leaves
        /// ReadyToPlay; streamed areas never do). Anything cached from the old world (game objects,
        /// edits waiting for a tile, pending answers about old objects) must be dropped when it changes.
        /// Evaluated at most once per frame, no allocation.
        /// </summary>
        internal static int WorldVersion
        {
            get
            {
                int frame = UnityEngine.Time.frameCount;
                if (frame == _frame) return _version;
                _frame = frame;
                int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                bool loading = IsLoading;
                if (scene != _scene || (loading && !_wasLoading)) _version++;
                _scene = scene;
                _wasLoading = loading;
                return _version;
            }
        }
    }
}
