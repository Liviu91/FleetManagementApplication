using MauiApp1.Model;
using MauiApp1.Services;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Timers;

namespace MauiApp1.Pages;

public partial class RoutesPage : ContentPage
{
    readonly RouteService _routeService;
    readonly ObservableCollection<RouteDto> _routes = new();
    readonly RabbitMqService _mq;
    readonly ObdDiagnostics _diag;
#if ANDROID
    readonly ObdService _obdService;
#endif

    RouteDto? _currentRoute;
    CancellationTokenSource? _gpsCts;
    bool _mqErrorShown = false;
    int _gpsSuccessCount = 0;
    // Guards against overlapping location reads: a single fix can take up to a few seconds, so
    // without this the 500 ms timer would stack concurrent reads that all resolve at once.
    volatile bool _gpsReadInFlight;
    // Guards Start/Finish against double-taps and overlap. The captured diagnostics showed a single
    // Finish tapped 5x (5 redundant monitor_stop + status updates) and a Start fired while a previous
    // connect was still looping. This flag covers the short bookkeeping section of each handler.
    bool _routeOpInProgress;

    public ObservableCollection<RouteDto> Routes => _routes;

#if ANDROID
    public RoutesPage(RouteService routeService, RabbitMqService mq, ObdService obdService, ObdDiagnostics diagnostics)
#else
    public RoutesPage(RouteService routeService, RabbitMqService mq, ObdDiagnostics diagnostics)
#endif
	{
		InitializeComponent();
        _routeService = routeService;
        BindingContext = this;
        _mq = mq;
        _diag = diagnostics;
#if ANDROID
        _obdService = obdService;
        LocationForegroundService.LocationLogged += OnForegroundLocationLogged;
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadRoutesAsync();
    }

    async Task LoadRoutesAsync()
    {
        _routes.Clear();
        var items = await _routeService.GetDriverRoutesAsync();
        foreach (var r in items)
        {
            r.Points = new ObservableCollection<GpsLogEntry>();
            _routes.Add(r);
        }
    }

    async void OnStartClicked(object sender, EventArgs e)
    {
        if (_routeOpInProgress) return;          // ignore double-taps / overlapping route operations
        _routeOpInProgress = true;
        try
        {
            var id = (int)((Button)sender).CommandParameter;
            if (!await EnsureBluetoothAndLocationPermissions())
            {
                await DisplayAlert("Permission Required",
                    "Location and Bluetooth permissions are required to start the route.", "OK");
                return;
            }
            await _routeService.UpdateRouteStatusAsync(id, "Started");

            await LoadRoutesAsync();                              // rebuild list
            _currentRoute = _routes.First(r => r.Id == id);       // <- new object

            _mqErrorShown = false;
            _gpsSuccessCount = 0;

            StartGpsLogging();

#if ANDROID
            _obdService.CurrentRouteId = _currentRoute.Id;
#endif
        }
        finally
        {
            _routeOpInProgress = false;
        }

        // Connect/monitor runs OUTSIDE the UI guard: the dongle handshake can take 20s+, and we must
        // let the driver press Finish meanwhile (which cancels this connect). ObdService serializes its
        // own connect/teardown, so starting it here is race-safe.
#if ANDROID
        if (_currentRoute != null)
            await StartObdAsync();
#endif
    }

#if ANDROID
    async Task StartObdAsync()
    {
        try
        {
            await _obdService.Connect();
            await DisplayAlert("OBD Connected", "Connected to OBD device. Live telemetry active.", "OK");
            _ = _obdService.StartMonitoring();
        }
        catch (OperationCanceledException)
        {
            // The route was finished (or a new route started) while the connect was still retrying.
            // Expected — no alert; the session was already cancelled/torn down.
            Debug.WriteLine("[OBD] Connect cancelled (route finished/superseded).");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OBD] Error: {ex.Message}");
            // The phone reached the dongle over Bluetooth but its RFCOMM channel is wedged (classic
            // ELM327-clone firmware lock-up). Only a power-cycle of the dongle reliably clears it, so
            // guide the driver instead of showing a raw exception string.
            await DisplayAlert("OBD not available",
                "Couldn't reach the car's OBD adapter.\n\n" +
                "\u2022 Unplug the OBD dongle, wait ~5 seconds, plug it back in, then press Start again.\n" +
                "\u2022 If it still fails, toggle the phone's Bluetooth off and on.\n\n" +
                "GPS tracking will continue without engine telemetry.",
                "OK");
        }
    }
#endif

    async void OnFinishClicked(object sender, EventArgs e)
    {
        if (_routeOpInProgress) return;          // ignore double-taps (the logs showed Finish hit 5x)
        _routeOpInProgress = true;
        try
        {
            _gpsCts?.Cancel();
            _gpsCts = null!;
            _currentRoute = null!;

            // Stop GPS + OBD monitoring
#if ANDROID
            LocationForegroundService.Stop();
            await _obdService.StopMonitoring();
#endif

            var id = (int)((Button)sender).CommandParameter;
            if (await _routeService.UpdateRouteStatusAsync(id, "Finished"))
                await LoadRoutesAsync();
        }
        finally
        {
            _routeOpInProgress = false;
        }
    }

    // Flushes the on-device OBD diagnostics to a snapshot file, hands it to the OS share sheet, then
    // starts a FRESH working log so the next export contains only newly-captured routes (no past
    // results). Works on all platforms; on non-Android the file is essentially just session markers.
    async void OnExportObdLogsClicked(object sender, EventArgs e)
    {
        try
        {
            var snapshot = await _diag.CreateExportSnapshotAsync();
            if (snapshot == null)
            {
                await DisplayAlert("OBD Logs", "No diagnostics have been captured yet.", "OK");
                return;
            }

            await Share.RequestAsync(new ShareFileRequest
            {
                Title = "OBD diagnostics",
                File = new ShareFile(snapshot)
            });

            // Reset AFTER sharing the snapshot copy, so the shared file is untouched and the next
            // export starts clean.
            await _diag.ResetAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Export failed",
                $"{ex.Message}\n\nFile on device:\n{_diag.FilePath}", "OK");
        }
    }

    void StartGpsLogging()
    {
#if ANDROID
        // Drive GPS from a foreground service + wake lock so logging continues when the screen is
        // off or the device is idle. The UI-thread Dispatcher timer (non-Android path below) is
        // throttled/suspended by Doze and would silently stop logging once the phone sleeps.
        LocationForegroundService.Start(_currentRoute!.Id);
#else
        _gpsCts?.Cancel();
        _gpsCts = new CancellationTokenSource();
        var token = _gpsCts.Token;
        _gpsReadInFlight = false;

        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(500), () =>
        {
            if (token.IsCancellationRequested || _currentRoute is null)
                return false;                            // stop timer

            // Re-entrancy guard: skip this tick if the previous location read is still running.
            // Geolocation.GetLocationAsync can block for up to its timeout; without this guard the
            // backed-up reads all complete together and publish a burst of identical-coordinate
            // points (the duplicate rows seen in car_datas). One read at a time keeps one point
            // per real fix and makes the published stream deterministic.
            if (_gpsReadInFlight)
                return true;                             // try again on the next tick

            _ = LogPointAsync(token);
            return true;                               // repeat
        });
#endif
    }

#if ANDROID
    // The foreground GPS service publishes each fix to RabbitMQ on a background thread; mirror the
    // point into the in-memory route on the UI thread so the on-device live map keeps updating.
    void OnForegroundLocationLogged(int routeId, double lat, double lng)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var route = _currentRoute;
            if (route is null || route.Id != routeId)
                return;

            route.Points.Add(new GpsLogEntry
            {
                Timestamp = DateTime.Now,
                Latitude = lat,
                Longitude = lng
            });
            _gpsSuccessCount++;
        });
    }
#endif

    async Task LogPointAsync(CancellationToken token)
    {
        _gpsReadInFlight = true;
        try
        {
            // Capture the route up front so a Finish that nulls _currentRoute mid-read cannot throw.
            var route = _currentRoute;
            if (route is null)
                return;

            var loc = await Geolocation.GetLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(3)), token);
            if (loc == null)
            {
                Debug.WriteLine("[GPS] GetLocationAsync returned null");
                return;
            }

            // The route may have finished while we were waiting for the fix; if so, drop this point
            // instead of publishing telemetry for a route that is no longer running.
            if (token.IsCancellationRequested || _currentRoute is null)
                return;

            route.Points.Add(new GpsLogEntry
            {
                Timestamp = DateTime.Now,
                Latitude = loc.Latitude,
                Longitude = loc.Longitude
            });

#if ANDROID
            // Feed latest GPS to OBD service for its messages
            _obdService.LastLatitude = loc.Latitude;
            _obdService.LastLongitude = loc.Longitude;
#endif

            var entry = new RouteGpsLogEntry
            {
                RouteId = route.Id,
                Timestamp = DateTime.Now,
                Latitude = loc.Latitude,
                Longitude = loc.Longitude
            };

            await _mq.PublishAsync(entry);

            _gpsSuccessCount++;
            Debug.WriteLine($"[GPS] Published #{_gpsSuccessCount}: {loc.Latitude}, {loc.Longitude}");
        }
        catch (OperationCanceledException)
        {
            // Route finished while a fix was in flight — expected, nothing to report.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GPS] Error: {ex.Message}");
            if (!_mqErrorShown)
            {
                _mqErrorShown = true;
                MainThread.BeginInvokeOnMainThread(async () =>
                    await DisplayAlert("GPS/MQ Error", ex.Message, "OK"));
            }
        }
        finally
        {
            _gpsReadInFlight = false;
        }
    }

    static async Task<bool> EnsureLocationPermission()
    {
        var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
        if (status == PermissionStatus.Granted) return true;

        status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
        return status == PermissionStatus.Granted;
    }

    static async Task<bool> EnsureBluetoothAndLocationPermissions()
    {
        try
        {
            var permissions = new List<(Func<Task<PermissionStatus>> check, Func<Task<PermissionStatus>> request)>
        {
            (Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>,
             Permissions.RequestAsync<Permissions.LocationWhenInUse>),
            (Permissions.CheckStatusAsync<Permissions.Bluetooth>,
             Permissions.RequestAsync<Permissions.Bluetooth>),
        };

            foreach (var permission in permissions)
            {
                var status = await permission.check();
                if (status != PermissionStatus.Granted)
                {
                    status = await permission.request();
                    if (status != PermissionStatus.Granted)
                        return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Permission error: {ex.Message}");
            return false;
        }
    }
}