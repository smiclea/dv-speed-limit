using System;
using System.Collections.Generic;

namespace DVTrainSpeedLimit
{
    // Pure geometry: safe to run on a worker, with no Unity object access.
    internal static class SignGeometry
    {
        internal struct Point
        {
            internal double X, Y, Z, Span;
            internal Point(double x, double y, double z, double span) { X = x; Y = y; Z = z; Span = span; }
        }
        internal sealed class Sign
        {
            internal double X, Y, Z, FacingX, FacingZ;
            internal int[] Speeds;
        }
        internal struct Match
        {
            internal int SignIndex;
            internal double Span;
            internal bool Increasing;
        }

        internal static List<Match> MatchSigns(Point[] points, Sign[] signs)
        {
            if (points.Length < 2) throw new InvalidOperationException("Track geometry has too few points");
            double minX = double.PositiveInfinity, minY = minX, minZ = minX;
            double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
            foreach (var p in points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            var matches = new List<Match>();
            for (int s = 0; s < signs.Length; s++)
            {
                var sign = signs[s];
                if (sign.X < minX - 1.25 || sign.X > maxX + 1.25 || sign.Y < minY - 1.25 ||
                    sign.Y > maxY + 1.25 || sign.Z < minZ - 1.25 || sign.Z > maxZ + 1.25) continue;
                double best = double.PositiveInfinity, span = 0, facingDot = 0;
                for (int i = 1; i < points.Length; i++)
                {
                    var a = points[i - 1]; var b = points[i];
                    double dx = b.X - a.X, dy = b.Y - a.Y, dz = b.Z - a.Z;
                    double lengthSquared = dx * dx + dy * dy + dz * dz;
                    if (lengthSquared <= 0) continue;
                    double t = Math.Max(0, Math.Min(1, ((sign.X - a.X) * dx + (sign.Y - a.Y) * dy + (sign.Z - a.Z) * dz) / lengthSquared));
                    double ex = sign.X - a.X - dx * t, ey = sign.Y - a.Y - dy * t, ez = sign.Z - a.Z - dz * t;
                    double distance = ex * ex + ey * ey + ez * ez;
                    if (distance >= best) continue;
                    best = distance;
                    span = a.Span + t * (b.Span - a.Span);
                    double horizontalLength = Math.Sqrt(dx * dx + dz * dz);
                    facingDot = horizontalLength > 0 ? (sign.FacingX * dx + sign.FacingZ * dz) / horizontalLength : 0;
                }
                // The pole has been offset back onto the track center by the indexer.
                // Adjacent tracks, another level of track, and wrong-facing signs are
                // rejected rather than selected by general world-space proximity.
                if (best <= 1.25 * 1.25 && Math.Abs(facingDot) >= 0.85)
                    matches.Add(new Match { SignIndex = s, Span = span, Increasing = facingDot < 0 });
            }
            matches.Sort((a, b) => a.Span.CompareTo(b.Span));
            return matches;
        }
    }
}
