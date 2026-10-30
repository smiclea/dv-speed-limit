using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityModManagerNet;

namespace DVTrainSpeedLimit
{
    public sealed class Settings : UnityModManager.ModSettings
    {
        public bool ShowInDrivingUI = true;
        public bool VerboseLogging;
        public float HudOffsetX;
        public float HudOffsetY = 8;
        public float FontSize = 20;
        public int DetailFontSize { get { return Mathf.RoundToInt(FontSize * 0.75f); } }
        public override void Save(UnityModManager.ModEntry entry) { Save(this, entry); }
        internal void Validate()
        {
            HudOffsetX = float.IsNaN(HudOffsetX) || float.IsInfinity(HudOffsetX) ? 0 : Mathf.Clamp(HudOffsetX, -1000, 1000);
            HudOffsetY = float.IsNaN(HudOffsetY) || float.IsInfinity(HudOffsetY) ? 8 : Mathf.Clamp(HudOffsetY, -500, 1000);
            FontSize = float.IsNaN(FontSize) || float.IsInfinity(FontSize) ? 20 : Mathf.Clamp(FontSize, 12, 28);
        }
    }

    public static class Main
    {
        internal static Settings Settings;
        internal static UnityModManager.ModEntry Entry;
        private static SpeedLimitController controller;
        private static StreamWriter log;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            Entry = entry;
            Settings = UnityModManager.ModSettings.Load<Settings>(entry);
            Settings.Validate();
            try
            {
                string dir = Path.Combine(entry.Path, "Logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "speed-limit.log");
                if (File.Exists(path)) File.Copy(path, Path.Combine(dir, "speed-limit.previous.log"), true);
                log = new StreamWriter(path, false) { AutoFlush = true };
            }
            catch (Exception ex) { entry.Logger.Error("Dedicated log unavailable: " + ex); }
            entry.OnToggle = Toggle;
            entry.OnGUI = DrawSettings;
            entry.OnSaveGUI = e => { Settings.Validate(); Settings.Save(e); };
            entry.OnUnload = e => { Stop(); if (log != null) log.Dispose(); log = null; return true; };
            Log("Loaded v0.3.2; Unity={0}; sign-data assembly={1}. Observed junction-route history; persistent area cache; adjustable F4 font (default 20/15).",
                Application.unityVersion, typeof(DV.Signs.SignPlacer).Assembly.GetName().Version);
            return true;
        }

        private static bool Toggle(UnityModManager.ModEntry entry, bool value)
        {
            if (value && !controller)
            {
                var host = new GameObject("DVTrainSpeedLimit");
                UnityEngine.Object.DontDestroyOnLoad(host);
                controller = host.AddComponent<SpeedLimitController>();
                Log("Enabled.");
            }
            else if (!value) Stop();
            return true;
        }

        private static void Stop()
        {
            if (controller) { controller.Cleanup(); UnityEngine.Object.Destroy(controller.gameObject); }
            controller = null;
            Log("Stopped; HUD readout removed.");
        }

        internal static void Log(string format, params object[] args)
        {
            string message = string.Format(CultureInfo.InvariantCulture, format, args);
            Entry.Logger.Log(message);
            if (log != null)
            {
                try
                {
                    if (log.BaseStream.Length > 5 * 1024 * 1024)
                    {
                        string path = Path.Combine(Entry.Path, "Logs", "speed-limit.log");
                        log.Dispose(); log = null;
                        File.Copy(path, Path.Combine(Entry.Path, "Logs", "speed-limit.previous.log"), true);
                        log = new StreamWriter(path, false) { AutoFlush = true };
                    }
                    log.WriteLine(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + message);
                }
                catch { if (log != null) log.Dispose(); log = null; }
            }
        }

        private static void DrawSettings(UnityModManager.ModEntry entry)
        {
            Settings.ShowInDrivingUI = GUILayout.Toggle(Settings.ShowInDrivingUI, "Show whole-train speed limit in F4 driving UI");
            Settings.VerboseLogging = GUILayout.Toggle(Settings.VerboseLogging, "Verbose diagnostics (5-second train snapshots and native sign positions)");
            GUILayout.Label("Font size: " + Settings.FontSize.ToString("0") + " (details " + Settings.DetailFontSize + ")");
            Settings.FontSize = Mathf.Round(GUILayout.HorizontalSlider(Settings.FontSize, 12, 28));
            GUILayout.Label("Readout offset from top-left of driving controls");
            GUILayout.Label("Horizontal: " + Settings.HudOffsetX.ToString("0") + " px");
            Settings.HudOffsetX = Mathf.Round(GUILayout.HorizontalSlider(Settings.HudOffsetX, -500, 500));
            GUILayout.Label("Vertical: " + Settings.HudOffsetY.ToString("0") + " px");
            Settings.HudOffsetY = Mathf.Round(GUILayout.HorizontalSlider(Settings.HudOffsetY, -100, 500));
            if (GUILayout.Button("Reset readout position")) { Settings.HudOffsetX = 0; Settings.HudOffsetY = 8; }
            GUILayout.Label(controller ? controller.CacheStatus : "Sign cache loads when enabled");
            GUILayout.Label("Cached areas are reused from disk; new areas are scanned automatically once.");
            if (controller && GUILayout.Button("Clear entire sign cache")) controller.ResetDiagnostics();
            GUILayout.Label(controller ? controller.Status : "Mod disabled");
            GUILayout.Label("Direction follows motion; when stopped it follows the reverser. Neutral at rest has no limit.");
            GUILayout.Label("Logs: Mods/DVTrainSpeedLimit/Logs/speed-limit.log and Unity Mod Manager/Log.txt");
        }
    }
}
