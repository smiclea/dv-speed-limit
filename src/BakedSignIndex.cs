using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using DV.Signs;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DVTrainSpeedLimit
{
    internal sealed class BakedSignIndex : IDisposable
    {
        private sealed class AreaJob
        {
            internal string Key;
            internal GameObject Root;
            internal SignGeneratorData[] Components;
            internal int Cursor;
            internal readonly List<CachedPole> Poles = new List<CachedPole>();
            internal double CaptureMs;
        }
        private readonly RegionCache cache = new RegionCache();
        private readonly Queue<AreaJob> jobs = new Queue<AreaJob>();
        private readonly List<Streamer> streamers = new List<Streamer>();
        private readonly Stopwatch slice = new Stopwatch();
        private AreaJob current;
        private Task<SignCacheDocument> load;
        private Task ioTail = Task.CompletedTask;
        private string path, build;
        private bool started, initialized, dirty, publishDirty;
        private float nextProgress;
        private double maxCaptureMs;
        internal bool Ready { get; private set; }
        internal bool Scanning { get { return !initialized || dirty || current != null || jobs.Count > 0 || publishDirty || !ioTail.IsCompleted; } }
        internal int Generation { get; private set; }
        internal int Count { get { return Snapshot == null ? cache.PoleCount : Snapshot.Length; } }
        internal SignGeometry.Sign[] Snapshot { get; private set; }
        internal string Status
        {
            get
            {
                if (!initialized) return "Loading sign cache...";
                if (!Ready) return "Indexing " + cache.AreaCount + " areas / " + cache.PoleCount + " signs...";
                return cache.AreaCount + " cached areas / " + Count + " signs" + (jobs.Count > 0 || current != null ? " (adding new areas)" : "");
            }
        }

        // A session reset reloads the existing disk cache. Only ClearCache deletes it.
        internal void Reset()
        {
            Unsubscribe();
            cache.Clear(); jobs.Clear(); current = null; load = null;
            started = initialized = dirty = publishDirty = Ready = false;
            Snapshot = null; Generation++;
        }
        internal void ClearCache()
        {
            EnsurePaths();
            jobs.Clear(); current = null; cache.Clear(); load = null;
            Snapshot = null; Ready = false; Generation++;
            // Serialize deletion after any older save so an in-flight write cannot
            // restore cleared data. Later saves are queued after this deletion.
            string cachePath = path;
            ioTail = ioTail.ContinueWith(_ => SignCacheDisk.Delete(cachePath), TaskScheduler.Default);
            initialized = true; publishDirty = true;
            if (!started) DiscoverStreamers();
            QueueLoadedAreas(true);
            Main.Log("Entire sign cache cleared; loaded uncached areas queued. No prior cache entries retained.");
        }
        private void EnsurePaths()
        {
            if (path != null) return;
            path = Path.Combine(Main.Entry.Path, "Cache", "signs.xml");
            build = typeof(TrainCar).Module.ModuleVersionId + "/" + typeof(SignGeneratorData).Module.ModuleVersionId + "/" + typeof(RailTrack).Module.ModuleVersionId;
        }
        private void MarkAreasChanged() { dirty = true; }
        private void DiscoverStreamers()
        {
            started = true;
            var timer = Stopwatch.StartNew();
            foreach (var streamer in UnityEngine.Object.FindObjectsOfType<Streamer>())
            {
                streamers.Add(streamer);
                streamer.LoadedScenesChanged += MarkAreasChanged;
            }
            Main.Log("Streamer discovery once per world: managers={0}, elapsed={1:0.00}ms. Area roots queried only when absent from disk cache.", streamers.Count, timer.Elapsed.TotalMilliseconds);
        }
        private void Queue(string key, GameObject root)
        {
            if (root && cache.TryQueue(key)) jobs.Enqueue(new AreaJob { Key = key, Root = root });
        }
        private void QueueLoadedAreas(bool includeStatic)
        {
            dirty = false;
            var streamedScenes = includeStatic ? new HashSet<int>() : null;
            foreach (var streamer in streamers)
            {
                if (!streamer || streamer.loadedScenes == null) continue;
                string collection = streamer.sceneCollection ? streamer.sceneCollection.path + "/" + streamer.sceneCollection.prefixName : streamer.name;
                foreach (var area in streamer.loadedScenes)
                {
                    if (area == null || !area.sceneGo) continue;
                    if (streamedScenes != null) streamedScenes.Add(area.sceneGo.scene.handle);
                    Queue("stream:" + collection + ":" + area.sceneName, area.sceneGo);
                }
            }
            if (!includeStatic) return;
            // Static roots are finite native component queries, not a managed walk
            // through scenery, meshes, UI, train internals and dynamically added nodes.
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || streamedScenes.Contains(scene.handle)) continue;
                var names = new Dictionary<string, int>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    int occurrence; names.TryGetValue(root.name, out occurrence); names[root.name] = occurrence + 1;
                    Queue("static:" + scene.path + ":" + root.name + ":" + occurrence, root);
                }
            }
            Main.Log("Cache coverage: savedAreas={0}, savedSigns={1}, uncached loaded roots queued={2}.", cache.AreaCount, cache.PoleCount, jobs.Count);
        }

        internal void Step()
        {
            EnsurePaths();
            if (ioTail.IsFaulted)
            {
                Main.Log("Sign cache disk operation failed: {0}", ioTail.Exception);
                ioTail = Task.CompletedTask;
            }
            if (!initialized)
            {
                if (load == null)
                {
                    string cachePath = path;
                    load = Task.Run(() => SignCacheDisk.Read(cachePath));
                    return;
                }
                if (!load.IsCompleted) return;
                try
                {
                    var document = load.GetAwaiter().GetResult();
                    if (document != null) cache.Import(document, build);
                    Main.Log("Persistent sign cache loaded: areas={0}, signs={1}, file={2}.", cache.AreaCount, cache.PoleCount, path);
                }
                catch (Exception ex)
                {
                    cache.Clear();
                    Main.Log("Cache unavailable or incompatible; rebuilding uncached areas. {0}", ex.Message);
                }
                load = null; initialized = true;
                if (!started) DiscoverStreamers();
                QueueLoadedAreas(true);
                publishDirty = true;
            }
            if (dirty) QueueLoadedAreas(false);
            slice.Restart();
            for (int work = 0; work < 4 && slice.Elapsed.TotalMilliseconds < 0.5; work++)
            {
                if (current == null)
                {
                    if (jobs.Count == 0) break;
                    current = jobs.Dequeue();
                    if (!current.Root) { cache.Cancel(current.Key); current = null; continue; }
                    var timer = Stopwatch.StartNew();
                    current.Components = current.Root.GetComponentsInChildren<SignGeneratorData>(true);
                    current.CaptureMs = timer.Elapsed.TotalMilliseconds;
                    maxCaptureMs = Math.Max(maxCaptureMs, current.CaptureMs);
                }
                // Parse a finite component array. Decoration count and hierarchy
                // mutations cannot keep this area in an indexing loop.
                int count = 0;
                while (current.Cursor < current.Components.Length && count++ < 64 && slice.Elapsed.TotalMilliseconds < 0.5)
                {
                    var data = current.Components[current.Cursor++];
                    if (data) Add(current.Poles, data);
                }
                if (current.Cursor < current.Components.Length) break;
                cache.Complete(current.Key, current.Poles.ToArray());
                if (current.Poles.Count > 0 || current.CaptureMs > 10)
                    Main.Log("Area cached: key={0}, poles={1}, componentCapture={2:0.00}ms.", current.Key, current.Poles.Count, current.CaptureMs);
                current = null; publishDirty = true;
            }
            if (current != null || jobs.Count > 0)
            {
                if (Time.unscaledTime >= nextProgress)
                {
                    Main.Log("Area indexing progress: completed={0}, pending={1}, signs={2}, current={3}.", cache.AreaCount, cache.PendingCount, cache.PoleCount, current == null ? "next area" : current.Key);
                    nextProgress = Time.unscaledTime + 5;
                }
                return;
            }
            if (!publishDirty) return;
            var snapshot = cache.Signs();
            // Empty/overlapping newly cached areas must not invalidate every
            // directional profile or restart geometry work already in progress.
            if (Snapshot == null || Snapshot.Length != snapshot.Length)
            { Snapshot = snapshot; Generation++; }
            Ready = true; publishDirty = false;
            var documentToSave = cache.Export(build);
            string savePath = path;
            ioTail = ioTail.ContinueWith(_ => SignCacheDisk.Write(savePath, documentToSave), TaskScheduler.Default);
            Main.Log("Sign cache ready: areas={0}, uniqueSigns={1}, largest area component query={2:0.000}ms. Saved on worker; cached areas will not be scanned again.", cache.AreaCount, Snapshot.Length, maxCaptureMs);
        }

        private static void Add(List<CachedPole> poles, SignGeneratorData data)
        {
            if (data.signParameters == null) return;
            var speeds = new List<int>();
            foreach (var parameter in data.signParameters)
            {
                if (parameter.type != SignType.SpeedLimit && parameter.type != SignType.SpeedLimitOld &&
                    parameter.type != SignType.SpeedLimitYellow && parameter.type != SignType.SpeedLimitYellowOld) continue;
                int value;
                if (!int.TryParse(parameter.signText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0 || value > 30) return;
                speeds.Add(value * 10);
            }
            if (speeds.Count == 0) return;
            Vector3 center = data.transform.position - WorldMover.currentMove + data.transform.right * 2;
            Vector3 facing = data.transform.forward;
            poles.Add(new CachedPole { X = center.x, Y = center.y, Z = center.z, FacingX = facing.x, FacingZ = facing.z, Speeds = speeds.ToArray() });
        }
        private void Unsubscribe()
        {
            foreach (var streamer in streamers) if (streamer) streamer.LoadedScenesChanged -= MarkAreasChanged;
            streamers.Clear();
        }
        public void Dispose() { Unsubscribe(); jobs.Clear(); }
    }
}
