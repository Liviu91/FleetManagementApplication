using MauiApp1.Model;
using MauiApp1.Services;
using Microsoft.Maui.ApplicationModel.Communication;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Timers;

namespace MauiApp1.Pages;

public partial class RoutesPage : ContentPage
{
    readonly RouteService _routeService;
    readonly ObservableCollection<RouteDto> _routes = new();
    readonly RabbitMqService _mq;
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

    public ObservableCollection<RouteDto> Routes => _routes;

#if ANDROID
    public RoutesPage(RouteService routeService, RabbitMqService mq, ObdService obdService)
#else
    public RoutesPage(RouteService routeService, RabbitMqService mq)
#endif
	{
		InitializeComponent();
        _routeService = routeService;
        BindingContext = this;
        _mq = mq;
#if ANDROID
        _obdService = obdService;
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

        // Start OBD monitoring on separate queue
#if ANDROID
        _obdService.CurrentRouteId = _currentRoute.Id;
        await StartObdAsync();
#endif
    }

#if ANDROID
    async Task StartObdAsync()
    {
        try
        {
            await _obdService.Connect();
            await DisplayAlert("OBD Connected", $"Connected to OBD device. Live telemetry active.", "OK");
            _ = _obdService.StartMonitoring();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OBD] Error: {ex.Message}");
            await DisplayAlert("OBD Warning", $"OBD not available: {ex.Message}\nGPS tracking will continue without telemetry.", "OK");
        }
    }
#endif

    async void OnFinishClicked(object sender, EventArgs e)
    {
        _gpsCts?.Cancel();
        _gpsCts = null!;
        _currentRoute = null!;

        // Stop OBD monitoring
#if ANDROID
        _obdService.StopMonitoring();
#endif

        var id = (int)((Button)sender).CommandParameter;
        if (await _routeService.UpdateRouteStatusAsync(id, "Finished"))
            await LoadRoutesAsync();
    }

    void StartGpsLogging()
    {
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
    }

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
                Timestamp = DateTime.UtcNow,
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
                Timestamp = DateTime.UtcNow,
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