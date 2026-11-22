using System.Linq;
using System.Text;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Ctrl+F12: writes an inventory of humanoid models and animation clips loaded in memory to the log.
    /// Used to pick a body model and animations for remote players.
    /// </summary>
    internal static class DebugDump
    {
        private static readonly string[] Interesting =
            { "idle", "walk", "run", "sprint", "jump", "sit", "drive", "dig", "shovel", "pan", "crouch", "pick", "carry", "talk", "wave" };

        internal static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== GMSCoop asset dump =====");

            // Animators in the scene (active or not) and in loaded assets.
            var animators = Resources.FindObjectsOfTypeAll<Animator>();
            sb.AppendLine($"Animators: {animators.Length}");
            foreach (var a in animators.OrderBy(a => a.name))
            {
                var smr = a.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (smr.Length == 0) continue;
                bool human = a.avatar != null && a.avatar.isHuman;
                string ctrl = a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "-";
                int clips = a.runtimeAnimatorController != null ? a.runtimeAnimatorController.animationClips.Length : 0;
                bool inScene = a.gameObject.scene.IsValid();
                sb.AppendLine($"  ANIM '{Path(a.transform)}' human={human} avatar={(a.avatar != null ? a.avatar.name : "-")} " +
                              $"ctrl={ctrl} clips={clips} skinned={smr.Length} bones={smr.Max(s => s.bones.Length)} inScene={inScene} active={a.gameObject.activeInHierarchy}");
                if (a.runtimeAnimatorController != null)
                    foreach (var c in a.runtimeAnimatorController.animationClips.Select(c => c.name).Distinct())
                        sb.AppendLine($"      clip: {c}");
                if (a.parameters != null && a.isActiveAndEnabled)
                    foreach (var p in a.parameters)
                        sb.AppendLine($"      param: {p.name} ({p.type})");
            }

            // All loaded clips whose name looks like a body animation.
            var allClips = Resources.FindObjectsOfTypeAll<AnimationClip>();
            sb.AppendLine($"AnimationClips loaded: {allClips.Length}");
            foreach (var c in allClips.OrderBy(c => c.name))
            {
                string n = c.name.ToLowerInvariant();
                if (Interesting.Any(n.Contains))
                    sb.AppendLine($"  CLIP '{c.name}' len={c.length:0.00}s humanMotion={c.humanMotion} loop={c.isLooping} legacy={c.legacy}");
            }

            sb.AppendLine("===== end dump =====");
            Plugin.Log.LogInfo(sb.ToString());
            Plugin.Instance.SetStatus("Inventario escrito en el log (Ctrl+F12)");
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
