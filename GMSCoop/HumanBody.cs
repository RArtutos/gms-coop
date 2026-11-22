using System;
using System.Collections.Generic;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Realistic remote-player body: a copy of the game's own humanoid worker (the driver model every vehicle
    /// carries, MachineController.WorkerPerson: a rigged Mecanim humanoid with an Animator). Its game
    /// scripts, colliders and physics are stripped. Its own animator controller only knows the seated driver
    /// pose, so it is animated procedurally in muscle space (HumanPoseHandler): walk, run, crouch, jump,
    /// ladder, carry and tool swing, from the synced velocity and flags. Works on any Mecanim humanoid rig.
    /// When no worker model is found (or the rig is not humanoid), Avatar keeps its procedural body.
    /// </summary>
    internal sealed class HumanBody
    {
        // ---------------- template (one copy, kept across scene loads) ----------------

        private static GameObject _template;
        private static float _nextSearch;
        private static bool _disabled;

        /// <summary>A stripped, inactive copy of a worker model, or null while none was found yet.</summary>
        internal static GameObject Template
        {
            get
            {
                if (_template != null || _disabled) return _template;
                if (Time.unscaledTime < _nextSearch) return null;
                _nextSearch = Time.unscaledTime + 3f;
                try { _template = BuildTemplate(); }
                catch (Exception ex)
                {
                    _disabled = true;
                    Plugin.Log.LogWarning("Avatar: no humanoid model, using the simple body: " + ex.Message);
                }
                return _template;
            }
        }

        private static GameObject BuildTemplate()
        {
            foreach (MachineController m in UnityEngine.Object.FindObjectsOfType<MachineController>())
            {
                GoldDigger.IKControl worker = m != null ? m.WorkerPerson : null;
                if (worker == null) continue;
                var anim = worker.GetComponent<Animator>();
                if (anim == null || anim.avatar == null || !anim.avatar.isHuman || !anim.avatar.isValid) continue;

                var holder = new GameObject("CoopHumanTemplate");
                UnityEngine.Object.DontDestroyOnLoad(holder);
                holder.SetActive(false);
                GameObject copy = UnityEngine.Object.Instantiate(worker.gameObject, holder.transform, false);
                copy.name = "Human";
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.SetActive(true); // active inside the inactive holder: instances start active
                Strip(copy);
                Plugin.Log.LogInfo($"Avatar: using the game's worker model '{worker.gameObject.name}' from {m.gameObject.name} for remote players");
                return holder;
            }
            return null;
        }

        /// <summary>Removes everything but transforms, renderers and the Animator (scripts, physics, audio, lights).</summary>
        private static void Strip(GameObject go)
        {
            // Scripts first (they may require the components below), then joints, colliders, bodies, the rest.
            DestroyAll<MonoBehaviour>(go);
            DestroyAll<Joint>(go);
            DestroyAll<Collider>(go);
            DestroyAll<Rigidbody>(go);
            DestroyAll<AudioSource>(go);
            DestroyAll<Light>(go);
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = true;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true; // bounds follow the procedural pose
            }
        }

        private static void DestroyAll<T>(GameObject go) where T : Component
        {
            foreach (T c in go.GetComponentsInChildren<T>(true))
                try { if (c != null) UnityEngine.Object.DestroyImmediate(c); } catch { }
        }

        // ---------------- instance ----------------

        internal readonly GameObject Model;
        private readonly Animator _anim;
        private readonly HumanPoseHandler _pose;
        private HumanPose _hp;
        private readonly Transform _footL, _footR, _handR;
        private float _groundOffset;

        private static int[] _m;
        private enum M
        {
            Spine, Chest, Head, Neck,
            LUpLegFB, LLowLeg, LFootUD, RUpLegFB, RLowLeg, RFootUD,
            LArmDU, LArmFB, LForearm, RArmDU, RArmFB, RForearm, LShoulder, RShoulder,
            Count,
        }

        private static readonly string[] MuscleNames =
        {
            "Spine Front-Back", "Chest Front-Back", "Head Nod Down-Up", "Neck Nod Down-Up",
            "Left Upper Leg Front-Back", "Left Lower Leg Stretch", "Left Foot Up-Down",
            "Right Upper Leg Front-Back", "Right Lower Leg Stretch", "Right Foot Up-Down",
            "Left Arm Down-Up", "Left Arm Front-Back", "Left Forearm Stretch",
            "Right Arm Down-Up", "Right Arm Front-Back", "Right Forearm Stretch",
            "Left Shoulder Down-Up", "Right Shoulder Down-Up",
        };

        internal Transform RightHand => _handR;

        private HumanBody(GameObject model, Animator anim)
        {
            Model = model;
            _anim = anim;
            _anim.enabled = false; // its controller is the seated driver: the pose is set here instead
            _pose = new HumanPoseHandler(anim.avatar, anim.transform);
            _pose.GetHumanPose(ref _hp);
            _footL = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            _footR = anim.GetBoneTransform(HumanBodyBones.RightFoot);
            _handR = anim.GetBoneTransform(HumanBodyBones.RightHand);
            if (_m == null)
            {
                _m = new int[(int)M.Count];
                string[] all = HumanTrait.MuscleName;
                for (int i = 0; i < _m.Length; i++) _m[i] = Array.IndexOf(all, MuscleNames[i]);
            }
        }

        /// <summary>Copies the template under <paramref name="parent"/>, tinted with the player color; null when not possible.</summary>
        internal static HumanBody Create(Transform parent, Color color)
        {
            GameObject t = Template;
            if (t == null) return null;
            Transform src = t.transform.Find("Human");
            if (src == null) return null;
            GameObject model = UnityEngine.Object.Instantiate(src.gameObject, parent, false);
            model.name = "Human";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            var anim = model.GetComponent<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman) { UnityEngine.Object.Destroy(model); return null; }
            var body = new HumanBody(model, anim);
            body.Tint(color);
            return body;
        }

        /// <summary>Work-clothes materials take some of the player color (skin, hair and eyes keep theirs).</summary>
        internal void Tint(Color color)
        {
            foreach (Renderer r in Model.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.materials; // instanced copies
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material mat = mats[i];
                    if (mat == null || !mat.HasProperty("_Color")) continue;
                    string n = (mat.name + " " + r.name).ToLowerInvariant();
                    bool skin = n.Contains("skin") || n.Contains("face") || n.Contains("head") || n.Contains("hair") ||
                                n.Contains("eye") || n.Contains("hand") || n.Contains("teeth") || n.Contains("body_s");
                    bool clothes = n.Contains("shirt") || n.Contains("jacket") || n.Contains("cloth") || n.Contains("vest") ||
                                   n.Contains("top") || n.Contains("torso") || n.Contains("coat") || n.Contains("uniform") ||
                                   n.Contains("helmet") || n.Contains("hat");
                    if (skin || !clothes) continue;
                    mat.color = Color.Lerp(mat.color, color, 0.55f);
                    changed = true;
                }
                if (changed) r.materials = mats;
            }
        }

        private void Set(M m, float v)
        {
            int i = _m[(int)m];
            if (i >= 0 && i < _hp.muscles.Length) _hp.muscles[i] = Mathf.Clamp(v, -1f, 1f);
        }

        /// <summary>
        /// One frame of procedural motion. Muscle values are normalized (-1..1). swing: -1..1 walk phase,
        /// moving/run/crouch/air/use: 0..1 blends.
        /// </summary>
        internal void Pose(float swing, float moving, float run, float crouch, float air, bool ladder, bool carrying,
                           bool tool, bool twoHanded, float work, float pitch)
        {
            if (_hp.muscles == null || _hp.muscles.Length == 0) return;
            for (int i = 0; i < _hp.muscles.Length; i++) _hp.muscles[i] = 0f;

            float stride = Mathf.Lerp(0.45f, 0.75f, run) * moving;
            float legL = swing * stride, legR = -swing * stride;
            float bendL = Mathf.Max(0f, -swing) * moving * Mathf.Lerp(0.5f, 0.9f, run);
            float bendR = Mathf.Max(0f, swing) * moving * Mathf.Lerp(0.5f, 0.9f, run);

            // Legs: front-back + knee (stretch 1 = straight, -1 = bent); crouch and jump fold them.
            Set(M.LUpLegFB, legL + 0.65f * crouch + 0.45f * air);
            Set(M.RUpLegFB, legR + 0.65f * crouch + 0.25f * air);
            Set(M.LLowLeg, 0.9f - bendL * 1.4f - 1.5f * crouch - 0.9f * air);
            Set(M.RLowLeg, 0.9f - bendR * 1.4f - 1.5f * crouch - 0.6f * air);
            Set(M.LFootUD, 0.1f + 0.3f * crouch);
            Set(M.RFootUD, 0.1f + 0.3f * crouch);

            // Upper body: lean forward when running, crouching or working.
            Set(M.Spine, 0.15f * run + 0.35f * crouch + (tool ? 0.15f * Mathf.Abs(work) : 0f));
            Set(M.Chest, 0.1f * run);
            float look = Mathf.Clamp(-pitch / 70f, -1f, 1f);
            Set(M.Head, look * 0.7f);
            Set(M.Neck, look * 0.3f);

            // Arms: down by the sides, swinging against the legs.
            float armSwing = swing * Mathf.Lerp(0.35f, 0.6f, run) * moving;
            float armL_DU = -0.75f, armR_DU = -0.75f;
            float armL_FB = -armSwing, armR_FB = armSwing;
            float foreL = Mathf.Lerp(0.75f, 0.2f, run), foreR = foreL;

            if (ladder)
            {
                armL_DU = 0.55f + swing * 0.2f; armR_DU = 0.55f - swing * 0.2f;
                armL_FB = armR_FB = 0.5f;
                foreL = foreR = 0.4f;
            }
            else if (carrying)
            {
                // What the player carries is shown by ObjectSync: arms forward, holding it.
                armL_DU = armR_DU = -0.25f;
                armL_FB = armR_FB = 0.85f;
                foreL = foreR = 0.35f;
            }
            else if (tool)
            {
                float hold = twoHanded ? 0.55f : 0.6f;
                armR_FB = hold + work * (twoHanded ? 0.45f : 0.3f) + armSwing * 0.3f;
                armR_DU = -0.35f + work * 0.2f;
                foreR = 0.35f;
                if (twoHanded) { armL_FB = armR_FB * 0.9f; armL_DU = armR_DU; foreL = 0.3f; }
            }
            if (air > 0f) { armL_DU = Mathf.Lerp(armL_DU, -0.3f, air); armR_DU = Mathf.Lerp(armR_DU, -0.3f, air); }

            Set(M.LArmDU, armL_DU); Set(M.RArmDU, armR_DU);
            Set(M.LArmFB, armL_FB); Set(M.RArmFB, armR_FB);
            Set(M.LForearm, foreL); Set(M.RForearm, foreR);

            // Hips: standing height (normalized), lowered by a crouch. Keep the body facing forward.
            _hp.bodyPosition = new Vector3(0f, 1f - 0.3f * crouch, 0f);
            _hp.bodyRotation = Quaternion.identity;
            _pose.SetHumanPose(ref _hp);

            // Feet on the ground whatever the rig's proportions: shift the model so the lowest foot touches y = 0.
            if (_footL != null && _footR != null)
            {
                Transform root = Model.transform.parent;
                float lowest = Mathf.Min(root.InverseTransformPoint(_footL.position).y, root.InverseTransformPoint(_footR.position).y);
                float target = Model.transform.localPosition.y - lowest + 0.08f - 0.15f * air;
                _groundOffset = Mathf.Abs(_groundOffset - target) > 0.5f ? target : Mathf.Lerp(_groundOffset, target, 0.25f);
                Model.transform.localPosition = new Vector3(0f, _groundOffset, 0f);
            }
        }

        /// <summary>Sitting (passenger seat): thighs forward, knees bent, hands on the lap.</summary>
        internal void Seated()
        {
            if (_hp.muscles == null || _hp.muscles.Length == 0) return;
            for (int i = 0; i < _hp.muscles.Length; i++) _hp.muscles[i] = 0f;
            Set(M.LUpLegFB, 0.9f); Set(M.RUpLegFB, 0.9f);
            Set(M.LLowLeg, -0.6f); Set(M.RLowLeg, -0.6f);
            Set(M.LArmDU, -0.55f); Set(M.RArmDU, -0.55f);
            Set(M.LArmFB, 0.45f); Set(M.RArmFB, 0.45f);
            Set(M.LForearm, 0.2f); Set(M.RForearm, 0.2f);
            _hp.bodyPosition = new Vector3(0f, 0.55f, 0f);
            _hp.bodyRotation = Quaternion.identity;
            _pose.SetHumanPose(ref _hp);
            Model.transform.localPosition = Vector3.zero;
        }

        internal void Destroy()
        {
            try { _pose?.Dispose(); } catch { }
            if (Model != null) UnityEngine.Object.Destroy(Model);
        }
    }
}
