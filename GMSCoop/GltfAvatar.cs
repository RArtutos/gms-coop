using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GMSCoop
{
    // =====================================================================================
    // Loading: a small glTF 2.0 binary (.glb) reader for rigged, animated characters.
    // Reads meshes (positions, normals, UV0, joints/weights, indices), one skin, PBR base color
    // (factor + embedded PNG/JPG), the node tree and animations (TRS channels) into legacy clips.
    // glTF is right-handed, Unity left-handed: X is mirrored (positions, normals, rotations,
    // bind matrices), triangle winding is reversed and V is flipped.
    // =====================================================================================

    /// <summary>A loaded character: an inactive prefab every avatar instantiates.</summary>
    internal sealed class GltfTemplate
    {
        internal AvatarModels.Character Character;
        internal volatile bool Failed;
        internal bool Ready => Prefab != null;
        internal GameObject Prefab;           // inactive holder child "Model"
        internal ParsedGltf Parsed;           // filled on a worker thread, consumed on the main thread
        internal volatile bool ParseDone;
    }

    /// <summary>Keeps the templates and builds them over a few frames on the main thread (never a long hitch).</summary>
    internal sealed class GltfAvatarLoader : MonoBehaviour
    {
        private static GltfAvatarLoader _runner;
        private static readonly Dictionary<string, GltfTemplate> Templates = new Dictionary<string, GltfTemplate>();
        internal static readonly List<GltfAvatar> Live = new List<GltfAvatar>();

        /// <summary>The template of <paramref name="c"/>: starts loading it on the first call.</summary>
        internal static GltfTemplate Get(AvatarModels.Character c)
        {
            if (Templates.TryGetValue(c.Id, out GltfTemplate t)) return t;
            t = new GltfTemplate { Character = c };
            Templates[c.Id] = t;
            EnsureRunner();
            string path = Path.Combine(AvatarModels.Folder, c.File);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { t.Parsed = ParsedGltf.Parse(File.ReadAllBytes(path)); }
                catch (Exception ex)
                {
                    t.Failed = true;
                    MainThread.Run(() => Plugin.Log.LogWarning($"Avatar: character file {c.File} could not be read: {ex.Message}"));
                }
                t.ParseDone = true;
            });
            return t;
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return;
            var go = new GameObject("GMSCoop_AvatarLoader");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _runner = go.AddComponent<GltfAvatarLoader>();
        }

        private void Update()
        {
            foreach (GltfTemplate t in Templates.Values)
            {
                if (t.Ready || t.Failed || !t.ParseDone) continue;
                try
                {
                    if (GltfBuilder.Step(t)) // one chunk of work per frame
                    {
                        t.Parsed = null;
                        Plugin.Log.LogInfo($"Avatar: character '{t.Character.Id}' loaded");
                    }
                }
                catch (Exception ex)
                {
                    t.Failed = true;
                    Plugin.Log.LogWarning($"Avatar: character '{t.Character.Id}' could not be built: {ex.Message}");
                }
                return;
            }
        }

        private void LateUpdate()
        {
            // After the Animation components sampled their clips: carry / tool arm poses on top.
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                GltfAvatar a = Live[i];
                if (a == null || a.Root == null) { Live.RemoveAt(i); continue; }
                try { a.LatePose(); } catch { }
            }
        }
    }

    /// <summary>The raw data of a .glb: JSON plus decoded accessor arrays (thread-safe, no Unity objects).</summary>
    internal sealed class ParsedGltf
    {
        internal JObject Json;
        internal byte[] Bin;
        internal readonly Dictionary<int, float[]> Floats = new Dictionary<int, float[]>();
        internal readonly Dictionary<int, int[]> Ints = new Dictionary<int, int[]>();
        internal readonly Dictionary<int, byte[]> Images = new Dictionary<int, byte[]>();

        internal static ParsedGltf Parse(byte[] d)
        {
            if (d.Length < 20 || BitConverter.ToUInt32(d, 0) != 0x46546C67) throw new InvalidDataException("not a .glb file");
            var p = new ParsedGltf();
            int off = 12;
            while (off + 8 <= d.Length)
            {
                int len = BitConverter.ToInt32(d, off);
                uint type = BitConverter.ToUInt32(d, off + 4);
                if (type == 0x4E4F534A) p.Json = JObject.Parse(System.Text.Encoding.UTF8.GetString(d, off + 8, len));
                else if (type == 0x004E4942) { p.Bin = new byte[len]; Buffer.BlockCopy(d, off + 8, p.Bin, 0, len); }
                off += 8 + len;
            }
            if (p.Json == null || p.Bin == null) throw new InvalidDataException("glb without JSON or binary chunk");

            var accessors = (JArray)p.Json["accessors"];
            for (int i = 0; i < accessors.Count; i++)
            {
                var a = (JObject)accessors[i];
                int ct = (int)a["componentType"];
                bool normalized = a["normalized"] != null && (bool)a["normalized"];
                if (ct == 5126 || normalized) p.Floats[i] = p.ReadFloats(a);
                else p.Ints[i] = p.ReadInts(a);
            }
            var images = p.Json["images"] as JArray;
            if (images != null)
                for (int i = 0; i < images.Count; i++)
                {
                    JToken bv = images[i]["bufferView"];
                    if (bv == null) continue;
                    var view = (JObject)p.Json["bufferViews"][(int)bv];
                    int o = (int?)view["byteOffset"] ?? 0, l = (int)view["byteLength"];
                    var img = new byte[l];
                    Buffer.BlockCopy(p.Bin, o, img, 0, l);
                    p.Images[i] = img;
                }
            return p;
        }

        private static int Components(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                case "MAT4": return 16;
                default: throw new InvalidDataException("accessor type " + type);
            }
        }

        private static int Size(int componentType)
        {
            switch (componentType)
            {
                case 5120: case 5121: return 1;
                case 5122: case 5123: return 2;
                case 5125: case 5126: return 4;
                default: throw new InvalidDataException("component type " + componentType);
            }
        }

        private void Locate(JObject a, out int start, out int stride, out int count, out int comps, out int ct)
        {
            count = (int)a["count"];
            comps = Components((string)a["type"]);
            ct = (int)a["componentType"];
            start = 0; stride = comps * Size(ct);
            JToken bvTok = a["bufferView"];
            if (bvTok == null) { start = -1; return; }
            var view = (JObject)Json["bufferViews"][(int)bvTok];
            start = ((int?)view["byteOffset"] ?? 0) + ((int?)a["byteOffset"] ?? 0);
            int? bs = (int?)view["byteStride"];
            if (bs.HasValue && bs.Value > 0) stride = bs.Value;
        }

        private float[] ReadFloats(JObject a)
        {
            Locate(a, out int start, out int stride, out int count, out int comps, out int ct);
            var r = new float[count * comps];
            if (start < 0) return r;
            int cs = Size(ct);
            for (int i = 0; i < count; i++)
                for (int c = 0; c < comps; c++)
                {
                    int o = start + i * stride + c * cs;
                    float v;
                    switch (ct)
                    {
                        case 5126: v = BitConverter.ToSingle(Bin, o); break;
                        case 5121: v = Bin[o] / 255f; break;
                        case 5123: v = BitConverter.ToUInt16(Bin, o) / 65535f; break;
                        case 5120: v = Math.Max((sbyte)Bin[o] / 127f, -1f); break;
                        case 5122: v = Math.Max(BitConverter.ToInt16(Bin, o) / 32767f, -1f); break;
                        default: v = 0f; break;
                    }
                    r[i * comps + c] = v;
                }
            return r;
        }

        private int[] ReadInts(JObject a)
        {
            Locate(a, out int start, out int stride, out int count, out int comps, out int ct);
            var r = new int[count * comps];
            if (start < 0) return r;
            int cs = Size(ct);
            for (int i = 0; i < count; i++)
                for (int c = 0; c < comps; c++)
                {
                    int o = start + i * stride + c * cs;
                    switch (ct)
                    {
                        case 5121: r[i * comps + c] = Bin[o]; break;
                        case 5123: r[i * comps + c] = BitConverter.ToUInt16(Bin, o); break;
                        case 5125: r[i * comps + c] = (int)BitConverter.ToUInt32(Bin, o); break;
                        case 5120: r[i * comps + c] = (sbyte)Bin[o]; break;
                        case 5122: r[i * comps + c] = BitConverter.ToInt16(Bin, o); break;
                    }
                }
            return r;
        }
    }

    /// <summary>Turns a ParsedGltf into Unity objects in steps: nodes + meshes, then one animation per step.</summary>
    internal static class GltfBuilder
    {
        private sealed class State
        {
            internal GameObject Holder, Model;
            internal Transform[] Nodes;
            internal string[] Paths;
            internal Animation Anim;
            internal int NextAnim;
        }

        private static readonly Dictionary<GltfTemplate, State> States = new Dictionary<GltfTemplate, State>();

        /// <summary>One step; true when the template is complete.</summary>
        internal static bool Step(GltfTemplate t)
        {
            if (!States.TryGetValue(t, out State s))
            {
                s = BuildScene(t);
                States[t] = s;
                return false;
            }
            var anims = t.Parsed.Json["animations"] as JArray;
            if (anims != null && s.NextAnim < anims.Count)
            {
                BuildClip(t.Parsed, s, (JObject)anims[s.NextAnim++]);
                return false;
            }
            States.Remove(t);
            t.Prefab = s.Holder;
            return true;
        }

        private static Vector3 P(float[] a, int i) => new Vector3(-a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);

        private static Quaternion Q(float x, float y, float z, float w) => new Quaternion(x, -y, -z, w);

        private static Matrix4x4 M(float[] a, int i)
        {
            var m = new Matrix4x4();
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                    m[r, c] = a[i * 16 + c * 4 + r]; // glTF is column-major
            // Mirror X on both sides: C * M * C, C = diag(-1, 1, 1, 1).
            var cm = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            return cm * m * cm;
        }

        private static State BuildScene(GltfTemplate t)
        {
            ParsedGltf p = t.Parsed;
            JObject js = p.Json;
            var s = new State();
            s.Holder = new GameObject("GMSCoop_Character_" + t.Character.Id);
            UnityEngine.Object.DontDestroyOnLoad(s.Holder);
            s.Holder.SetActive(false);
            s.Model = new GameObject("Model");
            s.Model.transform.SetParent(s.Holder.transform, false);

            // Node tree.
            var nodes = (JArray)js["nodes"];
            s.Nodes = new Transform[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = (JObject)nodes[i];
                var go = new GameObject((string)n["name"] ?? ("node" + i));
                s.Nodes[i] = go.transform;
            }
            var hasParent = new bool[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = (JObject)nodes[i];
                if (n["children"] is JArray ch)
                    foreach (JToken c in ch) { s.Nodes[(int)c].SetParent(s.Nodes[i], false); hasParent[(int)c] = true; }
            }
            Transform rig = new GameObject("Rig").transform;
            rig.SetParent(s.Model.transform, false);
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!hasParent[i]) s.Nodes[i].SetParent(rig, false);
                var n = (JObject)nodes[i];
                if (n["matrix"] is JArray mat)
                {
                    var arr = new float[16];
                    for (int k = 0; k < 16; k++) arr[k] = (float)mat[k];
                    Matrix4x4 m = M(arr, 0);
                    s.Nodes[i].localPosition = m.GetColumn(3);
                    s.Nodes[i].localRotation = m.rotation;
                    s.Nodes[i].localScale = m.lossyScale;
                    continue;
                }
                if (n["translation"] is JArray tr) s.Nodes[i].localPosition = new Vector3(-(float)tr[0], (float)tr[1], (float)tr[2]);
                if (n["rotation"] is JArray ro) s.Nodes[i].localRotation = Q((float)ro[0], (float)ro[1], (float)ro[2], (float)ro[3]);
                if (n["scale"] is JArray sc) s.Nodes[i].localScale = new Vector3((float)sc[0], (float)sc[1], (float)sc[2]);
            }

            // Materials and textures.
            Material[] mats = BuildMaterials(p, t.Character.Id);

            // Meshes: a skinned mesh goes on its own child of the model root (identity: the skin's bind space is
            // the scene space), a static mesh on its node.
            var skins = js["skins"] as JArray;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = (JObject)nodes[i];
                if (n["mesh"] == null) continue;
                Mesh mesh = BuildMesh(p, (JObject)js["meshes"][(int)n["mesh"]], out Material[] meshMats, mats, out bool skinned);
                if (n["skin"] != null && skins != null && skinned)
                {
                    var skin = (JObject)skins[(int)n["skin"]];
                    var joints = (JArray)skin["joints"];
                    var bones = new Transform[joints.Count];
                    var bind = new Matrix4x4[joints.Count];
                    float[] ibm = skin["inverseBindMatrices"] != null ? p.Floats[(int)skin["inverseBindMatrices"]] : null;
                    for (int j = 0; j < joints.Count; j++)
                    {
                        bones[j] = s.Nodes[(int)joints[j]];
                        bind[j] = ibm != null ? M(ibm, j) : Matrix4x4.identity;
                    }
                    mesh.bindposes = bind;
                    var holder = new GameObject(s.Nodes[i].name + "_skin");
                    holder.transform.SetParent(s.Model.transform, false);
                    var smr = holder.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMesh = mesh;
                    smr.bones = bones;
                    smr.rootBone = bones.Length > 0 ? bones[0] : null;
                    smr.sharedMaterials = meshMats;
                    smr.updateWhenOffscreen = true;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    minY = Mathf.Min(minY, mesh.bounds.min.y);
                    maxY = Mathf.Max(maxY, mesh.bounds.max.y);
                }
                else
                {
                    s.Nodes[i].gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = s.Nodes[i].gameObject.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = meshMats;
                }
            }
            if (minY > maxY) { minY = 0f; maxY = 1.75f; }

            // Paths for the animation curves (relative to the Model, where the Animation component lives).
            s.Paths = new string[nodes.Count];
            for (int i = 0; i < nodes.Count; i++) s.Paths[i] = PathOf(s.Nodes[i], s.Model.transform);
            s.Anim = s.Model.AddComponent<Animation>();
            s.Anim.playAutomatically = false;
            s.Anim.cullingType = AnimationCullingType.AlwaysAnimate;

            // A grown-up's height with the feet at the origin.
            float h = Mathf.Max(0.3f, maxY - minY);
            float scale = 1.75f / h;
            rig.localPosition = Vector3.zero;
            s.Model.transform.localScale = Vector3.one * scale;
            s.Model.transform.localPosition = new Vector3(0f, -minY * scale, 0f);
            return s;
        }

        private static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static Material _baseMat;

        private static Material[] BuildMaterials(ParsedGltf p, string id)
        {
            var list = p.Json["materials"] as JArray;
            if (list == null) return new Material[0];
            if (_baseMat == null)
            {
                // Borrow the default primitive's shader: it renders in the game's pipeline.
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _baseMat = new Material(tmp.GetComponent<Renderer>().sharedMaterial) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                UnityEngine.Object.Destroy(tmp);
            }
            var textures = new Dictionary<int, Texture2D>();
            var result = new Material[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var m = (JObject)list[i];
                var mat = new Material(_baseMat) { name = (string)m["name"] ?? ("mat" + i), hideFlags = HideFlags.DontUnloadUnusedAsset };
                Color color = Color.white;
                JObject pbr = m["pbrMetallicRoughness"] as JObject;
                if (pbr?["baseColorFactor"] is JArray f)
                    color = new Color((float)f[0], (float)f[1], (float)f[2], (float)f[3]).gamma;
                mat.color = color;
                if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
                if (pbr?["baseColorTexture"]?["index"] != null)
                {
                    int texIndex = (int)pbr["baseColorTexture"]["index"];
                    var tex = (JObject)p.Json["textures"][texIndex];
                    int src = (int?)tex["source"] ?? -1;
                    if (src >= 0 && p.Images.TryGetValue(src, out byte[] bytes))
                    {
                        if (!textures.TryGetValue(src, out Texture2D t2))
                        {
                            t2 = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = id + "_tex" + src, hideFlags = HideFlags.DontUnloadUnusedAsset };
                            if (!ImageConversion.LoadImage(t2, bytes, false)) t2 = null;
                            else { t2.wrapMode = TextureWrapMode.Repeat; t2.filterMode = FilterMode.Bilinear; }
                            textures[src] = t2;
                        }
                        if (t2 != null) mat.mainTexture = t2;
                    }
                }
                result[i] = mat;
            }
            return result;
        }

        private static Mesh BuildMesh(ParsedGltf p, JObject jm, out Material[] meshMats, Material[] mats, out bool skinned)
        {
            var prims = (JArray)jm["primitives"];
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>();
            var weights = new List<BoneWeight>();
            var subs = new List<int[]>(); var subMats = new List<Material>();
            skinned = false;
            foreach (JObject pr in prims)
            {
                int mode = (int?)pr["mode"] ?? 4;
                if (mode != 4) continue; // triangles only
                var at = (JObject)pr["attributes"];
                int baseV = pos.Count;
                float[] P0 = p.Floats[(int)at["POSITION"]];
                int vc = P0.Length / 3;
                for (int v = 0; v < vc; v++) pos.Add(P(P0, v));
                if (at["NORMAL"] != null) { float[] N = p.Floats[(int)at["NORMAL"]]; for (int v = 0; v < vc; v++) nrm.Add(P(N, v)); }
                else for (int v = 0; v < vc; v++) nrm.Add(Vector3.up);
                if (at["TEXCOORD_0"] != null) { float[] T = p.Floats[(int)at["TEXCOORD_0"]]; for (int v = 0; v < vc; v++) uv.Add(new Vector2(T[v * 2], 1f - T[v * 2 + 1])); }
                else for (int v = 0; v < vc; v++) uv.Add(Vector2.zero);
                if (at["JOINTS_0"] != null && at["WEIGHTS_0"] != null)
                {
                    skinned = true;
                    int ji = (int)at["JOINTS_0"], wi = (int)at["WEIGHTS_0"];
                    int[] J = p.Ints.TryGetValue(ji, out int[] jj) ? jj : Array.ConvertAll(p.Floats[ji], x => (int)x);
                    float[] W = p.Floats.TryGetValue(wi, out float[] ww) ? ww : Array.ConvertAll(p.Ints[wi], x => (float)x);
                    for (int v = 0; v < vc; v++)
                    {
                        float sum = W[v * 4] + W[v * 4 + 1] + W[v * 4 + 2] + W[v * 4 + 3];
                        if (sum <= 0f) sum = 1f;
                        weights.Add(new BoneWeight
                        {
                            boneIndex0 = J[v * 4], weight0 = W[v * 4] / sum,
                            boneIndex1 = J[v * 4 + 1], weight1 = W[v * 4 + 1] / sum,
                            boneIndex2 = J[v * 4 + 2], weight2 = W[v * 4 + 2] / sum,
                            boneIndex3 = J[v * 4 + 3], weight3 = W[v * 4 + 3] / sum,
                        });
                    }
                }
                else for (int v = 0; v < vc; v++) weights.Add(new BoneWeight { boneIndex0 = 0, weight0 = 1f });

                int[] idx;
                if (pr["indices"] != null) idx = p.Ints[(int)pr["indices"]];
                else { idx = new int[vc]; for (int v = 0; v < vc; v++) idx[v] = v; }
                var tri = new int[idx.Length - idx.Length % 3];
                for (int k = 0; k < tri.Length; k += 3) { tri[k] = baseV + idx[k]; tri[k + 1] = baseV + idx[k + 2]; tri[k + 2] = baseV + idx[k + 1]; }
                subs.Add(tri);
                int mi = (int?)pr["material"] ?? -1;
                subMats.Add(mi >= 0 && mi < mats.Length ? mats[mi] : (mats.Length > 0 ? mats[0] : _baseMat));
            }
            var mesh = new Mesh { name = (string)jm["name"] ?? "mesh" };
            if (pos.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(pos);
            mesh.SetNormals(nrm);
            mesh.SetUVs(0, uv);
            mesh.subMeshCount = subs.Count;
            for (int k = 0; k < subs.Count; k++) mesh.SetTriangles(subs[k], k);
            if (skinned) mesh.boneWeights = weights.ToArray();
            mesh.RecalculateBounds();
            meshMats = subMats.ToArray();
            return mesh;
        }

        private static void BuildClip(ParsedGltf p, State s, JObject a)
        {
            var clip = new AnimationClip { name = (string)a["name"], legacy = true, hideFlags = HideFlags.DontUnloadUnusedAsset };
            var samplers = (JArray)a["samplers"];
            foreach (JObject ch in (JArray)a["channels"])
            {
                var target = (JObject)ch["target"];
                if (target["node"] == null) continue;
                int node = (int)target["node"];
                string path = (string)target["path"];
                var smp = (JObject)samplers[(int)ch["sampler"]];
                float[] times = p.Floats[(int)smp["input"]];
                float[] vals = p.Floats[(int)smp["output"]];
                string interp = (string)smp["interpolation"] ?? "LINEAR";
                int comps = path == "rotation" ? 4 : 3;
                int n = times.Length;
                bool cubic = interp == "CUBICSPLINE";
                Func<int, int, float> get = (k, c) => cubic ? vals[(k * 3 + 1) * comps + c] : vals[k * comps + c];
                string[] props = path == "translation" ? new[] { "localPosition.x", "localPosition.y", "localPosition.z" }
                               : path == "rotation" ? new[] { "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w" }
                               : path == "scale" ? new[] { "localScale.x", "localScale.y", "localScale.z" } : null;
                if (props == null) continue;
                var curves = new AnimationCurve[comps];
                for (int c = 0; c < comps; c++) curves[c] = new AnimationCurve();
                for (int k = 0; k < n; k++)
                {
                    float x = get(k, 0), y = get(k, 1), z = get(k, 2);
                    float[] v;
                    if (path == "translation") v = new[] { -x, y, z };
                    else if (path == "rotation") { Quaternion q = Q(x, y, z, get(k, 3)); v = new[] { q.x, q.y, q.z, q.w }; }
                    else v = new[] { x, y, z };
                    for (int c = 0; c < comps; c++) curves[c].AddKey(new Keyframe(times[k], v[c]));
                }
                for (int c = 0; c < comps; c++)
                {
                    AnimationCurve cv = curves[c];
                    Keyframe[] keys = cv.keys;
                    for (int k = 0; k < keys.Length; k++)
                    {
                        if (interp == "STEP") { keys[k].inTangent = float.PositiveInfinity; keys[k].outTangent = float.PositiveInfinity; continue; }
                        float inT = k > 0 ? (keys[k].value - keys[k - 1].value) / Mathf.Max(1e-5f, keys[k].time - keys[k - 1].time) : 0f;
                        float outT = k < keys.Length - 1 ? (keys[k + 1].value - keys[k].value) / Mathf.Max(1e-5f, keys[k + 1].time - keys[k].time) : 0f;
                        keys[k].inTangent = k > 0 ? inT : outT;
                        keys[k].outTangent = k < keys.Length - 1 ? outT : inT;
                    }
                    cv.keys = keys;
                    clip.SetCurve(s.Paths[node], typeof(Transform), props[c], cv);
                }
            }
            clip.EnsureQuaternionContinuity();
            clip.wrapMode = WrapMode.Loop;
            s.Anim.AddClip(clip, clip.name);
        }
    }

    // =====================================================================================
    // Instance
    // =====================================================================================

    /// <summary>One remote player's glTF body: clip selection from the synced motion, carry / tool arm poses.</summary>
    internal sealed class GltfAvatar : IAvatarModel
    {
        public GameObject Root { get; }
        public Transform RightHand { get; }
        public Transform NameAnchor { get; }

        private readonly AvatarModels.Character _c;
        private readonly Animation _anim;
        private readonly Transform _model, _upL, _upR, _loL, _loR, _endL, _endR;
        private string _clip;
        private bool _carry, _driving;
        private float _carryW, _toolW;
        private byte _tool;
        private float _speed;
        private bool _tinted;

        internal GltfAvatar(GltfTemplate t)
        {
            _c = t.Character;
            Root = new GameObject("Character_" + _c.Id);
            GameObject model = UnityEngine.Object.Instantiate(t.Prefab.transform.Find("Model").gameObject, Root.transform, false);
            model.name = "Model";
            _model = model.transform;
            _anim = model.GetComponent<Animation>();

            Transform hand = Find(_model, _c.HandBone);
            // Attach point: the hand slot / hand, with +Z along the fingers is close enough on both rigs.
            var slot = new GameObject("GMS_HandSlot").transform;
            slot.SetParent(hand != null ? hand : _model, false);
            RightHand = slot;
            NameAnchor = Find(_model, _c.HeadBone) ?? _model;
            _upL = Find(_model, _c.UpperArmL); _upR = Find(_model, _c.UpperArmR);
            _loL = Find(_model, _c.LowerArmL); _loR = Find(_model, _c.LowerArmR);
            _endL = _loL != null && _loL.childCount > 0 ? _loL.GetChild(0) : null;
            _endR = _loR != null && _loR.childCount > 0 ? _loR.GetChild(0) : null;
            Play("idle", 0f);
            GltfAvatarLoader.Live.Add(this);
        }

        private static Transform Find(Transform root, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private bool Has(string clip) => _anim != null && _anim.GetClip(clip) != null;

        private void Play(string clip, float fade)
        {
            if (_anim == null || clip == _clip || !Has(clip)) return;
            _clip = clip;
            if (fade <= 0f) _anim.Play(clip);
            else _anim.CrossFade(clip, fade);
        }

        public void Animate(bool driving, Vector3 velocity, byte flags, byte tool, float pitch)
        {
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            float speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            _speed = Mathf.Lerp(_speed, Mathf.Min(speed, 12f), Mathf.Clamp01(dt * 10f));
            bool crouch = (flags & PlayerSync.FlagCrouch) != 0;
            bool sprint = (flags & PlayerSync.FlagSprint) != 0;
            bool usingTool = (flags & PlayerSync.FlagUsingTool) != 0;
            bool airborne = (flags & PlayerSync.FlagGrounded) == 0;
            _carry = !driving && (flags & PlayerSync.FlagCarrying) != 0;
            _driving = driving;
            _tool = driving ? (byte)0 : tool;

            string clip;
            float rate = 1f;
            if (driving) clip = "drive";
            else if (airborne && Has("jump")) clip = "jump";
            else if (crouch) clip = _speed > 0.3f || !Has("crouchidle") ? "crouch" : "crouchidle";
            else if (_speed > 3.5f || (sprint && _speed > 1.5f)) { clip = "run"; rate = Mathf.Clamp(_speed / 5f, 0.7f, 1.4f); }
            else if (_speed > 0.3f) { clip = _carry && _c.HasCarryClip ? "carrywalk" : "walk"; rate = Mathf.Clamp(_speed / 1.6f, 0.6f, 1.6f); }
            else if (usingTool && _tool != 0 && Has("use")) clip = "use";
            else clip = "idle";
            if (crouch && _speed > 0.3f) rate = Mathf.Clamp(_speed / 1.2f, 0.6f, 1.5f);
            Play(clip, 0.2f);
            if (_anim != null && _clip != null)
            {
                AnimationState st = _anim[_clip];
                if (st != null) st.speed = rate;
            }
            _carryW = Mathf.MoveTowards(_carryW, _carry ? 1f : 0f, dt * 4f);
            _toolW = Mathf.MoveTowards(_toolW, _tool != 0 && !_carry && clip != "use" ? 1f : 0f, dt * 4f);
        }

        /// <summary>After the clips: arms forward when carrying; the right arm holds the tool a bit forward.</summary>
        internal void LatePose()
        {
            if (_driving) return;
            Transform body = Root.transform;
            Vector3 fwd = body.forward, up = body.up, right = body.right;
            if (_carryW > 0.001f)
            {
                Aim(_upL, _loL, fwd * 0.8f - up * 0.45f - right * 0.08f, _carryW);
                Aim(_upR, _loR, fwd * 0.8f - up * 0.45f + right * 0.08f, _carryW);
                Aim(_loL, _endL, fwd * 0.9f + up * 0.05f + right * 0.3f, _carryW);
                Aim(_loR, _endR, fwd * 0.9f + up * 0.05f - right * 0.3f, _carryW);
            }
            else if (_toolW > 0.001f)
            {
                Aim(_upR, _loR, fwd * 0.45f - up * 0.85f + right * 0.1f, _toolW * 0.8f);
                Aim(_loR, _endR, fwd * 0.85f - up * 0.35f, _toolW * 0.8f);
            }
        }

        /// <summary>Rotates <paramref name="bone"/> so the direction to <paramref name="child"/> turns toward <paramref name="dir"/>.</summary>
        private static void Aim(Transform bone, Transform child, Vector3 dir, float w)
        {
            if (bone == null || child == null) return;
            Vector3 cur = child.position - bone.position;
            if (cur.sqrMagnitude < 1e-8f) return;
            Quaternion q = Quaternion.FromToRotation(cur, dir.normalized);
            bone.rotation = Quaternion.Slerp(Quaternion.identity, q, w) * bone.rotation;
        }

        public void SetColor(Color c)
        {
            if (_c.TintMaterials == null || _model == null) return;
            foreach (Renderer r in _model.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = r.materials; // instanced for this avatar
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || Array.IndexOf(_c.TintMaterials, mats[i].name.Replace(" (Instance)", "")) < 0) continue;
                    mats[i].color = Color.Lerp(new Color(0.75f, 0.75f, 0.75f), c, 0.7f);
                    changed = true;
                    _tinted = true;
                }
                if (changed) r.materials = mats;
            }
        }

        public void Destroy()
        {
            GltfAvatarLoader.Live.Remove(this);
            if (_model != null && _tinted) // only instanced (tinted) materials belong to this avatar
                foreach (Renderer r in _model.GetComponentsInChildren<Renderer>(true))
                    foreach (Material m in r.materials) if (m != null) UnityEngine.Object.Destroy(m);
            if (Root != null) UnityEngine.Object.Destroy(Root);
        }
    }
}
