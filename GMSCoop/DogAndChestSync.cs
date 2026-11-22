using System;
using System.Collections.Generic;
using System.IO;
using GoldDigger;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// The dog at the base (GoldDigger.Dog) is a world object on every PC, but it reacted only to its own
    /// PC's player: it slept on one screen while it stood up for the other player, and petting, feeding
    /// and barking were seen and heard by one player only.
    ///  - Awake/asleep: each PC sends whether its local player is near the dog (DogProximitySensor calls
    ///    IdleAnimation/SleepAnimation). The dog stays awake while ANY player is near.
    ///  - Pet, eat, bark: replayed on the other PCs.
    /// Social msg 18, sub 6.
    /// </summary>
    internal static class DogSync
    {
        internal const byte SubDog = 6;
        private const byte EvNear = 0, EvFar = 1, EvEat = 2, EvPet = 3, EvBark = 4;

        private static bool _applying;
        private static bool _localNear;
        private static float _nextNearRefresh;
        private static readonly Dictionary<ulong, float> RemoteNear = new Dictionary<ulong, float>(); // player -> expiry

        private static readonly System.Reflection.MethodInfo PetMethod = AccessTools.Method(typeof(Dog), "PetAnimation");

        private static bool AnyRemoteNear
        {
            get
            {
                float now = Time.unscaledTime;
                foreach (var kv in RemoteNear) if (kv.Value > now) return true;
                return false;
            }
        }

        private static bool Live => Net.Active && Net.WorldLive && !GameState.IsLoading;

        private static void Send(byte ev)
        {
            if (!Live || Net.Peers.Count == 0) return;
            var ms = Net.Writer(Social.MsgId);
            var w = new BinaryWriter(ms);
            w.Write(SubDog);
            w.Write(Net.LocalId);
            w.Write(ev);
            Net.Broadcast(ms.ToArray(), reliable: true);
        }

        internal static void Receive(ulong from, BinaryReader r, byte[] raw)
        {
            ulong owner = r.ReadUInt64();
            byte ev = r.ReadByte();
            if (Net.IsHost) Net.Broadcast(raw, reliable: true, except: from);
            if (owner == Net.LocalId || !Live) return;
            Dog dog = UnityEngine.Object.FindObjectOfType<Dog>();
            if (dog == null) { if (ev == EvNear) RemoteNear[owner] = Time.unscaledTime + 25f; return; }
            _applying = true;
            try
            {
                switch (ev)
                {
                    case EvNear: RemoteNear[owner] = Time.unscaledTime + 25f; dog.IdleAnimation(); break;
                    case EvFar:
                        RemoteNear.Remove(owner);
                        if (!_localNear && !AnyRemoteNear) dog.SleepAnimation();
                        break;
                    case EvEat: dog.EatAnimation(); break;
                    case EvPet: PetMethod?.Invoke(dog, null); break;
                    case EvBark: dog.Bark(); break;
                }
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Dog sync: " + ex.Message); }
            finally { _applying = false; }
        }

        /// <summary>Plugin.Update: refresh "near" while the local player stays near; drop expired remote entries.</summary>
        internal static void Tick()
        {
            if (!Net.Active) { RemoteNear.Clear(); _localNear = false; return; }
            if (_localNear && Time.unscaledTime >= _nextNearRefresh)
            {
                _nextNearRefresh = Time.unscaledTime + 10f;
                Send(EvNear);
            }
        }

        [HarmonyPatch(typeof(Dog), nameof(Dog.IdleAnimation))]
        private static class PatchIdle
        {
            private static void Prefix()
            {
                if (_applying || !Net.Active) return;
                _localNear = true;
                _nextNearRefresh = Time.unscaledTime + 10f;
                Send(EvNear);
            }
        }

        [HarmonyPatch(typeof(Dog), nameof(Dog.SleepAnimation))]
        private static class PatchSleep
        {
            private static bool Prefix()
            {
                if (_applying || !Net.Active) return true;
                bool wasNear = _localNear;
                _localNear = false;
                if (wasNear) Send(EvFar);
                return !AnyRemoteNear; // another player still next to the dog: it stays awake
            }
        }

        [HarmonyPatch(typeof(Dog), nameof(Dog.EatAnimation))]
        private static class PatchEat
        {
            private static void Prefix() { if (!_applying && Net.Active) Send(EvEat); }
        }

        [HarmonyPatch(typeof(Dog), "PetAnimation")]
        private static class PatchPet
        {
            private static bool Prepare() => PetMethod != null;
            private static void Prefix() { if (!_applying && Net.Active) Send(EvPet); }
        }

        [HarmonyPatch(typeof(Dog), nameof(Dog.Bark))]
        private static class PatchBark
        {
            private static void Prefix() { if (!_applying && Net.Active) Send(EvBark); }
        }
    }

    /// <summary>
    /// Seasonal event chests (Easter egg, Christmas gift, Halloween pumpkin). Their reward is the
    /// GoldenBarBonus inside, whose Unlocked flag WorldSync already shares, and UseGoldBar refuses an
    /// unlocked bar, so the reward is given once. But the chest itself stayed closed and clickable on the
    /// other PCs. When an unlocked flag arrives from another player (WorldSync runs the bar's
    /// AfterDeserialize), the chest holding that bar is shown as opened here too.
    /// </summary>
    internal static class SeasonChestSync
    {
        [HarmonyPatch(typeof(GoldenBarBonus), nameof(GoldenBarBonus.AfterDeserialize))]
        private static class PatchBarAfterDeserialize
        {
            private static void Postfix(GoldenBarBonus __instance)
            {
                try
                {
                    if (!Net.Active || GameState.IsLoading || __instance == null || !__instance.Unlocked) return;
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<EasterEggEventChest>())
                    {
                        if (c.goldBar != __instance) continue;
                        var col = c.GetComponent<BoxCollider>();
                        if (col != null) col.enabled = false;
                        var outline = c.GetComponent<OutlineData>();
                        if (outline != null) outline.IsOutlined = false;
                        if (c.eggBody != null) c.eggBody.gameObject.SetActive(false);
                    }
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<ChristmasGiftEventChest>())
                    {
                        if (c.goldBar != __instance) continue;
                        var col = c.GetComponent<Collider>();
                        if (col != null) col.enabled = false;
                        c.gameObject.SetActive(false);
                    }
                    foreach (var c in UnityEngine.Object.FindObjectsOfType<HalloweenEventPumpkin>())
                    {
                        if (c.goldBar != __instance.gameObject) continue;
                        foreach (var col in c.GetComponents<Collider>()) col.enabled = false;
                        if (c.pumpkinChest != null) c.pumpkinChest.SetActive(false);
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Season chest sync: " + ex.Message); }
            }
        }
    }
}
