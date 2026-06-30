#if ANDROID
using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Devices.Sensors;
using MauiApp1.Model;
using MauiApp1.Services;
using Debug = System.Diagnostics.Debug;

namespace MauiApp1
{
    /// <summary>
    /// Foreground service that keeps GPS logging alive while a route is in progress — even when the
    /// screen is off and the device enters Doze.
    ///
    /// The previous implementation drove GPS from a <c>Dispatcher.StartTimer</c> on the UI thread,
    /// which Android suspends once the app is backgrounded/idle, so points silently stopped being
    /// logged. A started foreground service (type "location") plus a partial wake lock keeps the
    /// process scheduled so <see cref="Geolocation"/> reads continue uninterrupted for the whole route.
    /// </summary>
    [Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeLocation)]
    public class LocationForegroundService : Service
    {
        public const string ActionStop = "MauiApp1.action.STOP_TRACKING";
        const string ExtraRouteId = "routeId";
        const int NotificationId = 0xF1EE7;     // arbitrary but stable
        const string ChannelId = "route_tracking";
        const int PollIntervalMs = 1000;

        static CancellationTokenSource? _loopCts;
        PowerManager.WakeLock? _wakeLock;

        /// <summary>Raised on each published GPS point so the page can update its local live map.</summary>
        public static event Action<int, double, double>? LocationLogged;

        /// <summary>The route currently being tracked (used by the background loop and the UI filter).</summary>
        public static int CurrentRouteId { get; private set; }

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

            _loopCts?.Cancel();
            _loopCts = new CancellationTokenSource();
            _ = TrackAsync(_loopCts.Token);

            // Restart with the last intent if the OS kills us while a route is still active.
            return StartCommandResult.RedeliverIntent;
        }

        async Task TrackAsync(CancellationToken ct)
        {
            var services = IPlatformApplication.Current?.Services;
            var mq = services?.GetService<RabbitMqService>();
            var obd = services?.GetService<ObdService>();
            int published = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var loc = await Geolocation.GetLocationAsync(
                        new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(5)), ct);

                    if (loc != null)
                    {
                        // Feed the latest fix to the OBD service so its telemetry rows carry coordinates.
                        if (obd != null)
                        {
                            obd.LastLatitude = loc.Latitude;
                            obd.LastLongitude = loc.Longitude;
                        }

                        if (mq != null)
                        {
                            await mq.PublishAsync(new RouteGpsLogEntry
                            {
                                RouteId = CurrentRouteId,
                                Timestamp = DateTime.UtcNow,
                                Latitude = loc.Latitude,
                                Longitude = loc.Longitude
                            });
                        }

                        LocationLogged?.Invoke(CurrentRouteId, loc.Latitude, loc.Longitude);
                        Debug.WriteLine($"[GPS-FGS] #{++published} route {CurrentRouteId}: {loc.Latitude}, {loc.Longitude}");
                    }
                    else
                    {
                        Debug.WriteLine("[GPS-FGS] GetLocationAsync returned null");
                    }
                }
                catch (System.OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GPS-FGS] Error: {ex.Message}");
                }

                try { await Task.Delay(PollIntervalMs, ct); }
                catch (System.OperationCanceledException) { break; }
            }
        }

        void StopTracking()
        {
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
