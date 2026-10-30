using System;
using System.Collections.Generic;

namespace DVTrainSpeedLimit
{
    internal struct LimitChange
    {
        internal double Distance;
        internal int Speed;
        internal LimitChange(double distance, int speed) { Distance = distance; Speed = speed; }
    }

    internal static class LimitMath
    {
        // Changes are ordered in the direction of travel. A change exactly at the rear
        // replaces the old section; a reduction exactly at the front already applies.
        internal static int Minimum(int initial, double length, IList<LimitChange> changes)
        {
            if (length < 0 || double.IsNaN(length) || double.IsInfinity(length))
                throw new ArgumentOutOfRangeException(nameof(length));
            int active = initial, result = 0;
            double previous = double.NegativeInfinity;
            foreach (var change in changes)
            {
                if (change.Speed <= 0 || double.IsNaN(change.Distance) || change.Distance < previous)
                    throw new ArgumentException("Invalid or unordered speed profile");
                previous = change.Distance;
                if (change.Distance <= 0) { active = change.Speed; continue; }
                if (result == 0) result = active;
                if (change.Distance > length) break;
                result = result == 0 ? 0 : Math.Min(result, change.Speed);
            }
            if (result == 0) result = active;
            if (result <= 0) throw new InvalidOperationException("No speed limit at the rear of this section");
            return result;
        }
    }

    internal static class OccupiedPath
    {
        internal static void AppendContiguous<T>(List<Segment<T>> path, Segment<T> piece) where T : class
        {
            if (path.Count > 0)
            {
                int index = path.Count - 1;
                var previous = path[index];
                if (ReferenceEquals(previous.Track, piece.Track) && previous.Increasing == piece.Increasing && Math.Abs(previous.To - piece.From) < 0.01)
                { previous.To = piece.To; path[index] = previous; return; }
            }
            path.Add(piece);
        }

        internal struct End<T> where T : class
        {
            internal T Track;
            internal bool Increasing;
            internal End(T track, bool increasing) { Track = track; Increasing = increasing; }
        }
        internal struct Segment<T> where T : class
        {
            internal T Track;
            internal bool Increasing;
            internal double From, To;
            internal double Length { get { return Math.Abs(To - From); } }
        }

        // Search only the small gap between neighboring bogies. Using all connections
        // here preserves the occupied branch even if a switch was thrown behind us.
        internal static List<Segment<T>> Between<T>(T start, double from, bool increasing,
            T target, double to, bool targetIncreasing, double maxDistance,
            Func<T, double> lengthOf, Func<T, bool, IList<End<T>>> connections) where T : class
        {
            var path = new List<Segment<T>>();
            List<Segment<T>> found = null;
            var visited = new HashSet<Tuple<T, bool>>();
            int work = 0, matches = 0;
            Action<T, double, bool, double> walk = null;
            walk = (track, span, inc, remaining) =>
            {
                if (track == null || remaining < 0 || matches > 1) return;
                if (++work > 256) throw new InvalidOperationException("Occupied path search budget exceeded");
                double length = lengthOf(track);
                if (length <= 0 || double.IsNaN(length) || double.IsInfinity(length) || span < -0.01 || span > length + 0.01)
                    throw new InvalidOperationException("Invalid track geometry or bogie span");
                span = Math.Max(0, Math.Min(length, span));
                var key = Tuple.Create(track, inc);
                if (!visited.Add(key)) return;
                double delta = inc ? to - span : span - to;
                if (ReferenceEquals(track, target) && inc == targetIncreasing && delta >= -0.001 && delta <= remaining)
                {
                    path.Add(new Segment<T> { Track = track, From = span, To = to, Increasing = inc });
                    found = new List<Segment<T>>(path);
                    matches++;
                    path.RemoveAt(path.Count - 1);
                }
                else
                {
                    double available = inc ? length - span : span;
                    if (available <= remaining)
                    {
                        path.Add(new Segment<T> { Track = track, From = span, To = inc ? length : 0, Increasing = inc });
                        var ends = connections(track, inc);
                        if (ends != null) foreach (var end in ends)
                            if (end.Track != null) walk(end.Track, end.Increasing ? 0 : lengthOf(end.Track), end.Increasing, remaining - available);
                        path.RemoveAt(path.Count - 1);
                    }
                }
                visited.Remove(key);
            };
            walk(start, from, increasing, maxDistance);
            if (matches != 1) throw new InvalidOperationException(matches == 0
                ? "No connected occupied path between neighboring bogies" : "Ambiguous occupied path between neighboring bogies");
            return found;
        }
    }
}
