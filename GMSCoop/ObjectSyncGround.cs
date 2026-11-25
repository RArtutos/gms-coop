using System.Collections.Generic;
using GoldDigger;
using UnityEngine;

namespace GMSCoop
{
    /// <summary>
    /// Ground guard: loose objects that ended up under the terrain surface on this PC are put back on it.
    ///
    /// Measured in a co-op save (claim ALLOTMENT I, both players digging and shoveling around the hog pan):
    /// the two water pumps sat 1.4-2 m under the ground and the big wash tub had fallen 55 m through the
    /// terrain. A heightfield has no thickness: a body that is more than a little under the surface is not
    /// pushed back up, it falls through (and below y -300 the game's sanity check teleports it to a claim
    /// depot or deactivates / destroys it, on that PC only). In co-op the surface under a resting body
    /// changes without that body being involved: the other player's terrain edits arrive and are applied
    /// (TerrainSync) under sleeping bodies, concurrent edits are host-ordered (a cell can come back to a
    /// higher value), followers are placed at the owner's rest pose (measured on the owner's terrain) and
    /// then handed to local physics asleep, and an area can be loaded around objects that already rest there.
    ///
    /// Round robin over the keyed objects this PC simulates itself (not followed, held, attached, frozen
    /// or kinematic): the surface at the object's x/z is found with a raycast on the loaded Terrain
    /// colliders (terrain holes let it through, so objects in a dig pit measure the pit floor). An object
    /// whose centre is clearly under it (or that is falling through it) is lifted so that its lowest point
    /// is just above the surface; this PC claims it if needed and streams the new pose, so every PC agrees.
    /// Nothing is done while the game is paused (area loading) or while the area at that spot is loading /
    /// unloading; an active object in an area that is not loaded here (no terrain under it) is parked in
    /// that area like the game does, at its last pose that was on the ground.
    /// </summary>
    internal static partial class ObjectSync
    {
        private const int GroundChecksPerTick = 12;      // 20 Hz send ticks: ~240 objects per second
        private const float GroundSunkDepth = 0.75f;     // centre this far under the surface: buried
        private const float GroundFallDepth = 0.1f;      // ...or this far while falling (through the surface)
        private const float GroundFallSpeed = 2f;        // downward speed that counts as falling
        private const float GroundLift = 0.05f;          // lowest point above the surface after the fix
        private const int GroundConfirm = 2;             // consecutive checks before acting

        private static int _groundCursor;
        private static int _groundLogs;
        private static readonly Dictionary<Entry, int> GroundHits = new Dictionary<Entry, int>();
        private static readonly Dictionary<Entry, Vector3> GroundGood = new Dictionary<Entry, Vector3>();
        private static readonly List<Collider> _groundCols = new List<Collider>();

        /// <summary>Called from Tick at the send rate.</summary>
        private static void GroundGuardTick()
        {
            if (All.Count == 0)
            {
                if (GroundHits.Count > 0) GroundHits.Clear();
                if (GroundGood.Count > 0) GroundGood.Clear();
                return;
            }
            if (Time.timeScale < 0.01f || GameState.IsLoading) return; // area loading pauses the game
            int budget = Mathf.Min(GroundChecksPerTick, All.Count);
            for (int i = 0; i < budget; i++)
            {
                if (_groundCursor >= All.Count) _groundCursor = 0;
                Entry e = All[_groundCursor++];
                try { CheckGround(e); }
                catch (System.Exception ex) { VehicleTraffic.Warn("ground guard", "Object sync: ground check failed: " + ex.Message); }
            }
        }

        private static void CheckGround(Entry e)
        {
            if (e.Go == null || e.Rb == null)
            {
                GroundHits.Remove(e);
                GroundGood.Remove(e);
                return;
            }
            Rigidbody rb = e.Rb;
            // Only copies this PC simulates: the owner streams followed / held objects, holders and beds carry theirs.
            if (e.Part || e.Following || e.HeldBy != 0 || e == _localEntry || e.Attached || rb.isKinematic ||
                !e.Go.activeSelf || Frozen(e) || Active.Contains(e))
            {
                GroundHits.Remove(e);
                return;
            }

            Vector3 pos = rb.position;
            AreaStreamer area = AreaAt(pos);
            if (area != null && (area.loading || area.unloading || area.unloadingwait)) { GroundHits.Remove(e); return; }
            if (!e.Go.activeInHierarchy) { GroundHits.Remove(e); return; } // parked in an unloaded area / inactive parent
            if (area != null && area.GameplayObjects != null && !area.GameplayObjects.gameObject.activeInHierarchy)
            {
                // Active in an area that is not loaded here: nothing to stand on. Park it there (inactive) at
                // its last pose on the ground, as the game keeps that area's objects.
                if (!Bump(e)) return;
                if (GroundGood.TryGetValue(e, out Vector3 good) && good.y > pos.y + 1f) MoveBody(e, good);
                ParkInUnloadedArea(e.Go, rb.position);
                if (_groundLogs++ < 20)
                    Plugin.Log.LogInfo($"Object sync: {e.Go.name} ({e.Key}) was loose in unloaded area {area.sceneName}, parked there");
                return;
            }

            if (!TryBounds(e.Go, out Bounds b)) return;
            if (!SurfaceAt(b.center, out float surface)) { GroundHits.Remove(e); return; }

            float depth = surface - b.center.y;
            bool sunk = depth > GroundSunkDepth || (depth > GroundFallDepth && rb.velocity.y < -GroundFallSpeed);
            if (!sunk)
            {
                GroundHits.Remove(e);
                if (b.min.y > surface - 0.25f && (rb.IsSleeping() || rb.velocity.sqrMagnitude < 0.01f)) GroundGood[e] = pos; // resting on the ground
                return;
            }
            if (!Bump(e)) return;

            if (e.Owner != Net.LocalId)
            {
                if (_claimBudget <= 0) return; // next round
                _claimBudget--;
                LocalClaim(e, grab: false);
            }
            float lift = surface + GroundLift - b.min.y;
            MoveBody(e, pos + Vector3.up * lift);
            rb.WakeUp();
            e.Streaming = true; // stream the correction, then the rest pose
            e.NeedRest = true;
            e.StillTime = 0f;
            GroundHits.Remove(e);
            if (_groundLogs++ < 50)
                Plugin.Log.LogInfo($"Object sync: {e.Go.name} ({e.Key}) was {depth:0.00} m under the ground at {pos}, put back on the surface");
        }

        // ---------------------------------------------------------------- remote terrain edits
        // TerrainSync writes another player's heights with SetHeightsDelayLOD and rebuilds the collider once per
        // frame (SyncHeightmap). Around that rebuild: TerrainEditBegin measures the loose objects in the edited
        // area on the OLD collider surface, TerrainEditEnd on the new one. Where the ground rose under an object
        // that rested on it, the object is lifted by the rise (rotation kept, no downward speed) and this PC
        // claims and streams it; where it dropped, the object is woken so it settles into the pit (instead of
        // floating asleep). The game itself only keeps dirt from being added under machines
        // (CantDigUnderMachine) for its own local digs, which are left as they are.

        private struct EditCandidate
        {
            public Entry E;
            public float OldSurface;
        }

        private const float EditMargin = 0.5f;     // around the edited cells (x/z)
        private const float EditHeight = 4f;       // above / below the edited heights
        private const float EditRestGap = 0.3f;    // lowest point at most this far above the old surface: resting on it
        private const int MaxEditLifts = 32;       // per rebuild

        private static readonly List<EditCandidate> _editCands = new List<EditCandidate>();
        private static readonly HashSet<Entry> _editSeen = new HashSet<Entry>();
        private static readonly Collider[] _editHits = new Collider[256];
        private static int _editLogs;

        /// <summary>TerrainSync, before rebuilding a tile's collider: remember the loose objects in <paramref name="area"/> and their surface.</summary>
        internal static void TerrainEditBegin(Bounds area)
        {
            if (!Net.Active || !_indexed || !Ready) return;
            Vector3 half = area.extents + new Vector3(EditMargin, EditHeight, EditMargin);
            int n = Physics.OverlapBoxNonAlloc(area.center, half, _editHits, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider col = _editHits[i];
                _editHits[i] = null;
                Rigidbody rb = col != null ? col.attachedRigidbody : null;
                if (rb == null || rb.isKinematic || !ByInstance.TryGetValue(rb.gameObject.GetInstanceID(), out Entry e)) continue;
                if (!_editSeen.Add(e) || !EditMovable(e)) continue;
                if (!TryBounds(e.Go, out Bounds b) || !SurfaceAt(b.center, out float surface)) continue;
                _editCands.Add(new EditCandidate { E = e, OldSurface = surface });
            }
        }

        /// <summary>TerrainSync, after the collider rebuild: lift what the ground rose under, wake what it dropped under.</summary>
        internal static void TerrainEditEnd()
        {
            if (_editCands.Count == 0) { _editSeen.Clear(); return; }
            int lifts = 0;
            try
            {
                foreach (EditCandidate c in _editCands)
                {
                    Entry e = c.E;
                    if (e.Go == null || e.Rb == null || !EditMovable(e)) continue;
                    if (!TryBounds(e.Go, out Bounds b) || !SurfaceAt(b.center, out float surface)) continue;
                    float rise = surface - c.OldSurface;
                    if (rise < -0.02f)
                    {
                        e.Rb.WakeUp(); // the ground dropped under it: let it settle into the pit
                        continue;
                    }
                    // Lift only what rested on the old surface and is now under the new one.
                    if (rise <= 0.02f || b.min.y > c.OldSurface + EditRestGap || surface <= b.min.y) continue;
                    if (lifts >= MaxEditLifts) break;
                    lifts++;
                    if (e.Owner != Net.LocalId) LocalClaim(e, grab: false);
                    float lift = Mathf.Min(rise, surface - b.min.y) + GroundLift;
                    Rigidbody rb = e.Rb;
                    Vector3 v = rb.velocity;
                    if (v.y < 0f) v.y = 0f;
                    Vector3 p = rb.position + Vector3.up * lift;
                    rb.position = p;
                    e.Go.transform.position = p;
                    rb.velocity = v;
                    rb.WakeUp();
                    e.Streaming = true;
                    e.NeedRest = true;
                    e.StillTime = 0f;
                    if (_editLogs++ < 30)
                        Plugin.Log.LogInfo($"Object sync: {e.Go.name} ({e.Key}) lifted {lift:0.00} m, the other player's terrain edit raised the ground under it");
                }
            }
            finally
            {
                _editCands.Clear();
                _editSeen.Clear();
            }
        }

        /// <summary>A keyed loose object this PC may move: not followed / held / attached / frozen, not a rope part.</summary>
        private static bool EditMovable(Entry e) =>
            !e.Part && !e.Following && e.HeldBy == 0 && e != _localEntry && !e.Attached && !e.Rb.isKinematic &&
            e.Go.activeInHierarchy && !Frozen(e);

        /// <summary>Counts a bad verdict; true once it was seen GroundConfirm times in a row.</summary>
        private static bool Bump(Entry e)
        {
            GroundHits.TryGetValue(e, out int n);
            n++;
            if (n < GroundConfirm) { GroundHits[e] = n; return false; }
            GroundHits.Remove(e);
            return true;
        }

        private static void MoveBody(Entry e, Vector3 pos)
        {
            Rigidbody rb = e.Rb;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.position = pos;
            e.Go.transform.position = pos;
        }

        /// <summary>World bounds of the object's solid colliders.</summary>
        private static bool TryBounds(GameObject go, out Bounds b)
        {
            b = default;
            bool any = false;
            go.GetComponentsInChildren(false, _groundCols);
            foreach (Collider c in _groundCols)
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                if (!any) { b = c.bounds; any = true; }
                else b.Encapsulate(c.bounds);
            }
            _groundCols.Clear();
            return any;
        }

        /// <summary>Loaded terrain surface (nearest to <paramref name="p"/>) at <paramref name="p"/>'s x/z (terrain holes let the raycast through).</summary>
        private static bool SurfaceAt(Vector3 p, out float surface)
        {
            surface = 0f;
            bool found = false;
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null) return false;
            foreach (Terrain t in terrains)
            {
                if (t == null || t.terrainData == null || !t.isActiveAndEnabled) continue;
                Vector3 o = t.GetPosition();
                Vector3 size = t.terrainData.size;
                if (p.x < o.x || p.z < o.z || p.x > o.x + size.x || p.z > o.z + size.z) continue;
                // TerrainCollider (by name: its module is not referenced).
                Collider tc = null;
                foreach (Collider c in t.GetComponents<Collider>())
                    if (c != null && c.enabled && c.GetType().Name == "TerrainCollider") { tc = c; break; }
                if (tc == null) continue;
                var ray = new Ray(new Vector3(p.x, o.y + size.y + 10f, p.z), Vector3.down);
                // Several tiles can cover one spot (a claim's dig terrain inside the main terrain): the surface
                // nearest to the object is the one it belongs to (an object in a deep dig pit is not "under"
                // the main terrain above the pit).
                if (tc.Raycast(ray, out RaycastHit hit, size.y + 20f) &&
                    (!found || Mathf.Abs(hit.point.y - p.y) < Mathf.Abs(surface - p.y)))
                {
                    surface = hit.point.y;
                    found = true;
                }
            }
            return found;
        }
    }
}
