using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Gsm = GoldDigger.GameStateManager;

namespace GMSCoop
{
    /// <summary>
    /// Test only (--coop-test, never active in normal play): the host "plays" a fixed list of scenarios through
    /// the game's own code paths (cash, tutorial step, terrain dig, grab and drop an object, load the truck
    /// bed, drive the truck, buy a shop item). Both PCs log "TEST &lt;tag&gt; &lt;utc&gt; &lt;values&gt;" fingerprints of the
    /// tracked things every 0.25 s; the host logs "TESTEV &lt;utc&gt; &lt;event&gt;" for each step. The tracked list is
    /// shared through a file in %TEMP% (both test instances run on one PC). Comparing the logs gives delay,
    /// error and final state per scenario.
    /// </summary>
    internal static class TestScenarios
    {
        internal static bool Enabled;

        private static readonly string TrackFile = Path.Combine(Path.GetTempPath(), "gmscoop_test_track.txt");
        private static readonly List<string> Tracked = new List<string>();
        private static bool _started;
        private static float _nextLog, _nextRead;
        private static Dictionary<string, float> _axes; // InputManager.GetAxis overrides while driving
        private static bool _clientDrove;
        private static string _lastGoto;                 // client: last "goto" line of the track file applied

        private static string Now => DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        private static void Ev(string s) => Plugin.Log.LogInfo("TESTEV " + Now + " " + s);
        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
        private static string V(Vector3 p) => F(p.x) + " " + F(p.y) + " " + F(p.z);

        internal static void Tick()
        {
            if (!Net.Active || GameState.IsLoading) return;
            if (Net.IsHost)
            {
                if (!_started)
                {
                    bool ready = false;
                    foreach (ulong p in Net.Peers) if (SaveTransfer.PeerReady(p)) ready = true;
                    if (ready)
                    {
                        _started = true;
                        try { File.WriteAllText(TrackFile, ""); } catch { }
                        PatchInput();
                        Plugin.Instance.StartCoroutine(Run());
                    }
                }
            }
            else if (Time.unscaledTime >= _nextRead)
            {
                _nextRead = Time.unscaledTime + 1f;
                try
                {
                    if (File.Exists(TrackFile))
                    {
                        Tracked.Clear();
                        foreach (string l in File.ReadAllLines(TrackFile)) if (l.Length > 0) Tracked.Add(l);
                        if (!_clientDrove && Tracked.Contains("clientdrive"))
                        {
                            _clientDrove = true;
                            PatchInput();
                            Plugin.Instance.StartCoroutine(Step("client-vehicle", Drive()));
                        }
                        string go = Tracked.LastOrDefault(l => l.StartsWith("goto ", StringComparison.Ordinal));
                        if (go != null && go != _lastGoto)
                        {
                            _lastGoto = go;
                            string[] g = go.Split(' ');
                            MoveWalker(new Vector3(float.Parse(g[1], CultureInfo.InvariantCulture), float.Parse(g[2], CultureInfo.InvariantCulture), float.Parse(g[3], CultureInfo.InvariantCulture)));
                            Plugin.Log.LogInfo("TESTEV " + Now + " client goto " + go);
                        }
                    }
                }
                catch { }
            }
            if (Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + 0.25f;
                LogFingerprints();
            }
        }

        private static void Track(string what)
        {
            if (Tracked.Contains(what)) return;
            Tracked.Add(what);
            try { File.WriteAllLines(TrackFile, Tracked.ToArray()); } catch { }
        }

        // ---------- fingerprints (same code on both PCs) ----------

        private static void LogFingerprints()
        {
            string now = Now;
            var sb = new StringBuilder();
            // FixedCash's == / != operators throw on null: compare as object.
            object cash = Singleton<Gsm>.IsInstanced() ? Singleton<Gsm>.Instance.Cash : null;
            if (cash != null)
                Plugin.Log.LogInfo("TEST cash " + now + " " + Singleton<Gsm>.Instance.Cash.Value.ToString(CultureInfo.InvariantCulture));
            if (Singleton<TutorialRunner>.IsInstanced())
                Plugin.Log.LogInfo("TEST step " + now + " " + (Singleton<TutorialRunner>.Instance.CurrentStep?.StepId ?? "none"));
            foreach (string t in Tracked)
            {
                string[] f = t.Split(' ');
                try
                {
                    switch (f[0])
                    {
                        case "m":
                        {
                            MachineController m = MachineSync.Get(int.Parse(f[1]));
                            if (m == null) { Plugin.Log.LogInfo("TEST " + f[0] + ":" + f[1] + " " + now + " missing"); break; }
                            Plugin.Log.LogInfo("TEST m:" + f[1] + " " + now + " " + V(m.transform.position) + " rd=" + (MachineSync.IsRemoteDriven(m) ? 1 : 0));
                            break;
                        }
                        case "o":
                        {
                            GameObject go = ObjectSync.Find(f[1]);
                            if (go == null) { Plugin.Log.LogInfo("TEST o:" + f[1] + " " + now + " missing"); break; }
                            Plugin.Log.LogInfo("TEST o:" + f[1] + " " + now + " " + V(go.transform.position) + " act=" + (go.activeInHierarchy ? 1 : 0) + " " + ObjectSync.DebugState(f[1]));
                            break;
                        }
                        case "t":
                        {
                            float x = float.Parse(f[2], CultureInfo.InvariantCulture), z = float.Parse(f[3], CultureInfo.InvariantCulture);
                            Terrain ter = Terrain.activeTerrains.FirstOrDefault(a => a != null && a.name == f[1]);
                            if (ter == null) { Plugin.Log.LogInfo("TEST t:" + f[1] + ":" + f[2] + ":" + f[3] + " " + now + " missing"); break; }
                            float h = ter.SampleHeight(new Vector3(x, 0f, z)) + ter.transform.position.y;
                            Plugin.Log.LogInfo("TEST t:" + f[1] + ":" + f[2] + ":" + f[3] + " " + now + " " + F(h));
                            break;
                        }
                    }
                }
                catch (Exception ex) { Plugin.Log.LogInfo("TEST " + t.Replace(' ', ':') + " " + now + " error " + ex.Message); }
            }
            // A client also logs the avatar it shows for the host.
            if (!Net.IsHost)
                foreach (var tr in UnityEngine.Object.FindObjectsOfType<Transform>())
                    if (tr.parent == null && tr.name.StartsWith("CoopAvatar_", StringComparison.Ordinal))
                    {
                        Plugin.Log.LogInfo("TEST avatar " + now + " " + V(tr.position));
                        break;
                    }
            if (Net.IsHost && Singleton<RigidbodyFPSWalker>.IsInstanced())
                Plugin.Log.LogInfo("TEST avatar " + now + " " + V(Singleton<RigidbodyFPSWalker>.Instance.transform.position));
        }

        // ---------- input override for driving ----------

        private static void PatchInput()
        {
            try
            {
                var h = new Harmony(Plugin.Guid + ".test");
                h.Patch(AccessTools.Method(typeof(InputManager), nameof(InputManager.GetAxis), new[] { typeof(string) }),
                    prefix: new HarmonyMethod(typeof(TestScenarios), nameof(AxisPrefix)));
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Test: input patch failed: " + ex.Message); }
        }

        private static bool AxisPrefix(string axisCode, ref float __result)
        {
            if (_axes == null || axisCode == null || !_axes.TryGetValue(axisCode, out float v)) return true;
            __result = v;
            return false;
        }

        // ---------- scenarios (host) ----------

        private static Transform Walker => Singleton<RigidbodyFPSWalker>.IsInstanced() ? Singleton<RigidbodyFPSWalker>.Instance.transform : null;

        private static void MoveWalker(Vector3 pos)
        {
            Transform w = Walker;
            if (w == null) return;
            var rb = w.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.position = pos; }
            w.position = pos;
        }

        private static IEnumerator Run()
        {
            Ev("begin");
            yield return new WaitForSeconds(8f);
            yield return Step("cash", Cash());
            yield return Step("tutorial", Tutorial());
            yield return Step("purchase", Purchase());
            yield return Step("object", GrabObject());
            yield return Step("bed", LoadBed());
            yield return Step("vehicle", Drive());
            yield return new WaitForSeconds(15f); // the truck settles; rest poses
            // Same drive from the client (host watches, relays and owns the parked truck afterwards).
            Ev("client-vehicle requested");
            Track("clientdrive");
            yield return new WaitForSeconds(45f);
            yield return Step("dig", Dig());
            yield return new WaitForSeconds(5f);
            Ev("done");
        }

        /// <summary>Runs one scenario; an exception ends that scenario only (logged), the next one still runs.</summary>
        private static IEnumerator Step(string name, IEnumerator body)
        {
            Ev(name + " start");
            while (true)
            {
                object cur;
                try
                {
                    if (!body.MoveNext()) break;
                    cur = body.Current;
                }
                catch (Exception ex)
                {
                    Ev(name + " exception " + ex.GetType().Name + ": " + ex.Message.Replace('\n', ' '));
                    Plugin.Log.LogWarning("Test " + name + ": " + ex);
                    break;
                }
                yield return cur;
            }
            _axes = null;
            Ev(name + " end");
            yield return new WaitForSeconds(4f);
        }

        private static IEnumerator Cash()
        {
            Gsm g = Singleton<Gsm>.Instance;
            decimal before = g.Cash.Value;
            g.Cash = new FixedCash(before + 1234m);
            Ev("cash set " + before.ToString(CultureInfo.InvariantCulture) + " -> " + g.Cash.Value.ToString(CultureInfo.InvariantCulture));
            yield return new WaitForSeconds(2f);
        }

        private static IEnumerator Tutorial()
        {
            if (!Singleton<TutorialRunner>.IsInstanced() || Singleton<TutorialRunner>.Instance.CurrentStep == null)
            {
                Ev("tutorial skipped (no running step)");
                yield break;
            }
            string from = Singleton<TutorialRunner>.Instance.CurrentStep.StepId;
            Singleton<TutorialRunner>.Instance.SkipStep();
            Ev("tutorial skip " + from + " -> " + (Singleton<TutorialRunner>.Instance.CurrentStep?.StepId ?? "none"));
            yield return new WaitForSeconds(2f);
        }


        /// <summary>
        /// Dig with the game's own function (GoldTerrainDigger.DigWithBox, what shovels, buckets and explosions
        /// call) on the nearest parcel terrain. The test save (a fresh tutorial game) owns no parcel: the walker
        /// goes there (its area loads) and the game's debug option DigEverywhere allows the dig.
        /// </summary>
        private static IEnumerator Dig()
        {
            Vector3 me = Walker.position;
            GoldTerrainDigger best = null;
            float bestD = float.MaxValue;
            int all = 0;
            foreach (GoldTerrainDigger d in Resources.FindObjectsOfTypeAll<GoldTerrainDigger>())
            {
                if (d == null || !d.gameObject.scene.IsValid() || d.GetComponent<Terrain>() == null) continue;
                all++;
                float dd = (d.transform.position - me).sqrMagnitude - (d.IsOwner ? 1e9f : 0f);
                if (dd < bestD) { bestD = dd; best = d; }
            }
            Ev("dig parcels in the world " + all);
            if (best == null) { Ev("dig skipped (no parcel terrain)"); yield break; }
            Terrain ter = best.GetComponent<Terrain>();
            Vector3 o = ter.transform.position, size = ter.terrainData.size;
            Vector3 spot = new Vector3(o.x + size.x * 0.5f, 0f, o.z + size.z * 0.5f);
            spot.y = ter.SampleHeight(spot) + o.y;
            MoveWalker(spot + new Vector3(4f, 2f, 0f));
            for (int i = 0; i < 40 && !best.isActiveAndEnabled; i++) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(4f);
            spot.y = ter.SampleHeight(spot) + o.y;
            MoveWalker(spot + new Vector3(4f, 1.5f, 0f));
            Track("goto " + F(spot.x + 6f) + " " + F(spot.y + 1.5f) + " " + F(spot.z)); // the client walks there too (its tile loads)
            Ev("dig parcel " + ter.name + " active " + best.isActiveAndEnabled + " owner " + best.IsOwner + " at " + V(spot));
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                    Track("t " + ter.name + " " + F(spot.x + i * 0.7f) + " " + F(spot.z + j * 0.7f));
            yield return new WaitForSeconds(3f);

            bool oldDig = SROptions.Current != null && SROptions.Current.DigEverywhere;
            if (SROptions.Current != null) SROptions.Current.DigEverywhere = true;
            var box = new GameObject("CoopTestDigBox");
            var col = box.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(2.5f, 2.5f, 0.5f);
            float total = 0f;
            try
            {
                for (int k = 0; k < 12; k++)
                {
                    // a little lower each pass: each call digs a new layer (and differs from lastpos)
                    box.transform.position = spot + Vector3.down * (0.1f + k * 0.06f);
                    Vector3 last = box.transform.position + Vector3.up;
                    total += best.DigWithBox(ref last, col, col.size.x, col.size.y, 0.25f, 1000000f).Dirt;
                    yield return new WaitForSeconds(0.3f);
                }
            }
            finally
            {
                UnityEngine.Object.Destroy(box);
                if (SROptions.Current != null) SROptions.Current.DigEverywhere = oldDig;
            }
            yield return new WaitForSeconds(1f);
            Ev("dig done volume " + F(total) + " new h " + F(ter.SampleHeight(spot) + o.y));
            yield return new WaitForSeconds(4f);
        }

        private static readonly List<GameObject> Bought = new List<GameObject>();

        private static IEnumerator GrabObject()
        {
            GameObject go = Bought.Count > 0 ? Bought[0] : null;
            if (go == null) { Ev("object skipped (nothing bought)"); yield break; }
            string key = ObjectSync.KeyOf(go);
            Track("o " + key);
            // The shop is far from the tutorial start: walk there and let its area load.
            MoveWalker(go.transform.position + new Vector3(1.2f, 1f, 0f));
            for (int i = 0; i < 30 && !go.activeInHierarchy; i++) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(3f);
            MoveWalker(go.transform.position + new Vector3(1.2f, 1f, 0f));
            yield return new WaitForSeconds(1f);
            Ev("object " + go.name + " key " + key + " active " + go.activeInHierarchy + " at " + V(go.transform.position));
            GravityGun gun = Singleton<GravityGun>.Instance;
            gun.PickObject(go);
            Ev("object picked " + (gun.PickedObject == go));
            Vector3 start = Walker.position;
            for (int i = 0; i < 40; i++) // walk 6 m while holding it
            {
                MoveWalker(start + new Vector3(0f, 0f, i * 0.15f));
                yield return new WaitForSeconds(0.1f);
            }
            gun.DropObject(false);
            Ev("object dropped at " + V(go.transform.position));
            yield return new WaitForSeconds(5f);
            Ev("object rest " + V(go.transform.position));
        }

        private static Pickup Truck => Pickup.MainPickup != null ? Pickup.MainPickup.GetComponent<Pickup>() : UnityEngine.Object.FindObjectOfType<Pickup>();

        private static IEnumerator LoadBed()
        {
            Pickup truck = Truck;
            if (truck == null || truck.MyTransportBed == null) { Ev("bed skipped (no pickup)"); yield break; }
            Track("m " + MachineSync.IdOf(truck));
            Transform bed = truck.MyTransportBed.transform;
            Ev("bed truck id " + MachineSync.IdOf(truck) + " at " + V(truck.transform.position));
            GravityGun gun = Singleton<GravityGun>.Instance;
            for (int n = 1; n < Bought.Count && n <= 2; n++)
            {
                GameObject go = Bought[n];
                if (go == null) continue;
                string key = ObjectSync.KeyOf(go);
                Track("o " + key);
                MoveWalker(go.transform.position + new Vector3(1.2f, 1f, 0f));
                yield return new WaitForSeconds(1f);
                gun.PickObject(go);
                Ev("bed picked " + go.name + " " + (gun.PickedObject == go));
                yield return new WaitForSeconds(0.6f);
                gun.DropObject(false);
                // carried to the truck: put it on the bed
                MoveWalker(truck.transform.position + truck.transform.right * -2.5f + Vector3.up);
                var rb = go.GetComponent<Rigidbody>();
                Vector3 p = bed.position + bed.up * 0.8f + bed.right * (n == 1 ? -0.35f : 0.35f);
                if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = p; }
                go.transform.position = p;
                Ev("bed placed " + go.name + " key " + key + " at " + V(p));
                yield return new WaitForSeconds(1.5f);
            }
            MoveWalker(truck.transform.position + truck.transform.right * -2.5f + Vector3.up);
            yield return new WaitForSeconds(4f);
            foreach (GameObject go in Bought) if (go != null) Ev("bed item " + ObjectSync.KeyOf(go) + " at " + V(go.transform.position));
        }

        private static readonly PropertyInfo Handbrake = AccessTools.Property(typeof(MachineController), nameof(MachineController.HandbrakeOn));

        private static IEnumerator Drive()
        {
            Pickup truck = Truck;
            if (truck == null) { Ev("vehicle skipped (no pickup)"); yield break; }
            int id = MachineSync.IdOf(truck);
            Track("m " + id);
            MoveWalker(truck.transform.position + truck.transform.right * -2.5f + Vector3.up);
            yield return new WaitForSeconds(1f);
            Vector3 start = truck.transform.position;
            truck.EnterToVehicleNoAnim(0);
            yield return new WaitForSeconds(2.5f);
            Ev("vehicle entered controlled=" + (Singleton<Player>.Instance.GetControlledMachine() == truck) + " local=" + MachineSync.LocallyDriven(truck));
            truck.StartEngine(true);
            yield return new WaitForSeconds(2f);
            if (truck.HandbrakeOn) Handbrake?.GetSetMethod(true)?.Invoke(truck, new object[] { false });
            Ev("vehicle engine=" + truck.IsEngineStarted + " handbrake=" + truck.HandbrakeOn);
            _axes = new Dictionary<string, float> { { truck.AccelCode, 1f }, { truck.SteeringCode, 0f } };
            yield return new WaitForSeconds(5f);
            _axes[truck.SteeringCode] = 0.6f;
            yield return new WaitForSeconds(3f);
            _axes[truck.SteeringCode] = 0f;
            Ev("vehicle braking speed " + F(truck.GetVelocity()) + " moved " + F(Vector3.Distance(start, truck.transform.position)));
            // brake while rolling forward; never hold "back" once stopped (that would reverse)
            for (int i = 0; i < 50; i++)
            {
                _axes[truck.AccelCode] = truck.GetVelocityNotABS() > 0.3f ? -1f : 0f;
                if (truck.GetVelocity() < 0.3f) break;
                yield return new WaitForSeconds(0.1f);
            }
            _axes = null;
            yield return new WaitForSeconds(1.5f);
            Ev("vehicle moved " + F(Vector3.Distance(start, truck.transform.position)) + " m, at " + V(truck.transform.position));
            truck.EnterVehicle.ExitMachine(noAnim: true);
            yield return new WaitForSeconds(3f);
            Ev("vehicle exited controlled=" + (Singleton<Player>.Instance.GetControlledMachine() != null) + " at " + V(truck.transform.position));
            foreach (GameObject go in Bought) if (go != null) Ev("vehicle bed item " + ObjectSync.KeyOf(go) + " at " + V(go.transform.position));
        }

        private static IEnumerator Purchase()
        {
            BuyableObject pick = null;
            int bestPrice = int.MaxValue;
            foreach (BuyableObject b in Resources.FindObjectsOfTypeAll<BuyableObject>())
            {
                if (b == null || !b.gameObject.scene.IsValid() || b.SellPrefab == null || b.BuyItem == null || b.BuyItem.Cash == null) continue;
                if (b.BuyItem.Cash.isWarehouse || b.SellPrefab.IsTrailer() || b.SellPrefab.MyDescriptor == null) continue;
                var cat = b.SellPrefab.MyDescriptor.ItemCategory;
                if (cat == ItemDescriptor.EItemCategory.MACHINE || cat == ItemDescriptor.EItemCategory.VEHICLE) continue;
                int price = b.SellPrefab.MyDescriptor.Price;
                if (price > 0 && price < bestPrice) { bestPrice = price; pick = b; }
            }
            if (pick == null) { Ev("purchase skipped (no shop item)"); yield break; }
            CashRegisterGUI reg = pick.BuyItem.Cash;
            var before = new HashSet<int>(AllSellables().Select(s => s.gameObject.GetInstanceID()));
            Ev("purchase 3x " + pick.SellPrefab.OrginalPrefabName + " price " + bestPrice);
            reg.ObjectsToBuy.Clear();
            reg.AddObjects(pick.SellPrefab, 3);
            decimal cashBefore = Singleton<Gsm>.Instance.Cash.Value;
            try { reg.BuyObjects(); }
            catch (Exception ex) { Ev("purchase BuyObjects threw " + ex.GetType().Name + ": " + ex.Message); }
            Ev("purchase cash " + cashBefore.ToString(CultureInfo.InvariantCulture) + " -> " + Singleton<Gsm>.Instance.Cash.Value.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < 16; i++)
            {
                yield return new WaitForSeconds(0.5f);
                foreach (SellableObject s in AllSellables())
                {
                    if (before.Contains(s.gameObject.GetInstanceID())) continue;
                    string key = ObjectSync.KeyOf(s.gameObject);
                    if (key == null) continue;
                    before.Add(s.gameObject.GetInstanceID());
                    Bought.Add(s.gameObject);
                    Track("o " + key);
                    Ev("purchase spawned " + s.name + " key " + key + " at " + V(s.transform.position) + " active " + s.gameObject.activeInHierarchy);
                }
            }
        }

        private static IEnumerable<SellableObject> AllSellables() =>
            Resources.FindObjectsOfTypeAll<SellableObject>().Where(s => s != null && s.gameObject.scene.IsValid());
    }
}
