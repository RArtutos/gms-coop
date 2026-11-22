using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Simple humanoid built from primitives, with procedural animation:
    /// idle sway, walk/run limb swing scaled by speed, crouch/jump poses, the held tool with a
    /// "working" arm swing, a carry pose, and a seated pose while driving.
    /// The game is first person and ships no usable full-body player model.
    /// Root pivot is at the feet.
    /// </summary>
    internal sealed class Avatar
    {
        internal readonly GameObject Root;
        private readonly Transform _body, _head, _armL, _armR, _legL, _legR;
        private readonly TextMesh _label;
        private string _name; // TextMesh.text allocates a new string on every read

        private const float TwoPi = Mathf.PI * 2f;
        private float _phase;
        private float _speed;      // smoothed horizontal speed, m/s
        private bool _driving;
        private float _crouch, _air, _use, _usePhase; // 0..1 blends / tool swing phase
        private byte _tool;
        private GameObject _toolRoot;
        private HumanBody _human;      // the game's own worker model, when found (HumanBody.cs)
        private IAvatarModel _model;   // a glTF character the player picked (AvatarModels / GltfAvatar)
        private string _character = AvatarModels.GameWorker;
        private float _nextHumanTry;
        private Color _color = Color.white;

        private static Material _matShirt, _matJeans, _matSkin, _matHelmet, _matBoots, _matWood, _matMetal, _matYellow, _matRed;

        internal Avatar(string name)
        {
            EnsureMaterials();
            Root = new GameObject("CoopAvatar_" + name);
            Object.DontDestroyOnLoad(Root);

            _body = new GameObject("Body").transform;
            _body.SetParent(Root.transform, false);
            _body.localPosition = new Vector3(0f, 0.95f, 0f); // hips height

            // Torso
            Part(PrimitiveType.Capsule, _body, new Vector3(0f, 0.32f, 0f), new Vector3(0.42f, 0.33f, 0.26f), _matShirt);

            // Head + helmet
            _head = new GameObject("Head").transform;
            _head.SetParent(_body, false);
            _head.localPosition = new Vector3(0f, 0.75f, 0f);
            Part(PrimitiveType.Sphere, _head, Vector3.zero, new Vector3(0.24f, 0.27f, 0.24f), _matSkin);
            Part(PrimitiveType.Sphere, _head, new Vector3(0f, 0.08f, 0f), new Vector3(0.29f, 0.17f, 0.31f), _matHelmet);
            Part(PrimitiveType.Cube, _head, new Vector3(0f, 0.03f, 0.13f), new Vector3(0.26f, 0.025f, 0.1f), _matHelmet); // brim

            // Arms pivot at shoulders
            _armL = Limb("ArmL", _body, new Vector3(-0.27f, 0.58f, 0f), 0.58f, 0.11f, _matShirt, _matSkin);
            _armR = Limb("ArmR", _body, new Vector3(0.27f, 0.58f, 0f), 0.58f, 0.11f, _matShirt, _matSkin);

            // Legs pivot at hips
            _legL = Limb("LegL", _body, new Vector3(-0.11f, 0.02f, 0f), 0.9f, 0.15f, _matJeans, _matBoots);
            _legR = Limb("LegR", _body, new Vector3(0.11f, 0.02f, 0f), 0.9f, 0.15f, _matJeans, _matBoots);

            // Name tag
            var tag = new GameObject("NameTag");
            tag.transform.SetParent(Root.transform, false);
            tag.transform.localPosition = new Vector3(0f, 2.15f, 0f);
            _label = tag.AddComponent<TextMesh>();
            _label.text = _name = name;
            _label.anchor = TextAnchor.MiddleCenter;
            _label.alignment = TextAlignment.Center;
            _label.characterSize = 0.05f;
            _label.fontSize = 48;
            _label.color = Color.white;
            _label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            tag.GetComponent<MeshRenderer>().material = _label.font.material;
        }

        internal void SetName(string name)
        {
            if (name == null || name == _name) return;
            _name = name;
            _label.text = name;
        }

        /// <summary>The player's color (PlayerColors): name tag text.</summary>
        internal void SetColor(Color c)
        {
            _color = c;
            _label.color = c;
            _human?.Tint(c);
            _model?.SetColor(c);
        }

        /// <summary>
        /// The body the player picked in the launcher (name packet): a glTF character id, "worker" (the game's
        /// worker model, default) or "procedural" (the simple body). A change rebuilds the body.
        /// </summary>
        internal void SetCharacter(string id)
        {
            id = AvatarModels.Sanitize(id ?? AvatarModels.GameWorker);
            if (id == _character) return;
            _character = id;
            _model?.Destroy();
            _model = null;
            _human?.Destroy();
            _human = null;
            _body.gameObject.SetActive(true);
            _nextHumanTry = 0f;
            if (_toolRoot != null) AttachTool(_toolRoot.transform);
        }

        /// <summary>Swaps the simple body for the game's humanoid worker as soon as one is available.</summary>
        private void TryUseHuman()
        {
            if (_human != null || _model != null || Time.unscaledTime < _nextHumanTry) return;
            if (_character == AvatarModels.Procedural) return;
            _nextHumanTry = Time.unscaledTime + 1f;
            // A picked glTF character first (it may still be loading: try again next second), then the game worker.
            if (AvatarModels.IsGltf(_character) && !AvatarModels.Failed(_character))
            {
                try
                {
                    _model = AvatarModels.TryCreate(_character);
                    if (_model == null) return; // still loading
                    _model.Root.transform.SetParent(Root.transform, false);
                    _model.SetColor(_color);
                    _body.gameObject.SetActive(false);
                    if (_toolRoot != null) AttachTool(_toolRoot.transform);
                    return;
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning("Avatar: character '" + _character + "' failed, using the game worker: " + ex.Message);
                    _model?.Destroy();
                    _model = null;
                    _character = AvatarModels.GameWorker;
                }
            }
            _nextHumanTry = Time.unscaledTime + 3f;
            try
            {
                _human = HumanBody.Create(Root.transform, _color);
                if (_human == null) return;
                _body.gameObject.SetActive(false); // the simple body stays as the fallback, hidden
                if (_toolRoot != null) AttachTool(_toolRoot.transform);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning("Avatar: humanoid body failed, keeping the simple one: " + ex.Message);
                _human?.Destroy();
                _human = null;
                _body.gameObject.SetActive(true);
                _nextHumanTry = float.MaxValue;
            }
        }

        /// <summary>Held tool in the hand: the humanoid's right hand bone, else the simple body's right arm.</summary>
        private void AttachTool(Transform r)
        {
            Transform hand = _model != null ? _model.RightHand : _human?.RightHand;
            if (hand != null)
            {
                r.SetParent(hand, false);
                r.localPosition = Vector3.zero;
                // Same orientation as on the simple arm: tool shapes hang along the root's down axis.
                r.rotation = Root.transform.rotation;
            }
            else
            {
                r.SetParent(_armR, false);
                r.localPosition = new Vector3(0f, -0.55f, 0.02f);
                r.localRotation = Quaternion.identity;
            }
        }

        /// <summary>
        /// Name tag: faces the camera, keeps a readable size at any distance (grows with distance up to a cap),
        /// hidden beyond 150 m.
        /// </summary>
        private void UpdateNameTag(Transform anchor)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Transform t = _label.transform;
            // Above the head of a model with other proportions (glTF characters); fixed height otherwise.
            float y = anchor != null ? Root.transform.InverseTransformPoint(anchor.position).y + 0.45f : 2.15f;
            if (Mathf.Abs(t.localPosition.y - y) > 0.01f) t.localPosition = new Vector3(0f, y, 0f);
            Vector3 d = t.position - cam.transform.position;
            float dist = d.magnitude;
            bool show = dist < 150f;
            var mr = _label.GetComponent<MeshRenderer>();
            if (mr != null && mr.enabled != show) mr.enabled = show;
            if (!show || dist < 0.01f) return;
            t.rotation = Quaternion.LookRotation(d);
            float s = Mathf.Clamp(dist / 12f, 1f, 6f);
            t.localScale = new Vector3(s, s, s);
        }

        /// <summary>Forget the smoothed speed (after a teleport / snap).</summary>
        internal void ResetMotion()
        {
            _speed = 0f;
            _air = 0f;
        }

        /// <summary>
        /// Called every frame after the root was placed. Animation is driven by the sender's
        /// actual velocity and movement flags (PlayerSync.Flag*), not by position deltas.
        /// </summary>
        internal void Animate(bool driving, Vector3 velocity, byte flags, byte tool, float pitch)
        {
            TryUseHuman();
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            float k = Mathf.Clamp01(dt * 10f);
            bool ladder = !driving && (flags & PlayerSync.FlagLadder) != 0;
            // On a ladder the climbing speed (vertical) drives the limb cycle.
            float moveSpeed = ladder ? Mathf.Abs(velocity.y) : new Vector3(velocity.x, 0f, velocity.z).magnitude;
            _speed = Mathf.Lerp(_speed, Mathf.Min(moveSpeed, 12f), k);
            _driving = driving;

            bool crouch = (flags & PlayerSync.FlagCrouch) != 0;
            bool airborne = (flags & PlayerSync.FlagGrounded) == 0;
            bool sprint = (flags & PlayerSync.FlagSprint) != 0;
            bool usingTool = (flags & PlayerSync.FlagUsingTool) != 0;
            bool carrying = (flags & PlayerSync.FlagCarrying) != 0;
            _crouch = Mathf.MoveTowards(_crouch, crouch && !driving ? 1f : 0f, dt * 4f);
            _air = Mathf.MoveTowards(_air, airborne && !driving ? 1f : 0f, dt * 6f);
            _use = Mathf.MoveTowards(_use, usingTool && !driving ? 1f : 0f, dt * 5f);
            SetTool(driving ? (byte)0 : tool);

            if (_model != null)
            {
                // The glTF character plays its own clips (and the carry / tool arm pose).
                _model.Animate(driving, velocity, flags, _tool, pitch);
                UpdateNameTag(_model.NameAnchor);
                return;
            }

            if (_driving)
            {
                // Seated: thighs forward, arms reaching to the wheel.
                _legL.localRotation = _legR.localRotation = Quaternion.Euler(-85f, 0f, 0f);
                _armL.localRotation = _armR.localRotation = Quaternion.Euler(-55f, 0f, 0f);
                _body.localPosition = new Vector3(0f, 0.5f, 0f);
                _body.localRotation = Quaternion.identity;
                _head.localRotation = Quaternion.identity;
                _human?.Seated();
            }
            else
            {
                // Walk 0..2 m/s, run above ~4 m/s (or when the sender sprints). Cadence and swing grow with speed.
                float moving = Mathf.Clamp01(_speed / 1.2f) * (1f - _air);
                float run = Mathf.Clamp01((_speed - 3f) / 2f);
                if (sprint) run = Mathf.Max(run, Mathf.Clamp01((_speed - 1.5f) / 2f));
                float crouchScale = Mathf.Lerp(1f, 0.6f, _crouch);
                _phase += dt * Mathf.Lerp(5.5f, 10f, run) * crouchScale * moving;
                if (_phase > TwoPi) _phase -= TwoPi; // hours of walking: keep the float small (sin precision)
                float swing = Mathf.Sin(_phase) * Mathf.Lerp(32f, 55f, run) * crouchScale * moving;

                // Crouch bends the thighs forward and lowers the hips; airborne tucks the legs.
                float thigh = -50f * _crouch - 25f * _air;
                _legL.localRotation = Quaternion.Euler(swing + thigh, 0f, 0f);
                _legR.localRotation = Quaternion.Euler(-swing + thigh + 15f * _air, 0f, 0f);

                float armBend = Mathf.Lerp(0f, -25f, run) - 20f * _air;
                Quaternion armL = Quaternion.Euler(-swing * 0.8f + armBend, 0f, 3f + 20f * _air);
                Quaternion armR = Quaternion.Euler(swing * 0.8f + armBend, 0f, -3f - 20f * _air);

                if (ladder)
                {
                    // Climbing: hands up on the rungs, alternating with the legs.
                    armL = Quaternion.Euler(-150f + swing * 0.5f, 0f, 6f);
                    armR = Quaternion.Euler(-150f - swing * 0.5f, 0f, -6f);
                    _legL.localRotation = Quaternion.Euler(-20f - Mathf.Max(0f, swing), 0f, 0f);
                    _legR.localRotation = Quaternion.Euler(-20f - Mathf.Max(0f, -swing), 0f, 0f);
                }
                else if (carrying)
                {
                    // Holding an object in front (the object itself is placed by ObjectSync).
                    armL = Quaternion.Euler(-70f, 0f, 8f);
                    armR = Quaternion.Euler(-70f, 0f, -8f);
                }
                else if (_tool != 0)
                {
                    // Tool in the right hand; shovels are held with both hands.
                    bool twoHanded = _tool == ToolShovel || _tool == ToolSmallShovel;
                    _usePhase += dt * (twoHanded ? 5f : 9f) * _use;
                    if (_usePhase > TwoPi) _usePhase -= TwoPi;
                    float work = Mathf.Sin(_usePhase) * _use;
                    float hold = twoHanded ? -35f : -45f;
                    float rightX = hold + work * (twoHanded ? 40f : 25f) + swing * 0.3f;
                    armR = Quaternion.Euler(rightX, 0f, -6f);
                    if (twoHanded) armL = Quaternion.Euler(rightX - 15f, 0f, 25f);
                }
                _armL.localRotation = armL;
                _armR.localRotation = armR;

                // Body bob while moving, breathing sway when idle; lower when crouching.
                float bob = Mathf.Abs(Mathf.Cos(_phase)) * 0.05f * moving;
                float idleT = Time.unscaledTime % (40f * Mathf.PI); // whole periods of both sways: continuous, small after hours of play
                float breathe = Mathf.Sin(idleT * 1.6f) * 0.008f * (1f - moving);
                _body.localPosition = new Vector3(0f, 0.95f + bob + breathe - 0.35f * _crouch, 0f);
                float lean = Mathf.Lerp(0f, 12f, run) + 18f * _crouch + 10f * _use;
                _body.localRotation = Quaternion.Euler(lean, 0f, 0f);
                float look = Mathf.Clamp(pitch, -60f, 60f) - lean;
                _head.localRotation = Quaternion.Euler(look + Mathf.Sin(idleT * 0.5f) * 3f * (1f - moving), 0f, 0f);

                if (_human != null)
                {
                    bool twoHandedTool = _tool == ToolShovel || _tool == ToolSmallShovel;
                    _human.Pose(Mathf.Sin(_phase) * Mathf.Lerp(1f, 0.6f, _crouch), moving, run, _crouch, _air, ladder, carrying,
                                _tool != 0 && !carrying && !ladder, twoHandedTool, Mathf.Sin(_usePhase) * _use, pitch);
                }
            }

            UpdateNameTag(null);
        }

        // ---------- held tool (simple primitive shapes) ----------

        // PlayerTool.EPlayerToolType values.
        private const byte ToolDrill = 1, ToolTorch = 3, ToolScrewdriver = 4, ToolCrowbar = 5,
                           ToolHammer = 6, ToolShovel = 8, ToolSmallShovel = 9,
                           ToolInspection = 10, ToolPilot = 11, ToolDetonator = 12;

        private void SetTool(byte tool)
        {
            if (tool == _tool) return;
            _tool = tool;
            _usePhase = 0f;
            if (_toolRoot != null) Object.Destroy(_toolRoot);
            _toolRoot = null;
            if (tool == 0) return;

            // Child of the right arm, at the hand. Arm-local -Y points from the shoulder to the hand.
            _toolRoot = new GameObject("Tool");
            Transform r = _toolRoot.transform;
            AttachTool(r);
            switch (tool)
            {
                case ToolShovel:
                case ToolSmallShovel:
                {
                    float len = tool == ToolShovel ? 1.15f : 0.55f;
                    // Handle continues forward/down from the hand, blade at its end.
                    var shaft = new GameObject("Shaft").transform;
                    shaft.SetParent(r, false);
                    shaft.localRotation = Quaternion.Euler(-25f, 0f, 0f); // with the arm held forward the handle points forward-down
                    Part(PrimitiveType.Cylinder, shaft, new Vector3(0f, -len * 0.5f + 0.15f, 0f), new Vector3(0.035f, len * 0.5f, 0.035f), _matWood);
                    float bw = tool == ToolShovel ? 0.24f : 0.16f;
                    Part(PrimitiveType.Cube, shaft, new Vector3(0f, -len + 0.05f, 0.02f), new Vector3(bw, bw * 1.2f, 0.02f), _matMetal);
                    break;
                }
                case ToolDrill:
                    Part(PrimitiveType.Cube, r, new Vector3(0f, -0.05f, 0.08f), new Vector3(0.07f, 0.16f, 0.2f), _matYellow);
                    Part(PrimitiveType.Cylinder, r, new Vector3(0f, 0f, 0.24f), new Vector3(0.02f, 0.07f, 0.02f), _matMetal, Quaternion.Euler(90f, 0f, 0f));
                    break;
                case ToolDetonator:
                case ToolInspection:
                case ToolPilot:
                    Part(PrimitiveType.Cube, r, new Vector3(0f, -0.03f, 0.05f), new Vector3(0.09f, 0.14f, 0.05f), tool == ToolDetonator ? _matRed : _matYellow);
                    break;
                case ToolTorch:
                    Part(PrimitiveType.Cylinder, r, new Vector3(0f, 0f, 0.1f), new Vector3(0.04f, 0.1f, 0.04f), _matMetal, Quaternion.Euler(90f, 0f, 0f));
                    break;
                case ToolHammer:
                    Part(PrimitiveType.Cylinder, r, new Vector3(0f, -0.12f, 0f), new Vector3(0.025f, 0.16f, 0.025f), _matWood);
                    Part(PrimitiveType.Cube, r, new Vector3(0f, -0.28f, 0.03f), new Vector3(0.05f, 0.05f, 0.14f), _matMetal);
                    break;
                default: // wrench, screwdriver, crowbar, pliers: a bar pointing forward
                {
                    float len = tool == ToolCrowbar ? 0.5f : tool == ToolScrewdriver ? 0.2f : 0.28f;
                    Part(PrimitiveType.Cube, r, new Vector3(0f, -0.02f, len * 0.5f), new Vector3(0.025f, 0.025f, len),
                         tool == ToolCrowbar ? _matRed : _matMetal);
                    break;
                }
            }
        }

        /// <summary>False once the avatar's object was destroyed (Unity null).</summary>
        internal bool Alive => Root != null;

        internal void Destroy()
        {
            _human?.Destroy();
            _model?.Destroy();
            if (Root != null) Object.Destroy(Root);
        }

        // ---------- construction helpers ----------

        private static Transform Limb(string name, Transform parent, Vector3 pivot, float length, float thickness,
                                      Material upper, Material end)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pivot;
            // Hangs down from the pivot.
            Part(PrimitiveType.Capsule, t, new Vector3(0f, -length * 0.42f, 0f), new Vector3(thickness, length * 0.44f, thickness), upper);
            Part(PrimitiveType.Cube, t, new Vector3(0f, -length * 0.92f, end == _matBoots ? 0.04f : 0f),
                 new Vector3(thickness * 1.05f, length * 0.16f, end == _matBoots ? thickness * 1.7f : thickness), end);
            return t;
        }

        private static void Part(PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Material mat,
                                 Quaternion? rot = null)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            if (rot.HasValue) go.transform.localRotation = rot.Value;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        private static void EnsureMaterials()
        {
            // Runtime materials are assets: a scene load's UnloadUnusedAssets destroys them while no avatar
            // uses them (static references do not count). They are marked to survive that; if any is gone
            // anyway, all are rebuilt.
            if (_matShirt != null && _matJeans != null && _matSkin != null && _matHelmet != null && _matBoots != null
                && _matWood != null && _matMetal != null && _matYellow != null && _matRed != null) return;
            _matShirt = Mat(new Color(0.75f, 0.2f, 0.15f));   // red work shirt
            _matJeans = Mat(new Color(0.18f, 0.27f, 0.45f));  // denim
            _matSkin = Mat(new Color(0.87f, 0.68f, 0.55f));
            _matHelmet = Mat(new Color(1f, 0.8f, 0.05f));     // hard hat
            _matBoots = Mat(new Color(0.3f, 0.2f, 0.12f));
            _matWood = Mat(new Color(0.55f, 0.38f, 0.2f));
            _matMetal = Mat(new Color(0.55f, 0.57f, 0.6f));
            _matYellow = Mat(new Color(0.95f, 0.7f, 0.1f));
            _matRed = Mat(new Color(0.7f, 0.1f, 0.08f));
        }

        private static Material Mat(Color c)
        {
            // Borrow the shader from a default primitive so it renders in the game's pipeline.
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var m = new Material(tmp.GetComponent<Renderer>().sharedMaterial) { color = c, hideFlags = HideFlags.DontUnloadUnusedAsset };
            Object.Destroy(tmp);
            return m;
        }
    }
}
