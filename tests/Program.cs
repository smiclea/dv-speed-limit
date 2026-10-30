using DVTrainSpeedLimit;

int count = 0;
void Equal<T>(string name, T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"{name}: expected {expected}, got {actual}");
    Console.WriteLine("PASS " + name); count++;
}
void Throws(string name, Action action)
{
    try { action(); } catch (InvalidOperationException) { Console.WriteLine("PASS " + name); count++; return; }
    throw new Exception(name + ": expected unavailable");
}
int Min(int initial, double length, params LimitChange[] changes) => LimitMath.Minimum(initial, length, changes);
Equal("60 to 90 stays 60 while rear before sign", Min(60, 600, new LimitChange(200, 90)), 60);
Equal("increase releases exactly at rear", Min(60, 600, new LimitChange(0, 90)), 90);
Equal("increase behind rear has released", Min(60, 600, new LimitChange(-0.01, 90)), 90);
Equal("lower limit starts exactly at leading coupler", Min(90, 600, new LimitChange(600, 40)), 40);
Equal("lower limit ahead does not apply early", Min(90, 600, new LimitChange(600.001, 40)), 90);
Equal("lower limit anywhere under train applies", Min(90, 600, new LimitChange(200, 40)), 40);
Equal("low section between front and rear is included", Min(80, 1000, new LimitChange(100, 30), new LimitChange(120, 100)), 30);
Equal("three increases still retain oldest restriction", Min(30, 1000, new LimitChange(200, 50), new LimitChange(500, 80), new LimitChange(900, 120)), 30);
Equal("reversing uses ordered distances in travel direction", Min(100, 800, new LimitChange(60, 40), new LimitChange(700, 80)), 40);
Equal("load midway needs no crossing history", Min(40, 1200, new LimitChange(100, 100)), 40);
Equal("multiple cleared sections use most recent rear limit", Min(30, 500, new LimitChange(-100, 50), new LimitChange(-20, 80)), 80);
Equal("single point reduction applies", Min(90, 0, new LimitChange(0, 20)), 20);
Throws("unknown previous limit is not guessed", () => Min(0, 100, new LimitChange(50, 80)));

var a = new Track("A", 100);
var b = new Track("B", 20);
var c = new Track("C", 100);
var other = new Track("other", 100);
a.Out.Add(new OccupiedPath.End<Track>(other, true)); // Selected/wrong branch is deliberately first.
a.Out.Add(new OccupiedPath.End<Track>(b, true));
b.Out.Add(new OccupiedPath.End<Track>(c, false));
List<OccupiedPath.Segment<Track>> Path(Track start, double from, bool inc, Track target, double to, bool targetInc, double budget)
    => OccupiedPath.Between(start, from, inc, target, to, targetInc, budget, t => t.Length, (t, dir) => dir ? t.Out : t.In);
var path = Path(a, 90, true, c, 90, false, 45);
Equal("find occupied branch independently of switch selection", path.Count, 3);
Equal("middle section is occupied", path[1].Track, b);
Equal("coordinate reversal at a connection", path[2].Increasing, false);
Equal("occupied length through reversed coordinate connection", path.Sum(p => p.Length), 40d);
Throws("disconnected bogies produce unavailable", () => Path(a, 90, true, new Track("isolated", 100), 0, true, 45));
Throws("unrealistically long path rejected", () => Path(a, 90, true, c, 90, false, 39));
Equal("direct same-track car footprint", Path(a, 10, true, a, 30, true, 40).Single().Length, 20d);
Equal("direct reverse footprint", Path(a, 80, false, a, 20, false, 70).Single().Length, 60d);
var zero = new Track("zero", 0);
Throws("zero-length track is unavailable", () => Path(zero, 0, true, a, 10, true, 50));
var fork = new Track("fork", 10); var l = new Track("left", 10); var r = new Track("right", 10); var merge = new Track("merge", 10);
fork.Out.Add(new OccupiedPath.End<Track>(l, true)); fork.Out.Add(new OccupiedPath.End<Track>(r, true));
l.Out.Add(new OccupiedPath.End<Track>(merge, true)); r.Out.Add(new OccupiedPath.End<Track>(merge, true));
Throws("ambiguous bogie path is unavailable", () => Path(fork, 5, true, merge, 5, true, 30));
var loop = new Track("loop", 10); loop.Out.Add(new OccupiedPath.End<Track>(loop, true));
Throws("loop terminates without hanging", () => Path(loop, 0, true, a, 5, true, 100));
var straight = new[] { new SignGeometry.Point(0, 0, 0, 0), new SignGeometry.Point(0, 0, 100, 100) };
SignGeometry.Sign SignAt(double x, double y, double z, double facingZ, params int[] speeds)
    => new() { X = x, Y = y, Z = z, FacingX = 0, FacingZ = facingZ, Speeds = speeds };
var baked = new[] { SignAt(0, 0, 20, -1, 60), SignAt(0, 0, 80, 1, 40),
    SignAt(4.5, 0, 40, -1, 90), SignAt(0, 8, 30, -1, 100), SignAt(0, 0, 130, -1, 20) };
var matched = SignGeometry.MatchSigns(straight, baked);
Equal("baked sign projection without a generator object", matched.Count, 2);
Equal("baked forward sign direction", matched[0].Increasing, true);
Equal("baked reverse sign direction", matched[1].Increasing, false);
Equal("exact sign position on bogie span", matched[0].Span, 20d);
Equal("parallel and elevated track signs rejected", matched.Select(m => m.SignIndex).ToArray().Length, 2);
Equal("small rail kinks tolerated", SignGeometry.MatchSigns(straight, new[] { SignAt(0.05, 0, 25, -1, 60) }).Count, 1);
var sideways = SignAt(0, 0, 20, 0, 60); sideways.FacingX = 1;
Equal("perpendicular signs rejected", SignGeometry.MatchSigns(straight, new[] { sideways }).Count, 0);
var shifted = new[] { new SignGeometry.Point(20000, 0, -13000, 0), new SignGeometry.Point(20000, 0, -12900, 100) };
Equal("absolute positions survive floating origin changes", SignGeometry.MatchSigns(shifted,
    new[] { SignAt(20000, 0, -12980, -1, 60) }).Single().Span, 20d);
var dual = new[] { SignAt(0, 0, 97, -1, 50, 30) };
Equal("dual branch pole metadata preserved", dual[SignGeometry.MatchSigns(straight, dual).Single().SignIndex].Speeds[1], 30);
var workerMatch = Task.Run(() => SignGeometry.MatchSigns(straight, baked));
Equal("geometry matching runs on a background worker", (await workerMatch).Count, 2);
var compact = new List<OccupiedPath.Segment<Track>>();
for (int i = 0; i < 1000; i++) OccupiedPath.AppendContiguous(compact,
    new OccupiedPath.Segment<Track> { Track = a, From = i * 10, To = (i + 1) * 10, Increasing = true });
Equal("1000 adjacent bogie ranges require one limit lookup", compact.Count, 1);
Equal("range compaction preserves full occupied length", compact[0].Length, 10000d);
OccupiedPath.AppendContiguous(compact, new OccupiedPath.Segment<Track> { Track = a, From = 10000, To = 9990, Increasing = false });
Equal("compaction preserves direction changes", compact.Count, 2);
OccupiedPath.AppendContiguous(compact, new OccupiedPath.Segment<Track> { Track = b, From = 0, To = 10, Increasing = true });
Equal("compaction preserves separate track sections", compact.Count, 3);
var largeGeometry = Enumerable.Range(0, 40001).Select(i => new SignGeometry.Point(0, 0, i * 0.5, i * 0.5)).ToArray();
var manySigns = Enumerable.Range(0, 4000).Select(i => SignAt(i < 80 ? 0 : 5000, 0, i * 100, -1, 60)).ToArray();
var benchmarkTimer = System.Diagnostics.Stopwatch.StartNew();
var benchmarkMatches = await Task.Run(() => SignGeometry.MatchSigns(largeGeometry, manySigns));
benchmarkTimer.Stop();
Equal("background matching handles a 20km track and 4000 world signs", benchmarkMatches.Count, 80);
Console.WriteLine($"Synthetic cold profile (20km, 4000 signs) on .NET 8 worker: {benchmarkTimer.Elapsed.TotalMilliseconds:0.0} ms; not an in-game frame-time measurement.");
var regionCache = new RegionCache();
Equal("new area queues automatically", regionCache.TryQueue("stream:signs:area-A"), true);
Equal("area already queued is not queued twice", regionCache.TryQueue("stream:signs:area-A"), false);
var poleA = new CachedPole { X = 100, Y = 10, Z = 200, FacingX = 0, FacingZ = -1, Speeds = new[] { 60 } };
var sixtySeven = Enumerable.Range(0, 67).Select(i => new CachedPole { X = i * 10, Y = 10, Z = 200,
    FacingX = 0, FacingZ = -1, Speeds = new[] { 60 } }).ToArray();
regionCache.Complete("stream:signs:area-A", sixtySeven);
Equal("67 signs complete an area without walking decoration", regionCache.PendingCount, 0);
Equal("completed area is never scanned again", regionCache.TryQueue("stream:signs:area-A"), false);
Equal("initial cached sign count", regionCache.Signs().Length, 67);
Equal("uncached neighboring area queues automatically", regionCache.TryQueue("stream:signs:area-B"), true);
regionCache.Complete("stream:signs:area-B", new[] { poleA }); // duplicates one A pole
Equal("overlapping cached areas deduplicate the same pole", regionCache.Signs().Length, 67);
regionCache.Complete("stream:signs:empty", Array.Empty<CachedPole>());
Equal("empty area is cached to prevent repeat searches", regionCache.TryQueue("stream:signs:empty"), false);
Equal("all completed areas are counted", regionCache.AreaCount, 3);
var cacheDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dv-sign-cache-test-" + Guid.NewGuid().ToString("N"));
var cachePath = System.IO.Path.Combine(cacheDirectory, "signs.xml");
try
{
    var document = regionCache.Export("game-build-A");
    await Task.Run(() => SignCacheDisk.Write(cachePath, document));
    var restored = new RegionCache();
    restored.Import(await Task.Run(() => SignCacheDisk.Read(cachePath)), "game-build-A");
    Equal("disk cache survives game-session restart", restored.Signs().Length, 67);
    Equal("saved area does not rescan after restart", restored.TryQueue("stream:signs:area-A"), false);
    Equal("saved empty area does not rescan after restart", restored.TryQueue("stream:signs:empty"), false);
    Equal("disk cache retains native sign speed", restored.Signs()[0].Speeds[0], 60);
    bool incompatible = false;
    try { restored.Import(document, "game-build-B"); } catch (InvalidDataException) { incompatible = true; }
    Equal("game update invalidates an older cache", incompatible, true);
    Equal("invalid import cannot partially overwrite the live cache", restored.AreaCount, 3);
    restored.Clear();
    Equal("clear button removes every cached area", restored.AreaCount, 0);
    Equal("clear button removes every cached sign", restored.Signs().Length, 0);
    Equal("previously visited area is queued after clear", restored.TryQueue("stream:signs:area-A"), true);
    restored.Cancel("stream:signs:area-A");
    Equal("unloaded unfinished area can retry when loaded", restored.TryQueue("stream:signs:area-A"), true);
    restored.Complete("stream:signs:area-A", new[] { poleA });
    await Task.Run(() => SignCacheDisk.Write(cachePath, restored.Export("game-build-A")));
    var replaced = SignCacheDisk.Read(cachePath);
    Equal("atomic cache replacement preserves the latest snapshot", replaced.Areas.Single().Poles.Length, 1);
    SignCacheDisk.Delete(cachePath);
    Equal("clear deletes the persisted file", File.Exists(cachePath), false);
    Equal("missing cache starts a fresh area scan", SignCacheDisk.Read(cachePath) == null, true);
}
finally { if (Directory.Exists(cacheDirectory)) Directory.Delete(cacheDirectory, true); }
var remembered = new ObservedRoute<Track>();
remembered.Observe(a, true, b, true);
remembered.Observe(b, true, c, false);
var backFromB = new[] { new OccupiedPath.End<Track>(other, false), new OccupiedPath.End<Track>(a, false) };
Equal("junction remains known after rear clears current footprint", remembered.TryPrevious(b, true, backFromB, out var prior), true);
Equal("observed incoming branch wins over changed switch selection", prior.Track, a);
Equal("remembered branch preserves forward orientation", prior.Increasing, true);
var backFromC = new[] { new OccupiedPath.End<Track>(b, false), new OccupiedPath.End<Track>(other, true) };
Equal("coordinate reversal survives previous-sign lookup", remembered.TryPrevious(c, false, backFromC, out prior), true);
Equal("coordinate reversal returns the actual previous track", prior.Track, b);
var backFromAInReverse = new[] { new OccupiedPath.End<Track>(b, true) };
Equal("observed connection stores both traversal orientations", remembered.TryPrevious(a, false, backFromAInReverse, out prior), true);
Equal("reverse traversal previous orientation is inverted", prior.Increasing, false);
Equal("unknown incoming branch is not guessed", remembered.TryPrevious(other, true, backFromB, out prior), false);
Equal("removed physical connection invalidates remembered branch", remembered.TryPrevious(b, true,
    new[] { new OccupiedPath.End<Track>(other, false) }, out prior), false);
Equal("connection orientation changes invalidate a remembered branch", remembered.TryPrevious(b, true,
    new[] { new OccupiedPath.End<Track>(a, true) }, out prior), false);
remembered.Observe(other, false, b, true);
Equal("later observed traversal updates an old incoming route", remembered.TryPrevious(b, true,
    new[] { new OccupiedPath.End<Track>(a, false), new OccupiedPath.End<Track>(other, true) }, out prior), true);
Equal("latest observed route is retained", prior.Track, other);
remembered.Clear();
Equal("context reset forgets previous train route", remembered.Count, 0);
Equal("cleared route cannot supply a stale branch", remembered.TryPrevious(b, true, backFromB, out prior), false);
var boundedHistory = new ObservedRoute<Track>(4);
for (int i = 0; i < 100; i++) boundedHistory.Observe(new Track("from" + i, 10), true, new Track("to" + i, 10), true);
Equal("observed-route history has a fixed memory bound", boundedHistory.Count, 4);
Console.WriteLine($"{count} checks passed.");

sealed class Track(string name, double length)
{
    internal readonly string Name = name;
    internal readonly double Length = length;
    internal readonly List<OccupiedPath.End<Track>> In = new(), Out = new();
}
