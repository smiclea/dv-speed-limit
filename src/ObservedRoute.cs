using System;
using System.Collections.Generic;

namespace DVTrainSpeedLimit
{
    // Only connections seen under this train are remembered. This is separate from
    // persistent sign data and never records a switch selection as a past route.
    internal sealed class ObservedRoute<T> where T : class
    {
        private readonly int capacity;
        private readonly Dictionary<Tuple<T, bool>, OccupiedPath.End<T>> previous = new Dictionary<Tuple<T, bool>, OccupiedPath.End<T>>();
        private readonly Queue<Tuple<T, bool>> order = new Queue<Tuple<T, bool>>();
        internal ObservedRoute(int capacity = 2048) { this.capacity = Math.Max(2, capacity); }
        internal int Count { get { return previous.Count; } }
        internal void Clear() { previous.Clear(); order.Clear(); }

        internal void Observe(T from, bool fromIncreasing, T to, bool toIncreasing)
        {
            if (from == null || to == null || ReferenceEquals(from, to)) return;
            Put(Tuple.Create(to, toIncreasing), new OccupiedPath.End<T>(from, fromIncreasing));
            Put(Tuple.Create(from, !fromIncreasing), new OccupiedPath.End<T>(to, !toIncreasing));
        }
        private void Put(Tuple<T, bool> key, OccupiedPath.End<T> value)
        {
            if (!previous.ContainsKey(key)) order.Enqueue(key);
            previous[key] = value;
            while (previous.Count > capacity) previous.Remove(order.Dequeue());
        }

        internal bool TryPrevious(T track, bool increasing, IList<OccupiedPath.End<T>> backwardConnections, out OccupiedPath.End<T> result)
        {
            if (previous.TryGetValue(Tuple.Create(track, increasing), out result) && backwardConnections != null)
                foreach (var end in backwardConnections)
                    if (ReferenceEquals(end.Track, result.Track) && end.Increasing != result.Increasing) return true;
            result = default(OccupiedPath.End<T>);
            return false;
        }
    }
}
