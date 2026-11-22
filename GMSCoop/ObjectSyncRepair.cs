using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using SaveSystem;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Repair-style holders (RepairHolder and its subclasses: miner's moss in a sluice or hog pan,
    /// sluice crates, machine spare parts such as teeth, wheels, hoses, trommel parts) do not keep the
    /// loose item. AttachObject turns on a built-in copy inside the holder (ObjectInHolder, a
    /// RepairHoldable) with the item's data and destroys the loose item. Taking it out turns the copy
    /// off and instantiates a fresh loose item in the player's hands.
    ///
    /// The loose-item sync only saw "item destroyed" / "item created": the other PC's holder stayed
    /// empty (moss not visible in the sluice) and that player could not take it out. Here the holder
    /// state itself is replicated (SubRepair):
    ///  - every PC polls the repair holders in HolderObjectsManager; a local change (filled / emptied)
    ///    is sent with the holder reference and the built-in copy's component data (moss gold and dirt,
    ///    part durability);
    ///  - the receiver fills its holder with the game's own AttachObject on a temporary instance of the
    ///    holder's HoldablePrefab (the attach destroys it), or empties it with the built-in copy's own
    ///    Detach while the loose item it spawns is captured and removed and GravityGun.PickObject is
    ///    blocked (the real loose item comes from the detaching PC as a normal network create);
    ///  - a filled miner's moss is re-sent when its contents change (sluice processing), so whoever
    ///    takes it out gets the gold;
    ///  - a joining player gets every holder that changed since the save in the catch-up.
    /// </summary>
    internal static partial class ObjectSync
    {
        private const float RepairPollInterval = 0.25f;
        private const float RepairDataInterval = 2f;

        private static readonly Dictionary<RepairHolder, bool> RepairKnown = new Dictionary<RepairHolder, bool>();
        private static readonly Dictionary<RepairHolder, int> RepairSig = new Dictionary<RepairHolder, int>();
        private static readonly HashSet<RepairHolder> RepairChanged = new HashSet<RepairHolder>();
        private static readonly List<Holder> _repairScan = new List<Holder>();
        private static float _nextRepairPoll, _nextRepairData, _nextRepairWarn;
        private static bool _repairApplying;       // GravityGun.PickObject blocked while set
        private static List<GameObject> _repairCaptured; // non-null while a remote apply runs

        private static void ClearRepair()
        {
            RepairKnown.Clear();
            RepairSig.Clear();
            RepairChanged.Clear();
            JackSent.Clear();
            JackRemoteUntil.Clear();
        }

        /// <summary>True if <paramref name="go"/> was instantiated by a remote repair apply (it is removed afterwards).</summary>
        private static bool CaptureRepairSpawn(GameObject go)
        {
            if (_repairCaptured == null || go == null) return false;
            _repairCaptured.Add(go);
            return true;
        }

        // ---------------------------------------------------------------- local detection

        private static void RepairTick(float now)
        {
            if (now < _nextRepairPoll || !Singleton<HolderObjectsManager>.IsInstanced()) return;
            _nextRepairPoll = now + RepairPollInterval;
            JackTick(now);
            bool dataPass = now >= _nextRepairData;
            if (dataPass) _nextRepairData = now + RepairDataInterval;

            _repairScan.Clear();
            _repairScan.AddRange(Singleton<HolderObjectsManager>.Instance.HolderObjects);
            foreach (Holder h in _repairScan)
            {
                if (!(h is RepairHolder rh) || rh == null || rh.ObjectInHolder == null) continue;
                bool filled = !rh.IsEmpty();
                if (!RepairKnown.TryGetValue(rh, out bool was))
                {
                    // First sight (world loaded, area streamed in, machine bought): same on every PC.
                    RepairKnown[rh] = filled;
                    if (filled) RepairSig[rh] = DataSig(rh);
                    continue;
                }
                if (was != filled)
                {
                    RepairKnown[rh] = filled;
                    RepairChanged.Add(rh);
                    SendRepair(rh, filled, log: true);
                }
                else if (dataPass && filled && Net.IsHost && rh.ObjectInHolder is MinersMoss)
                {
                    // Sluice processing fills the moss: keep the other PCs' copy current. Only the host sends
                    // it: the host is the contents authority of every container (ContainerSync), and its moss
                    // value is what every PC converges on; two PCs re-sending their own value flip-flopped it.
                    int sig = DataSig(rh);
                    if (!RepairSig.TryGetValue(rh, out int old) || old != sig)
                    {
                        RepairChanged.Add(rh);
                        SendRepair(rh, true, log: false);
                    }
                }
            }
            _repairScan.Clear();
            if (RepairKnown.Count > 4096) PruneRepair();
        }

        private static void PruneRepair()
        {
            var dead = new List<RepairHolder>();
            foreach (RepairHolder rh in RepairKnown.Keys) if (rh == null) dead.Add(rh);
            foreach (RepairHolder rh in dead) { RepairKnown.Remove(rh); RepairSig.Remove(rh); RepairChanged.Remove(rh); }
        }

        private static int DataSig(RepairHolder rh)
        {
            byte[] st = rh.ObjectInHolder != null ? SerializeBuiltIn(rh.ObjectInHolder.gameObject) : null;
            return Hash(st);
        }

        private static int Hash(byte[] data)
        {
            if (data == null) return 0;
            unchecked
            {
                int h = (int)2166136261;
                foreach (byte b in data) h = (h ^ b) * 16777619;
                return h;
            }
        }

        private static void SendRepair(RepairHolder rh, bool filled, bool log)
        {
            byte[] st = filled && rh.ObjectInHolder != null ? SerializeBuiltIn(rh.ObjectInHolder.gameObject) : null;
            var ms = Begin(SubRepair, out var w);
            w.Write(Net.LocalId);
            WriteHolderRef(w, rh, null);
            w.Write(filled);
            w.Write((ushort)(st?.Length ?? 0));
            if (st != null) w.Write(st);
            ItemData.Of(filled ? rh : null).Write(w);
            Bcast(ms.ToArray(), true);
            if (filled) RepairSig[rh] = Hash(st);
            if (log && LogEvent(EvAttach))
                Plugin.Log.LogInfo($"Object sync: {rh.name} ({rh.Type}) {(filled ? "filled" : "emptied")} here, sent");
        }

        private static void SendRepairCatchUp()
        {
            int n = 0;
            foreach (RepairHolder rh in RepairChanged)
            {
                if (rh == null || rh.ObjectInHolder == null) continue;
                SendRepair(rh, !rh.IsEmpty(), log: false);
                n++;
            }
            SendJackCatchUp();
            if (n > 0) Plugin.Log.LogInfo($"Object sync: sent {n} repair holder states (moss, crates, parts) for the joining player");
        }

        // ---------------------------------------------------------------- remote apply

        private static void ReceiveRepair(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            HolderRef hr = ReadHolderRef(r);
            bool filled = r.ReadBoolean();
            int len = r.ReadUInt16();
            byte[] state = len > 0 ? r.ReadBytes(len) : null;
            ItemData item = ItemData.Read(r);
            if (sender == Net.LocalId) return;
            RepairHolder rh = ResolveHolder(hr) as RepairHolder;
            if (rh == null || rh.ObjectInHolder == null)
            {
                if (Time.unscaledTime >= _nextRepairWarn)
                {
                    _nextRepairWarn = Time.unscaledTime + 10f;
                    Plugin.Log.LogWarning($"Object sync: repair holder {(hr.Names.Length > 0 ? hr.Names[hr.Names.Length - 1] : "?")} not found here (area not loaded?)");
                }
                return;
            }
            ApplyRepair(rh, filled, state, item);
        }

        private static void ApplyRepair(RepairHolder rh, bool filled, byte[] state, ItemData item)
        {
            RepairKnown[rh] = filled;
            RepairChanged.Add(rh);
            bool isFilled = !rh.IsEmpty();
            _repairCaptured = new List<GameObject>();
            _repairApplying = true;
            _applyingAttach = true; // the attach must not drop what the local player holds
            try
            {
                if (filled && !isFilled)
                {
                    // A diamond sluice crate is another prefab (RepairHolder.DetachObject picks it the same way).
                    GameObject prefab = item.CrateType == (int)WashPlantSluiceBoxDirt.CrateResourceType.Diamond && rh.OptionalHoldablePrefab != null
                        ? rh.OptionalHoldablePrefab : rh.HoldablePrefab;
                    if (prefab == null)
                    {
                        Plugin.Log.LogWarning($"Object sync: {rh.name} has no item prefab, cannot fill it here");
                    }
                    else
                    {
                        GameObject temp = UnityEngine.Object.Instantiate(prefab, rh.transform.position, rh.transform.rotation);
                        _repairCaptured.Add(temp);
                        // The holder logic reads the item it gets (trommel: screwed in or broken; crate: resource
                        // type): the temporary item carries the real item's values first.
                        item.ApplyTo(temp);
                        if (rh.ObjectInHolder is RepairHoldable rhd) rhd._EnableAchive = false; // the other player's repair
                        rh.AttachObject(temp); // the game's own attach: visuals, triggers, OnAttach (sluice logic)
                    }
                }
                else if (!filled && isFilled)
                {
                    Holdable builtIn = rh.ObjectInHolder;
                    if (builtIn.MyHolder == null) builtIn.MyHolder = rh;
                    builtIn.Detach(); // spawns a loose copy (captured below) and empties the holder
                }
                if (filled && state != null && !rh.IsEmpty()) ApplyBuiltIn(rh.ObjectInHolder.gameObject, state);
                if (filled && !rh.IsEmpty()) item.ApplyName(rh);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Object sync: applying {rh.name} {(filled ? "filled" : "emptied")} failed: {ex.Message}");
            }
            finally
            {
                _applyingAttach = false;
                _repairApplying = false;
                List<GameObject> captured = _repairCaptured;
                _repairCaptured = null;
                foreach (GameObject g in captured) if (g != null) UnityEngine.Object.Destroy(g);
            }
            // Remember what this PC really shows now (not what was asked): a failed apply must not be
            // detected as a local change and sent back, and the data signature is this PC's own reading.
            bool now = !rh.IsEmpty();
            RepairKnown[rh] = now;
            if (now) RepairSig[rh] = DataSig(rh);
            if (now != filled)
                Plugin.Log.LogWarning($"Object sync: {rh.name} could not be {(filled ? "filled" : "emptied")} here");
            else if (isFilled != filled && LogEvent(EvAttach))
                Plugin.Log.LogInfo($"Object sync: {rh.name} ({rh.Type}) {(filled ? "filled" : "emptied")} like the other player");
        }

        // ---------------------------------------------------------------- built-in copy data

        /// <summary>
        /// Save data of the built-in copy (moss gold / dirt, part durability...) in the same compact form
        /// as SerializeState. The copy has no SaveMe of its own (it is part of the holder's object), so
        /// its ISerializableComponent components are read directly, in hierarchy order (same prefab on
        /// every PC, same order).
        /// </summary>
        private static byte[] SerializeBuiltIn(GameObject go)
        {
            ISerializableComponent[] comps = BuiltInComponents(go);
            if (comps.Length == 0) return null;
            try
            {
                var ms = new MemoryStream();
                var w = new BinaryWriter(ms);
                int n = 0;
                w.Write((byte)0);
                for (int i = 0; i < comps.Length && i < 255; i++)
                {
                    List<XmlKey> keys;
                    try { keys = comps[i].Serialize(); }
                    catch (Exception) { continue; }
                    if (keys == null) continue;
                    var good = new List<XmlKey>();
                    foreach (XmlKey k in keys)
                        if (k != null && k.Key != null && k.referenceName == null && k.XmlObj == null && k.XmlListObj == null && TypeCode(k.Value) >= 0)
                            good.Add(k);
                    if (good.Count == 0) continue;
                    w.Write((byte)i);
                    w.Write((ushort)good.Count);
                    foreach (XmlKey k in good)
                    {
                        w.Write(k.Key);
                        int tc = TypeCode(k.Value);
                        w.Write((byte)tc);
                        switch (tc)
                        {
                            case 0: w.Write((string)k.Value); break;
                            case 1: w.Write((int)k.Value); break;
                            case 2: w.Write((ulong)k.Value); break;
                            case 3: w.Write((bool)k.Value); break;
                            case 4: w.Write((float)k.Value); break;
                            case 5: var q = (XmlQuaternion)k.Value; w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); break;
                            case 6: WriteV3(w, (Vector3)k.Value); break;
                        }
                    }
                    n++;
                }
                if (n == 0 || ms.Length > MaxStateBytes) return null;
                w.Flush();
                byte[] data = ms.ToArray();
                data[0] = (byte)n;
                return data;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void ApplyBuiltIn(GameObject go, byte[] data)
        {
            ISerializableComponent[] comps = BuiltInComponents(go);
            try
            {
                var r = new BinaryReader(new MemoryStream(data));
                int n = r.ReadByte();
                for (int c = 0; c < n; c++)
                {
                    int idx = r.ReadByte();
                    int count = r.ReadUInt16();
                    var keys = new List<XmlKey>(count);
                    for (int i = 0; i < count; i++)
                    {
                        var k = new XmlKey { Key = r.ReadString() };
                        switch (r.ReadByte())
                        {
                            case 0: k.Value = r.ReadString(); break;
                            case 1: k.Value = r.ReadInt32(); break;
                            case 2: k.Value = r.ReadUInt64(); break;
                            case 3: k.Value = r.ReadBoolean(); break;
                            case 4: k.Value = r.ReadSingle(); break;
                            case 5: k.Value = new XmlQuaternion { x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), w = r.ReadSingle() }; break;
                            case 6: k.Value = ReadV3(r); break;
                            default: return;
                        }
                        keys.Add(k);
                    }
                    if (idx >= comps.Length) continue;
                    try
                    {
                        comps[idx].Deserialize(keys);
                        comps[idx].AfterDeserialize();
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Object sync: holder item data of {go.name} #{idx} not applied: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Object sync: bad holder item data for " + go.name + ": " + ex.Message);
            }
        }

        private static readonly List<MonoBehaviour> _builtInScan = new List<MonoBehaviour>();

        private static ISerializableComponent[] BuiltInComponents(GameObject go)
        {
            _builtInScan.Clear();
            go.GetComponentsInChildren(true, _builtInScan);
            var list = new List<ISerializableComponent>();
            foreach (MonoBehaviour mb in _builtInScan)
            {
                // A child with its own SaveMe (between it and the copy) is a separate saved object.
                if (!(mb is ISerializableComponent sc)) continue;
                bool own = false;
                for (Transform t = mb.transform; t != null && t != go.transform; t = t.parent)
                    if (t.GetComponent<SaveSystem.SaveMe>() != null) { own = true; break; }
                if (!own) list.Add(sc);
            }
            _builtInScan.Clear();
            return list.ToArray();
        }

        // ---------------------------------------------------------------- car jack height

        // The jack's lever actions replay on every PC (WorldSync), but each PC integrates the lift in its own
        // FixedUpdate: heights drift apart, and a wheel can be "grounded" here while it is free on the other
        // PC. The jack's height is sent as an absolute value whenever it changes locally.
        private static readonly Dictionary<string, float> JackSent = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> JackRemoteUntil = new Dictionary<string, float>();

        private static void JackTick(float now)
        {
            foreach (Entry e in All)
            {
                if (e.Go == null || !(e.Hold is CarJack cj) || !e.Go.activeInHierarchy) continue;
                float v = cj.InterpolateProgress;
                if (!JackSent.TryGetValue(e.Key, out float last)) { JackSent[e.Key] = v; continue; }
                if (JackRemoteUntil.TryGetValue(e.Key, out float until) && now < until) { JackSent[e.Key] = v; continue; }
                bool end = (v <= 0f || v >= 1f) && v != last;
                if (Mathf.Abs(v - last) < 0.02f && !end) continue;
                JackSent[e.Key] = v;
                SendJack(e.Key, v);
            }
        }

        private static void SendJack(string key, float v)
        {
            var ms = Begin(SubJack, out var w);
            w.Write(Net.LocalId);
            w.Write(key);
            w.Write(v);
            Bcast(ms.ToArray(), true);
        }

        private static void SendJackCatchUp()
        {
            foreach (Entry e in All)
                if (e.Go != null && e.Hold is CarJack cj && cj.InterpolateProgress > 0.001f) SendJack(e.Key, cj.InterpolateProgress);
        }

        private static void ReceiveJack(BinaryReader r)
        {
            ulong sender = r.ReadUInt64();
            string key = r.ReadString();
            float v = Mathf.Clamp01(r.ReadSingle());
            if (sender == Net.LocalId || !ByKey.TryGetValue(key, out Entry e) || e.Go == null || !(e.Hold is CarJack cj)) return;
            cj.InterpolateProgress = v;
            if (cj.UpBox != null)
                cj.UpBox.localPosition = Vector3.Lerp(cj.StartTransform, cj.StartTransform + Vector3.forward * cj.JackHeight, v);
            // Its FixedUpdate sees the new value and sets the lifted truck / trailer kinematic like the other PC.
            JackSent[key] = v;
            JackRemoteUntil[key] = Time.unscaledTime + 1f; // the other PC drives it: do not echo the lerp
        }

        // ---------------------------------------------------------------- item values the holder logic reads

        private static readonly AccessTools.FieldRef<OutlineData, string> OutlineNameRef = TryOutlineName();

        private static AccessTools.FieldRef<OutlineData, string> TryOutlineName()
        {
            try { return AccessTools.FieldRefAccess<OutlineData, string>("ItemName"); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// What the game's AttachObject reads from the item it gets: wear (CheckAndRepair), the sluice crate
        /// resource type, and the crate's display name. Taken from the filled holder's built-in copy.
        /// </summary>
        private struct ItemData
        {
            public bool HasRepair;
            public float Lifetime, DestructAt, DestructSpeed; // Lifetime = CheckAndRepair.Durability (0..1)
            public bool Reinforced;
            public int CrateType; // -1: not a crate
            public string Name;

            public static ItemData Of(RepairHolder rh)
            {
                var d = new ItemData { CrateType = -1, Name = "" };
                if (rh == null || rh.ObjectInHolder == null) return d;
                GameObject go = rh.ObjectInHolder.gameObject;
                CheckAndRepair c = go.GetComponent<CheckAndRepair>();
                if (c != null)
                {
                    d.HasRepair = true;
                    d.Lifetime = c.Durability;
                    d.Reinforced = c.IsReinforced;
                    d.DestructAt = c.RandomDestructionAt;
                    d.DestructSpeed = c.RandomDestructionSpeed;
                }
                WashPlantSluiceBoxDirt dirt = go.GetComponentInChildren<WashPlantSluiceBoxDirt>(true);
                if (dirt != null && rh.ObjectInHolder.Type == Holder.HolderType.SluiceCrate) d.CrateType = (int)dirt.MyCrateResourceType;
                OutlineData od = go.GetComponent<OutlineData>();
                if (od != null && OutlineNameRef != null) d.Name = OutlineNameRef(od) ?? "";
                return d;
            }

            public void Write(BinaryWriter w)
            {
                w.Write(HasRepair);
                if (HasRepair) { w.Write(Lifetime); w.Write(Reinforced); w.Write(DestructAt); w.Write(DestructSpeed); }
                w.Write((sbyte)CrateType);
                w.Write(Name ?? "");
            }

            public static ItemData Read(BinaryReader r)
            {
                var d = new ItemData { HasRepair = r.ReadBoolean() };
                if (d.HasRepair) { d.Lifetime = r.ReadSingle(); d.Reinforced = r.ReadBoolean(); d.DestructAt = r.ReadSingle(); d.DestructSpeed = r.ReadSingle(); }
                d.CrateType = r.ReadSByte();
                d.Name = r.ReadString();
                return d;
            }

            /// <summary>Before the attach: the temporary item carries the real item's values.</summary>
            public void ApplyTo(GameObject temp)
            {
                CheckAndRepair c = temp.GetComponent<CheckAndRepair>();
                if (HasRepair && c != null)
                {
                    c.IsReinforced = Reinforced;
                    c.RandomDestructionAt = DestructAt;
                    c.RandomDestructionSpeed = DestructSpeed;
                    c.Durability = Lifetime;
                }
                SluiceCrateHoldable crate = temp.GetComponent<SluiceCrateHoldable>();
                if (crate != null && CrateType >= 0) crate.MyCrateResourceType = (WashPlantSluiceBoxDirt.CrateResourceType)CrateType;
            }

            /// <summary>After the attach: the crate's display name (RepairHolder saves it as attached_item_name).</summary>
            public void ApplyName(RepairHolder rh)
            {
                if (string.IsNullOrEmpty(Name) || OutlineNameRef == null || rh.ObjectInHolder == null) return;
                OutlineData od = rh.ObjectInHolder.GetComponent<OutlineData>();
                if (od != null) OutlineNameRef(od) = Name;
            }
        }

        // ---------------------------------------------------------------- Harmony

        // Non-virtual calls of the base methods (what the pickup-wheel overrides call after their check).
        private static readonly Action<RepairHolder, GameObject> RepairHolderAttachBase =
            AccessTools.MethodDelegate<Action<RepairHolder, GameObject>>(AccessTools.Method(typeof(RepairHolder), nameof(RepairHolder.AttachObject)), null, false);
        private static readonly Action<RepairHolder> RepairHolderDetachBase =
            AccessTools.MethodDelegate<Action<RepairHolder>>(AccessTools.Method(typeof(RepairHolder), nameof(RepairHolder.DetachObject)), null, false);
        private static readonly Action<RepairHoldable, GameObject> RepairHoldableAttachBase =
            AccessTools.MethodDelegate<Action<RepairHoldable, GameObject>>(AccessTools.Method(typeof(RepairHoldable), nameof(RepairHoldable.Attach)), null, false);
        private static readonly Action<RepairHoldable> RepairHoldableDetachBase =
            AccessTools.MethodDelegate<Action<RepairHoldable>>(AccessTools.Method(typeof(RepairHoldable), nameof(RepairHoldable.Detach)), null, false);

        /// <summary>
        /// A pickup wheel goes on / off only while the truck is on a car jack (wheel not grounded). That is the
        /// other player's local check: the wheel on this PC may still touch the ground for a moment (the jack
        /// height arrives separately). While a remote change is applied, skip the check like the other PC did.
        /// </summary>
        private static MethodInfo _updateWheelM;

        /// <summary>VPWheelCollider.UpdateWheelCollider(its WheelCollider), by reflection (no VehiclesModule reference).</summary>
        private static void UpdateWheel(RepairPickupWheelHolder h)
        {
            try
            {
                Component wc = h.WheelCollider.GetComponent("WheelCollider");
                if (wc == null) return;
                if (_updateWheelM == null) _updateWheelM = AccessTools.Method(h.WheelCollider.GetType(), "UpdateWheelCollider", new[] { wc.GetType() });
                _updateWheelM?.Invoke(h.WheelCollider, new object[] { wc });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Object sync: wheel collider update failed: " + ex.Message);
            }
        }

        [HarmonyPatch(typeof(RepairPickupWheelHolder), nameof(RepairPickupWheelHolder.AttachObject))]
        private static class PatchWheelHolderAttach
        {
            private static bool Prefix(RepairPickupWheelHolder __instance, GameObject obj)
            {
                if (!_repairApplying) return true;
                RepairHolderAttachBase(__instance, obj);
                if (__instance.WheelCollider != null)
                {
                    __instance.WheelCollider.radius = __instance.WheelRadius;
                    UpdateWheel(__instance);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RepairPickupWheelHolder), nameof(RepairPickupWheelHolder.DetachObject))]
        private static class PatchWheelHolderDetach
        {
            private static bool Prefix(RepairPickupWheelHolder __instance)
            {
                if (!_repairApplying) return true;
                RepairHolderDetachBase(__instance);
                if (__instance.WheelCollider != null)
                {
                    __instance.WheelRadius = __instance.WheelCollider.radius;
                    __instance.WheelCollider.radius = 0.14f;
                    UpdateWheel(__instance);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(RepairHoldablePickupWheel), nameof(RepairHoldablePickupWheel.Attach))]
        private static class PatchWheelAttach
        {
            private static bool Prefix(RepairHoldablePickupWheel __instance, GameObject obj)
            {
                if (!_repairApplying) return true;
                RepairHoldableAttachBase(__instance, obj);
                return false;
            }
        }

        [HarmonyPatch(typeof(RepairHoldablePickupWheel), nameof(RepairHoldablePickupWheel.Detach))]
        private static class PatchWheelDetach
        {
            private static bool Prefix(RepairHoldablePickupWheel __instance)
            {
                if (!_repairApplying) return true;
                RepairHoldableDetachBase(__instance);
                return false;
            }
        }

        /// <summary>No HUD hints from applying another player's change ("no car jack" and so on).</summary>
        [HarmonyPatch]
        private static class PatchHintWhileApplying
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (MethodInfo m in typeof(HUD).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (m.Name == "ShowHint" || m.Name == "ShowHintParam") yield return m;
            }
            private static bool Prefix() => !_repairApplying;
        }


        /// <summary>
        /// RepairHolder.DetachObject puts the new loose item in the LOCAL player's hands
        /// (GravityGun.PickObject). While a remote "emptied" is applied that loose copy is removed instead.
        /// </summary>
        [HarmonyPatch]
        private static class PatchRepairPick
        {
            private static MethodBase Target => AccessTools.Method(typeof(GravityGun), nameof(GravityGun.PickObject), new[] { typeof(GameObject), typeof(bool) });

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Object sync: GravityGun.PickObject not found, a remote holder detach may put an item in the local player's hands");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static bool Prefix() => !_repairApplying;
        }
    }
}
