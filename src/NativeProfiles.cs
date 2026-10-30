using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using DV.Utils;
using UnityEngine;

namespace DVTrainSpeedLimit
{
    internal sealed class ProfilePending : Exception { }

    internal sealed class NativeProfiles : IDisposable
    {
        private sealed class Marker
        {
            internal double Span;
            internal int Speed;
            internal Junction Junction;
            internal int[] BranchSpeeds;
        }
        private sealed class Profile
        {
            internal bool Yard;
            internal readonly List<Marker> Markers = new List<Marker>();
        }
        private sealed class Pair
        {
            internal readonly Profile Forward = new Profile(), Reverse = new Profile();
        }
        private sealed class Projection
        {
            internal List<SignGeometry.Match> Matches;
            internal double Milliseconds;
        }
        private readonly BakedSignIndex index = new BakedSignIndex();
        private readonly Dictionary<RailTrack, Pair> cache = new Dictionary<RailTrack, Pair>();
        private readonly Dictionary<Tuple<RailTrack, bool>, OccupiedPath.End<RailTrack>> occupiedNext = new Dictionary<Tuple<RailTrack, bool>, OccupiedPath.End<RailTrack>>();
        private readonly Dictionary<Tuple<RailTrack, bool>, OccupiedPath.End<RailTrack>> occupiedPrevious = new Dictionary<Tuple<RailTrack, bool>, OccupiedPath.End<RailTrack>>();
        private readonly HashSet<Tuple<RailTrack, bool>> visited = new HashSet<Tuple<RailTrack, bool>>();
        private readonly ObservedRoute<RailTrack> observedRoute = new ObservedRoute<RailTrack>();
        private TrainCar observedLoco;
        private Trainset observedSet;
        private int observedDirection;
        private Vector3 observedPosition;
        private float observedAt, observedSpeed;
        private bool hasObservedPosition;
        private Task<Projection> worker;
        private RailTrack workerTrack;
        private SignGeometry.Sign[] workerSigns;
        private int generation = -1;
        private RailTrackRegistryBase registry;
        internal int TrackingRevision { get; private set; }
        internal int CacheCount { get { return cache.Count; } }
        internal bool Ready { get { return index.Ready && index.Count > 0; } }
        internal bool Scanning { get { return index.Scanning; } }
        internal string IndexStatus { get { return index.Ready && index.Count == 0 ? "No signs found - clear cache in UMM" : index.Status; } }
        internal string CacheStatus { get { return index.Status; } }

        internal void StepIndex()
        {
            CheckWorld();
            index.Step();
            if (generation == index.Generation) return;
            generation = index.Generation;
            cache.Clear();
        }
        private void CheckWorld()
        {
            var currentRegistry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
            if (currentRegistry && !ReferenceEquals(registry, currentRegistry))
            {
                registry = currentRegistry;
                ForgetRoute("world changed");
                index.Reset();
            }
        }
        internal void Reindex() { index.ClearCache(); cache.Clear(); ForgetRoute("cache cleared"); }
        public void Dispose() { index.Dispose(); }

        internal void ForgetRoute(string reason)
        {
            TrackingRevision++;
            if (observedRoute.Count > 0) Main.Log("Observed route cleared: {0}; links={1}.", reason, observedRoute.Count);
            observedRoute.Clear(); observedLoco = null; observedSet = null; observedDirection = 0; hasObservedPosition = false;
        }

        internal void Begin(TrainFootprint footprint, TrainCar loco, int localDirection, float velocity)
        {
            CheckWorld();
            if (!Ready) throw new ProfilePending();
            if (observedLoco != loco || !ReferenceEquals(observedSet, loco.trainset) || observedDirection != localDirection)
            {
                ForgetRoute("locomotive, consist or travel direction changed");
                observedLoco = loco; observedSet = loco.trainset; observedDirection = localDirection;
            }
            Vector3 absolutePosition = loco.transform.position - WorldMover.currentMove;
            float elapsed = Mathf.Max(0, Time.unscaledTime - observedAt);
            float permittedMove = Mathf.Max(15, Mathf.Max(Mathf.Abs(velocity), observedSpeed) * elapsed * 3 + 5);
            if (hasObservedPosition && (absolutePosition - observedPosition).sqrMagnitude > permittedMove * permittedMove)
            {
                observedRoute.Clear();
                TrackingRevision++;
                Main.Log("Observed route cleared after position discontinuity (possible teleport).");
            }
            observedPosition = absolutePosition; observedAt = Time.unscaledTime; observedSpeed = Mathf.Abs(velocity); hasObservedPosition = true;
            occupiedNext.Clear(); occupiedPrevious.Clear();
            for (int i = 1; i < footprint.Segments.Count; i++)
            {
                var a = footprint.Segments[i - 1]; var b = footprint.Segments[i];
                if (a.Track == b.Track) continue;
                SetConnection(occupiedNext, Tuple.Create(a.Track, a.Increasing), new OccupiedPath.End<RailTrack>(b.Track, b.Increasing));
                SetConnection(occupiedPrevious, Tuple.Create(b.Track, b.Increasing), new OccupiedPath.End<RailTrack>(a.Track, a.Increasing));
            }
            // Commit only after validating the whole current path. Remember both
            // orientations, but reset context when the train actually reverses.
            for (int i = 1; i < footprint.Segments.Count; i++)
            {
                var a = footprint.Segments[i - 1]; var b = footprint.Segments[i];
                observedRoute.Observe(a.Track, a.Increasing, b.Track, b.Increasing);
            }
        }
        private static void SetConnection(Dictionary<Tuple<RailTrack, bool>, OccupiedPath.End<RailTrack>> map,
            Tuple<RailTrack, bool> key, OccupiedPath.End<RailTrack> value)
        {
            OccupiedPath.End<RailTrack> existing;
            if (map.TryGetValue(key, out existing) && (existing.Track != value.Track || existing.Increasing != value.Increasing))
                throw new InvalidOperationException("Ambiguous repeated occupied track");
            map[key] = value;
        }

        private Profile Get(RailTrack track, bool inc)
        {
            Pair pair;
            if (cache.TryGetValue(track, out pair)) return inc ? pair.Forward : pair.Reverse;
            if (index.Scanning) throw new ProfilePending();
            if (track.name.StartsWith("[Y]", StringComparison.Ordinal))
            {
                pair = new Pair(); pair.Forward.Yard = pair.Reverse.Yard = true;
                cache[track] = pair;
                return inc ? pair.Forward : pair.Reverse;
            }
            if (worker != null)
            {
                if (!worker.IsCompleted) throw new ProfilePending();
                var completed = worker;
                var completedTrack = workerTrack;
                var signs = workerSigns;
                worker = null; workerTrack = null; workerSigns = null;
                // Never wait on the Unity thread. Discard results if a scene change
                // or explicit reindex superseded the immutable sign snapshot.
                if (ReferenceEquals(signs, index.Snapshot) && completedTrack)
                {
                    var projection = completed.GetAwaiter().GetResult();
                    pair = MakeProfiles(completedTrack, projection.Matches, signs);
                    cache[completedTrack] = pair;
                    Main.Log("Baked track profile: track={0}, signs={1}/{2}, worker={3:0.00}ms (off Unity thread).",
                        completedTrack.name, pair.Forward.Markers.Count, pair.Reverse.Markers.Count, projection.Milliseconds);
                    if (completedTrack == track) return inc ? pair.Forward : pair.Reverse;
                }
            }
            if (track.GetComponent<TurntableRailTrack>()) throw new InvalidOperationException("Moving turntable profile unsupported");
            DV.PointSet.EquiPointSet pointSet;
            if (!RailTrack.pointSets.TryGetValue(track, out pointSet) || pointSet.points == null)
                throw new InvalidOperationException("Rail geometry is not loaded: " + track.name);
            var nativePoints = pointSet.points;
            var snapshot = index.Snapshot;
            workerTrack = track; workerSigns = snapshot;
            worker = Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                // Read only value-type geometry. No Unity objects or APIs on worker.
                var points = new SignGeometry.Point[nativePoints.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    var p = nativePoints[i];
                    points[i] = new SignGeometry.Point(p.position.x, p.position.y, p.position.z, p.span);
                }
                return new Projection { Matches = SignGeometry.MatchSigns(points, snapshot), Milliseconds = timer.Elapsed.TotalMilliseconds };
            });
            throw new ProfilePending();
        }

        private static Pair MakeProfiles(RailTrack track, List<SignGeometry.Match> matches, SignGeometry.Sign[] signs)
        {
            var pair = new Pair();
            foreach (var match in matches)
            {
                var sign = signs[match.SignIndex];
                var marker = new Marker { Span = match.Span, Speed = sign.Speeds[0] };
                if (sign.Speeds.Length > 1)
                {
                    Junction j = match.Increasing ? track.outJunction : track.inJunction;
                    if (!j || j.inBranch.track != track || j.outBranches.Count != sign.Speeds.Length)
                        throw new InvalidOperationException("Baked junction pole could not be matched to its branches on " + track.name);
                    marker.Junction = j; marker.BranchSpeeds = sign.Speeds;
                }
                (match.Increasing ? pair.Forward : pair.Reverse).Markers.Add(marker);
                if (Main.Settings.VerboseLogging) Main.Log("  baked sign track={0}, span={1:0.000}, increasing={2}, speeds={3}",
                    track.name, marker.Span, match.Increasing, string.Join(",", sign.Speeds));
            }
            pair.Reverse.Markers.Reverse();
            return pair;
        }

        private int Resolve(Marker marker, RailTrack track, bool inc, RailTrack knownNext = null)
        {
            if (!marker.Junction) return marker.Speed;
            OccupiedPath.End<RailTrack> occupied;
            RailTrack next;
            if (knownNext) next = knownNext;
            else if (occupiedNext.TryGetValue(Tuple.Create(track, inc), out occupied)) next = occupied.Track;
            else
            {
                var branch = inc ? track.GetOutBranch() : track.GetInBranch();
                next = branch == null ? null : branch.track;
            }
            for (int i = 0; i < marker.Junction.outBranches.Count; i++)
                if (marker.Junction.outBranches[i].track == next) return marker.BranchSpeeds[i];
            throw new InvalidOperationException("Cannot resolve branch-specific speed sign");
        }

        internal int At(RailTrack track, double span, bool inc)
        {
            visited.Clear();
            double searched = 0;
            RailTrack nextTowardTrain = null;
            while (track && visited.Count < 128 && searched < 10000)
            {
                if (!visited.Add(Tuple.Create(track, inc))) break;
                var profile = Get(track, inc);
                if (profile.Yard) return 30; // Native manual's unsigned yard-track rule.
                for (int i = profile.Markers.Count - 1; i >= 0; i--)
                {
                    var marker = profile.Markers[i];
                    if (inc ? marker.Span <= span : marker.Span >= span) return Resolve(marker, track, inc, nextTowardTrain);
                }
                searched += inc ? span : TrainFootprint.TrackLength(track) - span;
                nextTowardTrain = track;
                OccupiedPath.End<RailTrack> previous;
                if (occupiedPrevious.TryGetValue(Tuple.Create(track, inc), out previous))
                { track = previous.Track; inc = previous.Increasing; }
                else
                {
                    var ends = TrainFootprint.Connections(track, !inc);
                    if (observedRoute.TryPrevious(track, inc, ends, out previous))
                    { track = previous.Track; inc = previous.Increasing; }
                    else
                    {
                        if (ends.Count != 1) throw new InvalidOperationException("Previous speed sign lies beyond an unobserved junction: track=" +
                            track.name + ", span=" + span.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + ", increasing=" + inc +
                            ", branches=" + ends.Count + ", observedLinks=" + observedRoute.Count);
                        track = ends[0].Track; inc = !ends[0].Increasing;
                    }
                }
                span = inc ? TrainFootprint.TrackLength(track) : 0;
            }
            throw new InvalidOperationException("No preceding speed sign within the lookup budget");
        }

        internal int Minimum(OccupiedPath.Segment<RailTrack> segment)
        {
            var profile = Get(segment.Track, segment.Increasing);
            if (profile.Yard) return 30;
            int result = At(segment.Track, segment.From, segment.Increasing);
            foreach (var marker in profile.Markers)
            {
                double distance = segment.Increasing ? marker.Span - segment.From : segment.From - marker.Span;
                if (distance >= 0 && distance <= segment.Length)
                    result = Math.Min(result, Resolve(marker, segment.Track, segment.Increasing));
            }
            return result;
        }
    }
}
