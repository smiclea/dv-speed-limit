using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVTrainSpeedLimit
{
    internal sealed class TrainFootprint
    {
        internal readonly List<OccupiedPath.Segment<RailTrack>> Segments = new List<OccupiedPath.Segment<RailTrack>>();
        internal int Cars, Direction;
        internal double Length;
        private Trainset layoutSet;
        private TrainCar layoutLoco;
        private Bogie[] bogies;
        private int[] orderSigns;
        private int locoOrderSign;
        private double firstOverhang, lastOverhang;
        private Gap[] gaps;
        private readonly List<OccupiedPath.Segment<RailTrack>> extension = new List<OccupiedPath.Segment<RailTrack>>(4);
        private sealed class Gap
        {
            internal RailTrack Start, End;
            internal bool StartIncreasing, EndIncreasing;
            internal List<OccupiedPath.Segment<RailTrack>> Path;
        }

        internal static double TrackLength(RailTrack track)
        {
            DV.PointSet.EquiPointSet points;
            // Bogies already have their rail geometry. Never generate distant track
            // geometry synchronously during an ordinary driving update.
            if (!RailTrack.pointSets.TryGetValue(track, out points) || points.points == null || points.points.Length < 2)
                throw new InvalidOperationException("Track geometry not loaded: " + track.name);
            return Math.Min(points.span, points.points[points.points.Length - 1].span);
        }
        internal static IList<OccupiedPath.End<RailTrack>> Connections(RailTrack track, bool increasing)
        {
            var branches = increasing ? track.GetAllOutBranches() : track.GetAllInBranches();
            var result = new List<OccupiedPath.End<RailTrack>>();
            if (branches != null) foreach (var branch in branches)
                if (branch != null && branch.track && RailTrack.pointSets.ContainsKey(branch.track))
                    result.Add(new OccupiedPath.End<RailTrack>(branch.track, branch.first));
            return result;
        }
        private static void RequireBogie(Bogie bogie)
        {
            if (!bogie || !bogie.fullyInitialized || bogie.HasDerailed || !bogie.track || bogie.traveller == null || bogie.TrackDirectionSign == 0)
                throw new InvalidOperationException("A car is derailed or its bogie position is not ready");
        }

        private void Layout(TrainCar loco)
        {
            var set = loco.trainset;
            if (set == null || set.cars.Count == 0) throw new InvalidOperationException("Train consist is not ready");
            if (ReferenceEquals(set, layoutSet) && loco == layoutLoco) return;
            var ordered = new List<Bogie>();
            var signs = new List<int>();
            Coupler entry = set.GetEndmost(true), lastExit = null;
            locoOrderSign = 0;
            foreach (var car in set.cars)
            {
                if (!car || !entry || entry.train != car) throw new InvalidOperationException("Consist topology is changing");
                int orderSign = entry.isFrontCoupler ? -1 : 1;
                if (car == loco) locoOrderSign = orderSign;
                var carBogies = car.Bogies;
                if (carBogies == null || carBogies.Length == 0) throw new InvalidOperationException("Car has no bogies: " + car.ID);
                for (int i = 0; i < carBogies.Length; i++)
                {
                    ordered.Add(carBogies[orderSign > 0 ? i : carBogies.Length - i - 1]);
                    signs.Add(orderSign);
                }
                lastExit = entry.GetOppositeCoupler();
                entry = lastExit ? lastExit.coupledTo : null;
            }
            if (locoOrderSign == 0 || !lastExit || lastExit.IsCoupled()) throw new InvalidOperationException("Incomplete consist topology");
            bogies = ordered.ToArray(); orderSigns = signs.ToArray();
            gaps = new Gap[Math.Max(0, bogies.Length - 1)];
            for (int i = 0; i < gaps.Length; i++) gaps[i] = new Gap();
            var first = bogies[0]; var last = bogies[bogies.Length - 1];
            var firstCoupler = set.GetEndmost(true);
            firstOverhang = Math.Abs(first.Car.transform.InverseTransformPoint(first.transform.position).z -
                first.Car.transform.InverseTransformPoint(firstCoupler.transform.position).z);
            lastOverhang = Math.Abs(last.Car.transform.InverseTransformPoint(last.transform.position).z -
                last.Car.transform.InverseTransformPoint(lastExit.transform.position).z);
            layoutSet = set; layoutLoco = loco; Cars = set.cars.Count;
            Main.Log("Cached consist layout: cars={0}, bogies={1}; gaps searched only when bogies change rail tracks.", Cars, bogies.Length);
        }

        internal TrainFootprint Build(TrainCar loco, int localDirection)
        {
            Layout(loco);
            foreach (var car in layoutSet.cars)
                if (!car || car.derailed || car.IsTeleporting) throw new InvalidOperationException("Consist is derailed or teleporting");
            foreach (var bogie in bogies) RequireBogie(bogie);
            Segments.Clear(); Length = 0;
            Direction = localDirection * locoOrderSign;
            var first = bogies[0]; var last = bogies[bogies.Length - 1];
            Extend(first.track, first.traveller.Span, first.TrackDirectionSign * -orderSigns[0] > 0, firstOverhang);
            Reverse(extension);
            foreach (var piece in extension) Append(piece);
            for (int i = 0; i < gaps.Length; i++) AddGap(i);
            Extend(last.track, last.traveller.Span, last.TrackDirectionSign * orderSigns[orderSigns.Length - 1] > 0, lastOverhang);
            foreach (var piece in extension) Append(piece);
            if (Direction < 0) Reverse(Segments);
            while (Segments.Count > 1 && Segments[0].Length < 0.000001) Segments.RemoveAt(0);
            foreach (var segment in Segments) Length += segment.Length;
            return this;
        }

        private void AddGap(int i)
        {
            var a = bogies[i]; var b = bogies[i + 1];
            double from = a.traveller.Span, to = b.traveller.Span;
            bool inc = a.TrackDirectionSign * orderSigns[i] > 0;
            bool endInc = b.TrackDirectionSign * orderSigns[i + 1] > 0;
            double direct = inc ? to - from : from - to;
            if (a.track == b.track && inc == endInc && direct >= -0.001 && direct < 200)
            {
                Append(new OccupiedPath.Segment<RailTrack> { Track = a.track, From = from, To = to, Increasing = inc });
                return;
            }
            var gap = gaps[i];
            if (gap.Path == null || gap.Start != a.track || gap.End != b.track || gap.StartIncreasing != inc || gap.EndIncreasing != endInc)
            {
                double budget = Math.Max(10, Vector3.Distance(a.transform.position, b.transform.position) * 1.5 + 10);
                if (budget > 200) throw new InvalidOperationException("Unexpected gap between neighboring bogies");
                gap.Path = OccupiedPath.Between(a.track, from, inc, b.track, to, endInc, budget, TrackLength, Connections);
                gap.Start = a.track; gap.End = b.track; gap.StartIncreasing = inc; gap.EndIncreasing = endInc;
            }
            for (int n = 0; n < gap.Path.Count; n++)
            {
                var piece = gap.Path[n];
                if (n == 0) piece.From = from;
                if (n == gap.Path.Count - 1) piece.To = to;
                Append(piece);
            }
        }

        private void Append(OccupiedPath.Segment<RailTrack> piece)
        {
            OccupiedPath.AppendContiguous(Segments, piece);
        }
        private static void Reverse(List<OccupiedPath.Segment<RailTrack>> path)
        {
            path.Reverse();
            for (int i = 0; i < path.Count; i++)
            {
                var s = path[i]; double from = s.From;
                s.From = s.To; s.To = from; s.Increasing = !s.Increasing; path[i] = s;
            }
        }
        private void Extend(RailTrack track, double span, bool inc, double distance)
        {
            extension.Clear();
            for (int i = 0; i < 16; i++)
            {
                double length = TrackLength(track), step = Math.Min(distance, inc ? length - span : span);
                extension.Add(new OccupiedPath.Segment<RailTrack> { Track = track, From = span, To = span + (inc ? step : -step), Increasing = inc });
                distance -= step;
                if (distance <= 0.001) return;
                // Only the body overhang is beyond a bogie here. Follow the native
                // selected connection; bogie-to-bogie paths still use occupied rails.
                var branch = inc ? track.GetOutBranch() : track.GetInBranch();
                if (branch == null || !branch.track) return; // Body may overhang a buffer stop.
                track = branch.track; inc = branch.first; span = inc ? 0 : TrackLength(track);
            }
            throw new InvalidOperationException("Train overhang exceeded track budget");
        }
    }
}
