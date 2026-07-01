#if ANDROID
using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.Extensions.DependencyInjection;
using Android.Locations;
using Android.Runtime;
using MauiApp1.Model;
using MauiApp1.Services;
using Debug = System.Diagnostics.Debug;

namespace MauiApp1
{
    /// <summary>
    /// Foreground service that keeps GPS logging alive while a route is in progress — even when the
    /// screen is off and the device enters Doze.
    ///
    /// It uses CONTINUOUS location updates (<see cref="LocationManager"/> GPS + network) rather than
    /// one-shot <c>Geolocation.GetLocationAsync</c> polling. Continuous updates keep the GPS hardware
    /// hot, so a brief signal loss recovers in seconds instead of the multi-minute coordinate freezes
    /// that produced "straight line" / "barely moves" artifacts on the map. The most recent fix is
    /// cached and republished at a FIXED cadence, so the logged track has constant time granularity
    /// regardless of how irregularly the OS delivers fixes. A started foreground service (type
    /// "location") plus a partial wake lock keeps the process scheduled for the whole route.
    /// </summary>
    [Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeLocation)]
    public class LocationForegroundService : Service, ILocationListener
    {
        public const string ActionStop = "MauiApp1.action.STOP_TRACKING";
        const string ExtraRouteId = "routeId";
        const int NotificationId = 0xF1EE7;     // arbitrary but stable
        const string ChannelId = "route_tracking";
        // Publish one point at this fixed cadence so map granularity is CONSTANT in time, decoupled from
        // GPS-provider jitter. Continuous updates keep the latest fix fresh in the background.
        const int PublishIntervalMs = 1000;
        // Ask the OS to deliver location updates at least this often (0 min distance = time-based).
        const long GpsUpdateIntervalMs = 1000;

        static CancellationTokenSource? _loopCts;
        PowerManager.WakeLock? _wakeLock;

        // Continuous-location state: OnLocationChanged (main looper) writes it, the publish loop
        // (background) reads it. Guarded by _fixLock.
        LocationManager? _locationManager;
        readonly object _fixLock = new();
        double _lastLat, _lastLng;
        bool _hasFix;

        /// <summary>Raised on each published GPS point so the page can update its local live map.</summary>
        public static event Action<int, double, double>? LocationLogged;

        /// <summary>The route currently being tracked (used by the background loop and the UI filter).</summary>
        public static int CurrentRouteId { get; private set; }

        /// <summary>True while the foreground tracking service is running. Diagnostics hint only.</summary>
        public static bool IsRunning { get; private set; }

        /// <summary>True while the partial wake lock is held. Diagnostics hint only.</summary>
        public static bool WakeLockHeld { get; private set; }

        public static void Start(int routeId)
        {
            CurrentRouteId = routeId;
            var ctx = global::Android.App.Application.Context;
            var intent = new Intent(ctx, typeof(LocationForegroundService));
            intent.PutExtra(ExtraRouteId, routeId);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                ctx.StartForegroundService(intent);
            else
                ctx.StartService(intent);
        }

        public static void Stop()
        {
            var ctx = global::Android.App.Application.Context;
            var intent = new Intent(ctx, typeof(LocationForegroundService));
            intent.SetAction(ActionStop);
            ctx.StartService(intent);
        }

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == ActionStop)
            {
                StopTracking();
                StopSelfResult(startId);
                return StartCommandResult.NotSticky;
            }

            CurrentRouteId = intent?.GetIntExtra(ExtraRouteId, CurrentRouteId) ?? CurrentRouteId;

            CreateNotificationChannel();
            StartForeground(NotificationId, BuildNotification());
            AcquireWakeLock();
            IsRunning = true;
            IPlatformApplication.Current?.Services?.GetService<ObdDiagnostics>()?
                .MonitorNote("fgs_start", data: new { route = CurrentRouteId });

            _loopCts?.Cancel();
            _loopCts = new CancellationTokenSource();
            StartLocationUpdates();
            _ = PublishLoopAsync(_loopCts.Token);

            // Restart with the last intent if the OS kills us while a route is still active.
            return StartCommandResult.RedeliverIntent;
        }

        // Registers for CONTINUOUS updates from GPS (and network as a coarse fallback). Keeping the
        // providers active is what prevents the multi-minute coordinate freezes: after a brief signal
        // loss the fix resumes in seconds instead of the OS having to cold-start a one-shot request.
        void StartLocationUpdates()
        {
            try
            {
                _locationManager = (LocationManager?)GetSystemService(LocationService);
                if (_locationManager == null)
                {
                    Debug.WriteLine("[GPS-FGS] No LocationManager");
                    return;
                }

                bool any = false;
                foreach (var provider in new[] { LocationManager.GpsProvider, LocationManager.NetworkProvider })
                {
                    try
                    {
                        if (_locationManager.IsProviderEnabled(provider))
                        {
                            _locationManager.RequestLocationUpdates(provider, GpsUpdateIntervalMs, 0f, this, Looper.MainLooper);
                            any = true;
                        }
                    }
                    catch (Exception ex) { Debug.WriteLine($"[GPS-FGS] {provider} updates failed: {ex.Message}"); }
                }

                if (!any) Debug.WriteLine("[GPS-FGS] No location providers enabled");

                // Seed with the last known fix so the first point is published without waiting for a new one.
                try
                {
                    var seed = _locationManager.GetLastKnownLocation(LocationManager.GpsProvider)
                            ?? _locationManager.GetLastKnownLocation(LocationManager.NetworkProvider);
                    if (seed != null) OnLocationChanged(seed);
                }
                catch { /* last-known may be unavailable */ }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GPS-FGS] StartLocationUpdates failed: {ex.Message}");
            }
        }

        // Publishes the latest cached fix at a FIXED cadence so the logged track has constant time
        // granularity. Runs off the main thread so the RabbitMQ publish never blocks location callbacks.
        async Task PublishLoopAsync(CancellationToken ct)
        {
            var services = IPlatformApplication.Current?.Services;
            var mq = services?.GetService<RabbitMqService>();
            var obd = services?.GetService<ObdService>();
            int published = 0;

            while (!ct.IsCancellationRequested)
            {
                double lat = 0, lng = 0;
                bool has;
                lock (_fixLock) { has = _hasFix; lat = _lastLat; lng = _lastLng; }

                if (has)
                {
                    // Feed the latest fix to the OBD service so its telemetry rows carry coordinates.
                    if (obd != null)
                    {
                        obd.LastLatitude = lat;
                        obd.LastLongitude = lng;
                    }

                    try
                    {
                        if (mq != null)
                            await mq.PublishAsync(new RouteGpsLogEntry
                            {
                                RouteId = CurrentRouteId,
                                Timestamp = DateTime.Now,
                                Latitude = lat,
                                Longitude = lng
                            });
                    }
                    catch (Exception ex) { Debug.WriteLine($"[GPS-FGS] Publish error: {ex.Message}"); }

                    LocationLogged?.Invoke(CurrentRouteId, lat, lng);
                    Debug.WriteLine($"[GPS-FGS] #{++published} route {CurrentRouteId}: {lat}, {lng}");
                }
                else
                {
                    Debug.WriteLine("[GPS-FGS] Waiting for first fix\u2026");
                }

                try { await Task.Delay(PublishIntervalMs, ct); }
                catch (System.OperationCanceledException) { break; }
            }
        }

        // ---- ILocationListener --------------------------------------------------------------------
        public void OnLocationChanged(Android.Locations.Location location)
        {
            lock (_fixLock)
            {
                _lastLat = location.Latitude;
                _lastLng = location.Longitude;
                _hasFix = true;
            }
        }

        public void OnProviderEnabled(string provider) { }
        public void OnProviderDisabled(string provider) { }
        public void OnStatusChanged(string? provider, [GeneratedEnum] Availability status, Bundle? extras) { }

        void StopTracking()
        {
            IPlatformApplication.Current?.Services?.GetService<ObdDiagnostics>()?
                .MonitorNote("fgs_stop", data: new { route = CurrentRouteId });
            IsRunning = false;
            try { _locationManager?.RemoveUpdates(this); } catch { }
            _locationManager = null;
            lock (_fixLock) { _hasFix = false; }
            _loopCts?.Cancel();
            _loopCts = null;
            ReleaseWakeLock();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                StopForeground(StopForegroundFlags.Remove);
            else
#pragma warning disable CS0618
                StopForeground(true);
#pragma warning restore CS0618
        }

        public override void OnDestroy()
        {
            StopTracking();
            base.OnDestroy();
        }

        void AcquireWakeLock()
        {
            try
            {
                if (_wakeLock != null) return;
                var pm = (PowerManager?)GetSystemService(PowerService);
                _wakeLock = pm?.NewWakeLock(WakeLockFlags.Partial, "MauiApp1:RouteTrackingWakeLock");
                _wakeLock?.Acquire();
                WakeLockHeld = _wakeLock?.IsHeld ?? false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GPS-FGS] WakeLock acquire failed: {ex.Message}");
            }
        }

        void ReleaseWakeLock()
        {
            try
            {
                if (_wakeLock?.IsHeld == true)
                    _wakeLock.Release();
                _wakeLock = null;
                WakeLockHeld = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GPS-FGS] WakeLock release failed: {ex.Message}");
            }
        }

        void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            var mgr = (NotificationManager?)GetSystemService(NotificationService);
            if (mgr == null || mgr.GetNotificationChannel(ChannelId) != null) return;
            var channel = new NotificationChannel(ChannelId, "Route tracking", NotificationImportance.Low)
            {
                Description = "Keeps GPS logging active while a route is in progress."
            };
            mgr.CreateNotificationChannel(channel);
        }

        Notification BuildNotification()
        {
            return new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle("Route in progress")
                .SetContentText("Logging GPS location\u2026")
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
                .SetOngoing(true)
                .SetPriority((int)NotificationPriority.Low)
                .Build();
        }
    }
}
#endif
