using System;
using System.Collections.Generic;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Exclusive use of objects. What another player holds (bucket, pan, jerrycan, tool, hose / rope end,
    /// fuel pistol, ...) or just put into / took out of a holder (the holder may still run that player's
    /// animation or input block: separator, hog pan box, shovel holders) cannot be picked, stacked, attached
    /// to, or used ("E" / "ToolAction2") by the local player. The press is swallowed before the game sees it,
    /// so the local player keeps what they hold (a press on a refused target would otherwise drop it), and a
    /// short Spanish hint names the other player.
    ///
    /// Who holds what comes from ObjectSync (Grab / held poses, ObjectSync.RemoteUserOf). Two players that
    /// pick the same object within one latency window both pick it locally; the ownership claim tie-break
    /// (epoch, then the lower LocalId: SyncLogic.ClaimWins) is the single arbiter every PC computes the same
    /// way, and the loser's PC drops it (ObjectSync.SetOwner -> ForceLocalDrop, then Hint here). Driver seats
    /// are MachineSync's, work tables WorkTableSync's, gold panning tubs InteractionGuard's.
    /// </summary>
    internal static class ExclusiveUse
    {
        /// <summary>Seconds an item stays reserved for the player who put it into / took it out of a holder.</summary>
        internal const float AttachBusy = 3f;
        /// <summary>Same after a holder change a game script made on its own (not from the player's hand).</summary>
        internal const float AutoBusy = 0.75f;
        private const float HintGap = 2f;
        private static float _nextHint;

        private static readonly AccessTools.FieldRef<GravityGun, string> ActionKeyRef = GoldSync.TryRef<GravityGun, string>("_actionKey");

        /// <summary>Shows "Lo tiene NAME" (rate limited unless <paramref name="force"/>).</summary>
        internal static void Hint(ulong who, bool force = false)
        {
            float now = Time.unscaledTime;
            if (!force && now < _nextHint) return;
            _nextHint = now + HintGap;
            try { GoldSync.ShowHint("Lo tiene " + Net.NameOf(who)); }
            catch (Exception) { /* hint only */ }
        }

        /// <summary>True (and the hint) if another player holds or is using <paramref name="go"/>.</summary>
        internal static bool Refuse(GameObject go)
        {
            if (!Net.Active || go == null || !Net.WorldLive) return false;
            ulong who = ObjectSync.RemoteUserOf(go);
            if (who == 0) return false;
            Hint(who);
            return true;
        }

        // While GravityGun.Update runs on a target another player holds / uses: its action presses read false.
        private static string _swallowKey;
        private static bool _swallow;

        [HarmonyPatch]
        private static class PatchGravityGunUpdate
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(GravityGun), "Update");

            private static bool Prepare()
            {
                if (TargetMethod() != null) return true;
                Plugin.Log.LogWarning("Exclusive use: GravityGun.Update not found, objects in another player's use can be stacked / used");
                return false;
            }

            private static void Prefix(GravityGun __instance)
            {
                _swallow = false;
                try
                {
                    if (!Net.Active || __instance == null || ObjectSync.ApplyingHolderChange) return;
                    GameObject hit = __instance.ObjectHit;
                    if (hit == null) return;
                    string key = ActionKeyRef != null ? ActionKeyRef(__instance) : "Action";
                    if (string.IsNullOrEmpty(key)) key = "Action";
                    // Read the real input first (the swallow flag is still off here).
                    if (!InputManager.GetButtonDown(key) && !InputManager.GetButtonDown("ToolAction2")) return;
                    if (!Refuse(hit)) return;
                    _swallowKey = key;
                    _swallow = true;
                }
                catch (Exception) { _swallow = false; }
            }

            private static Exception Finalizer(Exception __exception)
            {
                _swallow = false;
                return __exception;
            }
        }

        // ================================================================== bucket poured by a remote attach
        //
        // A bucket put into a BucketHolderMagnetiteSeparator (magnetite separator, wave table, gold pan, hog pan
        // box, magnetite trailer) is poured by the holder's attach: AddContentFromBucket blocks input, starts
        // Bucket.FillOut (contents move 0.5 s later) and, when the animation ends, OnAnimationEnd puts the bucket
        // in the hands of whoever runs it. ObjectSync replays another player's attach here with the game's own
        // AttachObject, so before this:
        //  - the material moved on BOTH PCs: the acting client sent its deltas and the host had already moved
        //    the same material itself (bucket clamped at 0, the wave table / separator got it twice: gold
        //    doubled); the host pouring made the client send the same move as its own delta;
        //  - at the end of the animation the LOCAL player got the other player's bucket in their hands
        //    (and claimed it from them).
        // Now a pour started by a remote attach is shown only (contents restored, no pan particles, no stats),
        // and its end does not pick the bucket up here.

        private static readonly HashSet<object> ShowOnlyPours = new HashSet<object>();      // FillOut iterators
        private static readonly HashSet<BucketHolderMagnetiteSeparator> RemoteSeparators = new HashSet<BucketHolderMagnetiteSeparator>();
        private static int _showOnlyDepth, _noPickDepth;

        [HarmonyPatch]
        private static class PatchSeparatorAttach
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(BucketHolderMagnetiteSeparator), nameof(BucketHolderMagnetiteSeparator.AddContentFromBucket));
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(BucketHolderMagnetiteSeparator __instance)
            {
                if (__instance == null) return;
                if (RemoteSeparators.Count > 64) RemoteSeparators.RemoveWhere(s => s == null);
                if (Net.Active && ObjectSync.ApplyingHolderChange) RemoteSeparators.Add(__instance);
                else RemoteSeparators.Remove(__instance);
            }
        }

        [HarmonyPatch]
        private static class PatchSeparatorEnd
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(BucketHolderMagnetiteSeparator), nameof(BucketHolderMagnetiteSeparator.OnAnimationEnd));
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(BucketHolderMagnetiteSeparator __instance, out GameObject __state)
            {
                __state = null;
                if (__instance == null || !RemoteSeparators.Remove(__instance)) return;
                __state = __instance.Bucket != null ? __instance.Bucket.gameObject : null;
                _noPickDepth++;
            }

            private static Exception Finalizer(Exception __exception, GameObject __state)
            {
                if (__state != null || _noPickDepth > 0)
                {
                    if (_noPickDepth > 0) _noPickDepth--;
                    try { if (__state != null) ObjectSync.AcceptLocalHolderChange(__state); }
                    catch (Exception) { /* bookkeeping only */ }
                }
                return __exception;
            }
        }

        /// <summary>The end of a remote player's pour must not hand that player's bucket to the local player.</summary>
        [HarmonyPatch]
        private static class PatchPickDuringRemoteEnd
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(GravityGun), nameof(GravityGun.PickObject), new[] { typeof(GameObject), typeof(bool) });
            private static bool Prepare() => TargetMethod() != null;
            private static bool Prefix() => _noPickDepth <= 0;
        }

        private static object _firstIterator;

        internal static MethodBase MoveNextOfFillOut()
        {
            MethodInfo m = AccessTools.Method(typeof(Bucket), nameof(Bucket.FillOutCorutine), new[] { typeof(GameObject) });
            return m != null ? AccessTools.EnumeratorMoveNext(m) : null;
        }

        /// <summary>
        /// Bucket.FillOutCorutine steps: those of a pour that a remote attach started run with contents
        /// restored afterwards. The iterator is recognized on its first step (run inside StartCoroutine,
        /// inside Bucket.FillOut, inside the remote attach).
        /// </summary>
        [HarmonyPatch]
        private static class PatchFillOutStep
        {
            private static MethodBase TargetMethod() => MoveNextOfFillOut();
            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(object __instance, out object __state)
            {
                __state = null;
                try
                {
                    if (Net.Active && ObjectSync.ApplyingHolderChange && _firstIterator == null) _firstIterator = __instance;
                    if (!ShowOnlyPours.Contains(__instance)) return;
                    Bucket b = Traverse.Create(__instance).Field("<>4__this").GetValue<Bucket>();
                    __state = ContainerSync.BeginShowOnly(b);
                    _showOnlyDepth++;
                }
                catch (Exception) { __state = null; }
            }

            private static Exception Finalizer(Exception __exception, object __instance, bool __result, object __state)
            {
                try
                {
                    if (ShowOnlyPours.Contains(__instance))
                    {
                        if (_showOnlyDepth > 0) _showOnlyDepth--;
                        ContainerSync.EndShowOnly(__state);
                        if (!__result || __exception != null) ShowOnlyPours.Remove(__instance);
                    }
                    else if (ReferenceEquals(_firstIterator, __instance))
                    {
                        // First step done inside the remote attach: every later step is show only.
                        if (__result && __exception == null)
                        {
                            if (ShowOnlyPours.Count > 64) ShowOnlyPours.Clear(); // stopped coroutines never finish
                            ShowOnlyPours.Add(__instance);
                        }
                    }
                }
                catch (Exception) { /* never break the game's coroutine */ }
                finally { if (ReferenceEquals(_firstIterator, __instance)) _firstIterator = null; }
                return __exception;
            }
        }

        /// <summary>A gold pan filled by a remote player's pour gets no particles here (a pan's contents exist once, on the panning PC).</summary>
        [HarmonyPatch]
        private static class PatchPanFillShowOnly
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(GoldPan), nameof(GoldPan.FillIn), new[] { typeof(float), typeof(ResourceBag), typeof(float) });
            private static bool Prepare() => TargetMethod() != null;
            private static bool Prefix() => _showOnlyDepth <= 0;
        }

        [HarmonyPatch]
        private static class PatchButtonDown
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(InputManager), nameof(InputManager.GetButtonDown), new[] { typeof(string) });

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(string buttonCode, ref bool __result)
            {
                if (!_swallow || (buttonCode != _swallowKey && buttonCode != "ToolAction2")) return true;
                __result = false;
                return false;
            }
        }
    }
}
