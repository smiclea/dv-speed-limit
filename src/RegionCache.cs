using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace DVTrainSpeedLimit
{
    public sealed class CachedPole
    {
        public double X, Y, Z, FacingX, FacingZ;
        public int[] Speeds;
    }
    public sealed class CachedArea
    {
        public string Key;
        public CachedPole[] Poles;
    }
    public sealed class SignCacheDocument
    {
        public int Format = 1;
        public string GameBuild;
        public CachedArea[] Areas;
    }

    // Area scheduling/completion is independent of scene hierarchy size. Even an
    // area with no signs is recorded, so it is not searched on every later visit.
    internal sealed class RegionCache
    {
        private readonly Dictionary<string, CachedArea> areas = new Dictionary<string, CachedArea>(StringComparer.Ordinal);
        private readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
        internal int AreaCount { get { return areas.Count; } }
        internal int PendingCount { get { return queued.Count; } }
        internal int PoleCount { get; private set; }

        internal bool TryQueue(string key)
        {
            if (string.IsNullOrEmpty(key) || areas.ContainsKey(key)) return false;
            return queued.Add(key);
        }
        internal void Cancel(string key) { queued.Remove(key); }
        internal void Complete(string key, CachedPole[] poles)
        {
            var area = new CachedArea { Key = key, Poles = poles ?? new CachedPole[0] };
            Validate(area);
            CachedArea existing;
            if (areas.TryGetValue(key, out existing)) PoleCount -= existing.Poles.Length;
            areas[key] = area;
            PoleCount += area.Poles.Length;
            queued.Remove(key);
        }
        internal void Clear() { areas.Clear(); queued.Clear(); PoleCount = 0; }

        internal void Import(SignCacheDocument document, string build)
        {
            if (document == null || document.Format != 1 || document.GameBuild != build || document.Areas == null)
                throw new InvalidDataException("Cache format/game build changed");
            // Validate before changing the live cache.
            foreach (var area in document.Areas) Validate(area);
            Clear();
            foreach (var area in document.Areas) Complete(area.Key, area.Poles);
        }
        internal SignCacheDocument Export(string build)
        {
            var values = new CachedArea[areas.Count];
            areas.Values.CopyTo(values, 0);
            return new SignCacheDocument { GameBuild = build, Areas = values };
        }
        internal SignGeometry.Sign[] Signs()
        {
            var result = new List<SignGeometry.Sign>(PoleCount);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var area in areas.Values) foreach (var pole in area.Poles)
            {
                string key = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}/{1:0.0}/{2:0.0}/{3:0.00}/{4:0.00}/{5}",
                    pole.X, pole.Y, pole.Z, pole.FacingX, pole.FacingZ, string.Join(",", pole.Speeds));
                if (!seen.Add(key)) continue;
                result.Add(new SignGeometry.Sign { X = pole.X, Y = pole.Y, Z = pole.Z, FacingX = pole.FacingX, FacingZ = pole.FacingZ, Speeds = pole.Speeds });
            }
            return result.ToArray();
        }
        private static void Validate(CachedArea area)
        {
            if (area == null || string.IsNullOrEmpty(area.Key) || area.Poles == null) throw new InvalidDataException("Invalid cached area");
            foreach (var pole in area.Poles)
            {
                if (pole == null || !Finite(pole.X) || !Finite(pole.Y) || !Finite(pole.Z) || !Finite(pole.FacingX) || !Finite(pole.FacingZ) ||
                    pole.Speeds == null || pole.Speeds.Length == 0 || pole.Speeds.Length > 8)
                    throw new InvalidDataException("Invalid cached pole");
                foreach (int speed in pole.Speeds) if (speed <= 0 || speed > 300) throw new InvalidDataException("Invalid cached speed");
            }
        }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }

    internal static class SignCacheDisk
    {
        internal static SignCacheDocument Read(string path)
        {
            if (!File.Exists(path)) return null;
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                return (SignCacheDocument)new XmlSerializer(typeof(SignCacheDocument)).Deserialize(reader);
        }
        internal static void Write(string path, SignCacheDocument document)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            try
            {
                using (var stream = File.Create(temp)) new XmlSerializer(typeof(SignCacheDocument)).Serialize(stream, document);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        internal static void Delete(string path) { if (File.Exists(path)) File.Delete(path); }
    }
}
