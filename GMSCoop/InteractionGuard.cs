using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GoldDigger;
using HarmonyLib;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Never leaves the local player locked by a game interaction that another player broke.
    ///
    /// Some uses run a coroutine that sets InputManager.AllInputBlocked, waits, then clears it:
    /// emptying the shovel into the starter hog pan's dirt box or a bucket (ShovelToolHolder:
    /// shovel parented to the holder, input blocked 1 s), the small shovel (ShovelSmallToolHolder)
    /// and the magnetite separator bucket animation. Unity stops a coroutine for good when its
    /// GameObject is deactivated, and the cleanup never runs: input stays blocked forever (no walk,
    /// no actions, no pause menu) while the rest of the game keeps running.
    ///
    /// In single player that cannot happen (the input block stops the player from breaking it). In
    /// co-op the other player can: picking the dirt box off the hog pan replicates here as
    /// Holdable.Detach -> HogPan.OnDetachMudBox -> HogPanDirtBox.OnDetach, which deactivates the
    /// box's ShovelToolHolder unconditionally, killing the local Unscrewing coroutine (and the
    /// local shovel, parented under that inactive holder, vanishes with it).
    ///
    /// The coroutines are wrapped: when one dies (its object went inactive or was destroyed, or it
    /// threw) or runs far too long, the guard does what its tail would have done: unblock input,
    /// give the shovel back to the player, end the use action.
    /// </summary>
    internal static class InteractionGuard
    {
        private enum Kind { Shovel, SmallShovel, Separator }

        private sealed class Token
        {
            public Kind Kind;
            public MonoBehaviour Owner;
            public float Start;
            public float Timeout;
            public bool Done;    // ran to the end
            public bool Failed;  // threw
            public bool Aborted; // recovered by the guard: the wrapper stops at its next step
        }

        private static readonly List<Token> Tokens = new List<Token>();

        private static readonly AccessTools.FieldRef<ToolUseComponent, PlayerTool> MyToolRef = SafeRef();

        private static AccessTools.FieldRef<ToolUseComponent, PlayerTool> SafeRef()
        {
            try { return AccessTools.FieldRefAccess<ToolUseComponent, PlayerTool>("_myTool"); }
            catch (Exception) { return null; }
        }

        /// <summary>Wraps a game coroutine so the guard knows when it finished, died or threw.</summary>
        private static IEnumerator Wrap(IEnumerator inner, Token t)
        {
            while (true)
            {
                if (t.Aborted) yield break;
                bool more;
                try { more = inner.MoveNext(); }
                catch (Exception ex)
                {
                    t.Failed = true;
                    Plugin.Log.LogWarning($"Interaction guard: {t.Kind} coroutine threw: {ex.Message}");
                    yield break;
                }
                if (!more) break;
                yield return inner.Current;
            }
            t.Done = true;
        }

        private static Token Track(Kind kind, MonoBehaviour owner, float timeout)
        {
            // A new interaction of the same holder replaces the old one (the old coroutine is gone).
            for (int i = Tokens.Count - 1; i >= 0; i--)
                if (ReferenceEquals(Tokens[i].Owner, owner)) { Tokens[i].Aborted = true; Tokens.RemoveAt(i); }
            var t = new Token { Kind = kind, Owner = owner, Start = Time.unscaledTime, Timeout = timeout };
            Tokens.Add(t);
            return t;
        }

        private static bool InputBlocked =>
            Singleton<InputManager>.IsInstanced() && Singleton<InputManager>.Instance.AllInputBlocked;

        /// <summary>Called from Plugin.Update.</summary>
        internal static void Tick()
        {
            if (Tokens.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = Tokens.Count - 1; i >= 0; i--)
            {
                Token t = Tokens[i];
                bool remove;
                try { remove = Check(t, now); }
                catch (Exception ex)
                {
                    remove = true;
                    Plugin.Log.LogWarning("Interaction guard: " + ex.Message);
                }
                if (remove) Tokens.RemoveAt(i);
            }
        }

        /// <summary>True when the token is finished with (normally or recovered).</summary>
        private static bool Check(Token t, float now)
        {
            if (t.Done) return true;
            // The separator is not wrapped (see PatchSeparator): it is over once input is free again.
            if (t.Kind == Kind.Separator && !InputBlocked) return true;
            bool dead = t.Failed || t.Owner == null || !t.Owner.gameObject.activeInHierarchy;
            bool late = now - t.Start > t.Timeout;
            if (!dead && !late) return false;
            t.Aborted = true;
            Plugin.Log.LogWarning($"Interaction guard: {t.Kind} use on {(t.Owner != null ? t.Owner.name : "a destroyed object")} " +
                                  $"{(t.Failed ? "threw" : dead ? "was interrupted (object disabled or removed, e.g. taken by another player)" : "timed out")}" +
                                  ", unlocking the local player");
            Recover(t);
            return true;
        }

        /// <summary>Does what the end of the interrupted coroutine would have done.</summary>
        private static void Recover(Token t)
        {
            try
            {
                if (t.Kind == Kind.Shovel) RecoverShovel(t.Owner as ShovelToolHolder);
                else if (t.Kind == Kind.SmallShovel) RecoverSmallShovel(t.Owner as ShovelSmallToolHolder);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Interaction guard: tool recovery failed: " + ex.Message); }
            finally
            {
                // Always free the player, even if the tool could not be put back.
                if (Singleton<InputManager>.IsInstanced()) Singleton<InputManager>.Instance.AllInputBlocked = false;
            }
            if (t.Kind != Kind.Separator)
            {
                try
                {
                    if (Singleton<GravityGun>.IsInstanced()) Singleton<GravityGun>.Instance.StopUsingObject();
                }
                catch (Exception ex) { Plugin.Log.LogWarning("Interaction guard: StopUsingObject failed: " + ex.Message); }
            }
        }

        private static PlayerTool ToolOf(ToolUseComponent c)
        {
            if (c == null || MyToolRef == null) return null;
            PlayerTool tool = MyToolRef(c);
            return tool != null ? tool : null;
        }

        private static bool LocallyPicked(PlayerTool tool) =>
            Singleton<ToolGun>.IsInstanced() && Singleton<ToolGun>.Instance.PickedObject == tool.gameObject;

        private static void RecoverShovel(ShovelToolHolder h)
        {
            if (h != null) h.isUnscrewing = false;
            Shovel shovel = ToolOf(h) as Shovel;
            if (shovel == null) return;
            if (shovel.DropCorutine != null)
            {
                // Its throw coroutine (hog pan path) died with it, half done: the dirt it still holds was
                // partly dropped already, so empty it (as the coroutine's end does) instead of duplicating.
                try { shovel.StopCoroutine(shovel.DropCorutine); } catch (Exception) { }
                shovel.DropCorutine = null;
                shovel.CurrentVolume = 0f;
                shovel.Minerals = new ResourceBag();
            }
            // Still parented to the (inactive) holder: put it back in the player's hands.
            if (!LocallyPicked(shovel) && (h == null || shovel.transform.IsChildOf(h.transform)))
                shovel.SnapToPlayer();
            if (shovel.gameObject.activeInHierarchy) shovel.SetDirtOnShovel(shovel.CurrentVolume > 0f);
        }

        private static void RecoverSmallShovel(ShovelSmallToolHolder h)
        {
            if (h != null) h.IsWorking = false;
            ShovelSmall shovel = ToolOf(h) as ShovelSmall;
            if (shovel == null) return;
            bool snapped = h != null && h.ShovelSnap != null && shovel.transform.IsChildOf(h.ShovelSnap);
            if (shovel.IsAnimating || snapped)
            {
                // Its WaitForAnim coroutine ends with SnapToPlayer + IsAnimating = false.
                shovel.IsAnimating = false;
                if (!LocallyPicked(shovel)) shovel.SnapToPlayer();
            }
        }

        // =====================================================================================
        // Harmony
        // =====================================================================================

        [HarmonyPatch]
        private static class PatchShovelHolder
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(ShovelToolHolder), "Unscrewing");

            private static bool Prepare()
            {
                if (Target != null && MyToolRef != null) return true;
                Plugin.Log.LogWarning("Interaction guard: ShovelToolHolder.Unscrewing / _myTool not found, an interrupted shovel use may lock the player");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Postfix(ShovelToolHolder __instance, ref IEnumerator __result)
            {
                if (__result == null || !Net.Active) return;
                // Unscrewing is a fixed 1 s; 6 s means it is stuck.
                __result = Wrap(__result, Track(Kind.Shovel, __instance, 6f));
            }
        }

        [HarmonyPatch]
        private static class PatchSmallShovelHolder
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(ShovelSmallToolHolder), "Unscrewing");

            private static bool Prepare()
            {
                if (Target != null && MyToolRef != null) return true;
                Plugin.Log.LogWarning("Interaction guard: ShovelSmallToolHolder.Unscrewing not found, an interrupted small shovel use may lock the player");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Postfix(ShovelSmallToolHolder __instance, ref IEnumerator __result)
            {
                if (__result == null || !Net.Active) return;
                // A couple of seconds plus the shovel animation.
                __result = Wrap(__result, Track(Kind.SmallShovel, __instance, 15f));
            }
        }

        // HogPanDirtBox.OnDetach deactivates the box's ShovelToolHolder even in the middle of the
        // local shovel use (HogPanDirtBox.Update itself waits for !isUnscrewing). In co-op the other
        // player can take the box off the hog pan at that moment: leave the holder alone while it is
        // working, Update hides it once the use has finished (the guard above is the safety net).
        [HarmonyPatch]
        private static class PatchDirtBoxDetach
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(HogPanDirtBox), nameof(HogPanDirtBox.OnDetach));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Interaction guard: HogPanDirtBox.OnDetach not found");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Prefix(HogPanDirtBox __instance, out ShovelToolHolder __state)
            {
                __state = null;
                ShovelToolHolder h = __instance.ShovelHolder;
                if (h != null && h.isUnscrewing)
                {
                    __state = h;
                    __instance.ShovelHolder = null; // OnDetach skips a missing holder
                }
            }

            private static void Finalizer(HogPanDirtBox __instance, ShovelToolHolder __state)
            {
                if (__state != null) __instance.ShovelHolder = __state;
            }
        }

        // The separator's RunAnimation is a tiny non-virtual iterator factory (may be inlined by the
        // JIT), so the guard hooks AddContentFromBucket, which blocks input and starts it.
        [HarmonyPatch]
        private static class PatchSeparator
        {
            private static MethodBase Target => AccessTools.DeclaredMethod(typeof(BucketHolderMagnetiteSeparator), nameof(BucketHolderMagnetiteSeparator.AddContentFromBucket));

            private static bool Prepare()
            {
                if (Target != null) return true;
                Plugin.Log.LogWarning("Interaction guard: BucketHolderMagnetiteSeparator.AddContentFromBucket not found");
                return false;
            }

            private static MethodBase TargetMethod() => Target;

            private static void Postfix(BucketHolderMagnetiteSeparator __instance)
            {
                if (!Net.Active) return;
                Track(Kind.Separator, __instance, 30f);
            }
        }
    }
}
