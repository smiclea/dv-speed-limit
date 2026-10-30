using System;
using System.Diagnostics;
using System.Reflection;
using DV.HUD;
using DV.UI.LocoHUD;
using DV.Utils;
using UnityEngine;

namespace DVTrainSpeedLimit
{
    public sealed class SpeedLimitController : MonoBehaviour
    {
        private NativeProfiles profiles = new NativeProfiles();
        private readonly TrainFootprint footprint = new TrainFootprint();
        private readonly DrivingReadout readout = new DrivingReadout();
        private TrainCar lastLoco;
        private float nextUpdate, nextError, nextSnapshot, retryAfter;
        private string lastState;
        private string lastError;
        private int lastDirection;
        private bool wasMoving;
        private bool indexing;
        private int? heldLimit;
        private string heldDetail, heldStatus;
        private Trainset heldSet;
        private int heldRevision;
        private float currentVelocity;
        public string Status { get; private set; } = "Open the F4 driving UI in a locomotive.";
        public string CacheStatus { get { return profiles.CacheStatus; } }

        public void ResetDiagnostics()
        {
            ClearHeldReading();
            profiles.Reindex();
            nextSnapshot = nextError = nextUpdate = 0;
            lastState = null;
            retryAfter = 0;
            Main.Log("Clear entire persistent sign cache and diagnostic snapshot requested.");
        }

        private void Update()
        {
            HUDManager manager = null;
            try
            {
                if (Time.unscaledTime < nextUpdate)
                {
                    if (indexing) profiles.StepIndex();
                    return;
                }
                nextUpdate = Time.unscaledTime + 0.2f;
                manager = SingletonBehaviour<HUDManager>.Instance;
                var interfacer = SingletonBehaviour<HUDInterfacer>.Instance;
                var loco = interfacer ? interfacer.currentCar : null;
                bool visible = manager && manager.locoHUDVisible && manager.currentHUD && Main.Settings.ShowInDrivingUI;
                indexing = visible && loco && profiles.Scanning;
                if (indexing) profiles.StepIndex();
                if (!manager || !manager.locoHUDVisible || !manager.currentHUD || !Main.Settings.ShowInDrivingUI)
                {
                    ClearHeldReading();
                    profiles.ForgetRoute("driving UI hidden");
                    Status = "Open the F4 driving UI in a locomotive.";
                    readout.Refresh(manager, "", false, false);
                    return;
                }
                if (!loco || !loco.IsLoco) { Unavailable(manager, "Enter a locomotive", null); return; }
                if (loco != lastLoco)
                {
                    ClearHeldReading();
                    Main.Log("Driving locomotive changed: {0} -> {1}.", lastLoco ? lastLoco.ID : "none", loco.ID);
                    lastLoco = loco; lastDirection = 0; wasMoving = false; lastState = null;
                }
                float velocity = loco.GetForwardSpeed();
                currentVelocity = velocity;
                if (float.IsNaN(velocity) || float.IsInfinity(velocity)) throw new InvalidOperationException("Locomotive velocity unavailable");
                // Motion wins over a changed reverser while the train is still rolling.
                // Hysteresis prevents direction flicker at rest and during coupling.
                if (Math.Abs(velocity) >= 0.15f) wasMoving = true;
                else if (Math.Abs(velocity) <= 0.03f) wasMoving = false;
                int direction = wasMoving ? (velocity > 0 ? 1 : -1) : Reverser(loco);
                if (direction == 0) { Unavailable(manager, "Select forward or reverse", null); return; }
                if (!ReferenceEquals(heldSet, loco.trainset) || lastDirection != direction) ClearHeldReading();
                if (!profiles.Ready) { Pending(manager, profiles.IndexStatus); return; }
                if (Time.unscaledTime < retryAfter) return;
                var timer = Stopwatch.StartNew();
                TrainFootprint train;
                try { train = footprint.Build(loco, direction); }
                catch { profiles.ForgetRoute("train footprint unavailable"); throw; }
                profiles.Begin(train, loco, direction, velocity);
                int limit = int.MaxValue;
                string limitingTrack = null;
                foreach (var segment in train.Segments)
                {
                    int value = profiles.Minimum(segment);
                    if (value < limit) { limit = value; limitingTrack = segment.Track.name; }
                }
                if (limit == int.MaxValue) throw new InvalidOperationException("Empty occupied train path");
                var head = train.Segments[train.Segments.Count - 1];
                int headLimit = profiles.At(head.Track, head.To, head.Increasing);
                string detail = headLimit > limit ? "Front " + headLimit + " | Rear restricted" : train.Cars + " cars | " + train.Length.ToString("0") + " m";
                Status = "Whole train: " + limit + " km/h; front: " + headLimit + " km/h; " + train.Cars + " cars.";
                heldLimit = limit; heldDetail = detail; heldStatus = Status;
                heldSet = loco.trainset; heldRevision = profiles.TrackingRevision;
                DrawHeldReading(manager);
                string state = loco.ID + ":" + direction + ":" + train.Cars + ":" + limit + ":" + headLimit;
                bool periodic = Time.unscaledTime >= nextSnapshot;
                if (state != lastState || periodic)
                {
                    Main.Log("Limit: loco={0}, direction={1} ({2}), cars={3}, occupied={4:0.0}m, whole={5}km/h, front={6}km/h, limitingTrack={7}, cache={8}, update={9:0.00}ms.",
                        loco.ID, direction, wasMoving ? "motion" : "reverser", train.Cars, train.Length, limit, headLimit, limitingTrack, profiles.CacheCount, timer.Elapsed.TotalMilliseconds);
                    if (periodic && Main.Settings.VerboseLogging)
                    {
                        foreach (var segment in train.Segments)
                            Main.Log("  occupied track={0} id={1}, increasing={2}, span={3:0.000}->{4:0.000}, min={5}",
                                segment.Track.name, segment.Track.GetInstanceID(), segment.Increasing, segment.From, segment.To, profiles.Minimum(segment));
                        foreach (var car in loco.trainset.cars) foreach (var bogie in car.Bogies)
                            Main.Log("  car={0}, bogie={1}, track={2}, span={3:0.000}, orientation={4}",
                                car.ID, bogie.name, bogie.track.name, bogie.traveller.Span, bogie.TrackDirectionSign);
                    }
                    nextSnapshot = Time.unscaledTime + (Main.Settings.VerboseLogging ? 5 : 30);
                }
                lastState = state;
                if (direction != lastDirection) Main.Log("Travel direction now {0}; occupied path and directional signs reevaluated.", direction);
                lastDirection = direction;
            }
            catch (ProfilePending) { Pending(manager, "Reading track limits..."); }
            catch (Exception ex)
            {
                indexing = false;
                if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                Unavailable(manager, "Limit unavailable - see mod log", ex);
                retryAfter = Time.unscaledTime + 2;
            }
        }

        private static int Reverser(TrainCar loco)
        {
            var sim = loco.SimController;
            var controls = sim ? sim.controlsOverrider : null;
            var reverser = controls ? controls.Reverser : null;
            if (!reverser) throw new InvalidOperationException("Locomotive reverser unavailable");
            float value = reverser.Value;
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value > 1)
                throw new InvalidOperationException("Invalid reverser value: " + value);
            return Math.Abs(value - 0.5f) <= 0.0001f ? 0 : value > 0.5f ? 1 : -1;
        }

        private void Unavailable(HUDManager manager, string reason, Exception ex)
        {
            ClearHeldReading();
            Status = reason;
            readout.Refresh(manager, "<b>TRAIN LIMIT  --</b>\n<size=" + Main.Settings.DetailFontSize + ">" + reason + "</size>", false, false);
            // Genuine failures clear the number; normal background work uses Pending.
            string state = ex == null ? reason : ex.Message;
            if (ex != null && (state != lastError || Time.unscaledTime >= nextError))
            {
                Main.Log("Readout unavailable: {0}{1}", reason, ex == null ? "" : ". " + ex);
                nextError = Time.unscaledTime + 30;
                lastError = state;
            }
            lastState = state;
        }

        private void Pending(HUDManager manager, string reason)
        {
            if (heldLimit.HasValue && lastLoco && ReferenceEquals(heldSet, lastLoco.trainset) && heldRevision == profiles.TrackingRevision)
            {
                Status = heldStatus + " Updating track data...";
                DrawHeldReading(manager);
                return;
            }
            Unavailable(manager, reason, null);
        }

        private void DrawHeldReading(HUDManager manager)
        {
            bool overspeed = Math.Abs(currentVelocity) * 3.6f > heldLimit.Value + 0.5f;
            readout.Refresh(manager, "<b>TRAIN LIMIT  " + heldLimit.Value + " km/h</b>\n<size=" + Main.Settings.DetailFontSize + ">" + heldDetail + "</size>", true, overspeed);
        }

        private void ClearHeldReading()
        {
            heldLimit = null; heldDetail = heldStatus = null; heldSet = null;
        }

        private bool cleaned;
        public void Cleanup() { readout.Clear(); if (!cleaned) profiles.Dispose(); cleaned = true; }
        private void OnDestroy() { Cleanup(); }
    }
}
