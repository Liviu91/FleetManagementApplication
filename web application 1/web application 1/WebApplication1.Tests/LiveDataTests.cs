using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Project.Controllers;
using WebApplication1.Enums;
using WebApplication1.Models;
using Xunit;
using Route = WebApplication1.Models.Route;

namespace WebApplication1.Tests;

/// <summary>
/// Tests for the two live-monitoring endpoints that power the admin dashboard:
/// <c>GetRouteGpsData</c> (the live map polyline, polled with <c>?since=</c>) and
/// <c>GetActiveVehicles</c> (the live OBD telemetry cards).
///
/// They reproduce — without a car, phone or database — the exact behaviour the browser relies
/// on, so the live map / stale-OBD fixes can be verified locally before a real-world drive.
/// </summary>
public class LiveDataTests
{
    private const int RouteId = 1;
    private static readonly DateTime Base =
        new(2026, 6, 26, 10, 0, 0, DateTimeKind.Unspecified); // Unspecified == how EF reads datetime2

    // ----------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------

    private static Route StartedRoute(DateTime startDate) => new()
    {
        Id = RouteId,
        UserId = "driver-1",
        CarId = 10,
        Status = Status.Started,
        Name = "Test Route",
        Start = "A",
        End = "B",
        StartDate = startDate
    };

    private static CarData Gps(int seconds, double lat, double lng) => new()
    {
        RouteId = RouteId,
        Timestamp = Base.AddSeconds(seconds),
        Latitude = lat.ToString(CultureInfo.InvariantCulture),
        Longitude = lng.ToString(CultureInfo.InvariantCulture)
    };

    private static CarData GpsAt(DateTime ts, double lat, double lng) => new()
    {
        RouteId = RouteId,
        Timestamp = ts,
        Latitude = lat.ToString(CultureInfo.InvariantCulture),
        Longitude = lng.ToString(CultureInfo.InvariantCulture)
    };

    private static CarData Obd(DateTime ts, string rpm, string speed = "50") => new()
    {
        RouteId = RouteId,
        Timestamp = ts,
        RPM = rpm,
        Speed = speed,
        Latitude = "45.0",
        Longitude = "25.0"
    };

    private static HomeController GpsController(Route route, IEnumerable<CarData> carData)
        => new(new FakeRepository<CarData>(carData), new FakeRepository<Route>(new[] { route }),
               null!, null!, null!);

    private static HomeController ActiveVehiclesController(Route route, IEnumerable<CarData> carData)
    {
        var car = new Car { Id = route.CarId, SerialNumber = "SN-10" };
        var driver = new AppUser { Id = route.UserId, DisplayName = "Jane Driver", Email = "jane@x.com" };
        return new HomeController(
            new FakeRepository<CarData>(carData),
            new FakeRepository<Route>(new[] { route }),
            new FakeRepository<Car>(new[] { car }),
            null!,
            MockUserManager(new List<AppUser> { driver }));
    }

    private static UserManager<AppUser> MockUserManager(IList<AppUser> drivers)
    {
        var store = new Mock<IUserStore<AppUser>>();
        var mgr = new Mock<UserManager<AppUser>>(
            store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        mgr.Setup(m => m.GetUsersInRoleAsync(It.IsAny<string>())).ReturnsAsync(drivers);
        return mgr.Object;
    }

    private static List<JsonElement> Points(ActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static string TimestampString(JsonElement point) => point.GetProperty("timestamp").GetString()!;

    private static DateTime ParseRoundtrip(string ts) =>
        DateTime.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static List<JsonElement> Vehicles(ActionResult result) => Points(result);

    // ----------------------------------------------------------------------------------------
    // GetRouteGpsData — the live map polyline
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task GetRouteGpsData_NoSince_ReturnsAllSessionPoints()
    {
        var route = StartedRoute(Base);
        var data = new[] { Gps(0, 45.10, 25.10), Gps(1, 45.11, 25.11), Gps(2, 45.12, 25.12) };
        var controller = GpsController(route, data);

        var points = Points(await controller.GetRouteGpsData(RouteId));

        Assert.Equal(3, points.Count);
    }

    [Fact]
    public async Task GetRouteGpsData_SerializesTimestampsAsUtc()
    {
        var route = StartedRoute(Base);
        var controller = GpsController(route, new[] { Gps(0, 45.10, 25.10) });

        var ts = TimestampString(Points(await controller.GetRouteGpsData(RouteId))[0]);

        // A 'Z' (or +00:00) suffix is what makes the ?since round-trip timezone-unambiguous.
        Assert.EndsWith("Z", ts);
    }

    [Fact]
    public async Task GetRouteGpsData_WithSince_ReturnsOnlyNewerPoints()
    {
        var route = StartedRoute(Base);
        var controller = GpsController(route,
            new[] { Gps(0, 45.10, 25.10), Gps(1, 45.11, 25.11), Gps(2, 45.12, 25.12) });

        var since = DateTime.SpecifyKind(Base.AddSeconds(1), DateTimeKind.Utc);
        var points = Points(await controller.GetRouteGpsData(RouteId, since));

        Assert.Single(points);
        Assert.Equal(45.12, points[0].GetProperty("lat").GetDouble(), 5);
    }

    /// <summary>
    /// Reproduces the browser's exact loop: read the latest timestamp from the JSON response,
    /// echo it back as <c>?since=</c>. This is the round-trip that was suspected of freezing the
    /// live map. Even when the value is re-parsed as a non-UTC kind (as a model binder might),
    /// the UTC normalisation keeps the comparison correct.
    /// </summary>
    [Fact]
    public async Task GetRouteGpsData_SinceRoundTrip_IsTimezoneRobust()
    {
        var route = StartedRoute(Base);
        var repo = new FakeRepository<CarData>(new[] { Gps(0, 45.10, 25.10), Gps(1, 45.11, 25.11) });
        var controller = new HomeController(repo, new FakeRepository<Route>(new[] { route }),
            null!, null!, null!);

        // 1) Initial full load — grab the last timestamp string exactly as the browser would.
        var initial = Points(await controller.GetRouteGpsData(RouteId));
        var lastTs = TimestampString(initial[^1]);

        // 2) A new point streams in.
        repo.Items.Add(Gps(2, 45.12, 25.12));

        // 3) Browser echoes the string back; simulate a model binder that yields a LOCAL kind.
        var since = DateTime.Parse(lastTs, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var fresh = Points(await controller.GetRouteGpsData(RouteId, since));

        Assert.Single(fresh);
        Assert.Equal(45.12, fresh[0].GetProperty("lat").GetDouble(), 5);
    }

    /// <summary>Full simulation of the 2-second live polling loop accumulating a polyline.</summary>
    [Fact]
    public async Task GetRouteGpsData_LivePollingLoop_AccumulatesEveryNewPoint()
    {
        var route = StartedRoute(Base);
        var repo = new FakeRepository<CarData>(new[] { Gps(0, 45.100, 25.100) });
        var controller = new HomeController(repo, new FakeRepository<Route>(new[] { route }),
            null!, null!, null!);

        var lastTs = ParseRoundtrip(TimestampString(Points(await controller.GetRouteGpsData(RouteId))[^1]));
        var received = 1;

        for (var i = 1; i <= 5; i++)
        {
            repo.Items.Add(Gps(i, 45.100 + i * 0.001, 25.100 + i * 0.001));

            var fresh = Points(await controller.GetRouteGpsData(RouteId, lastTs));

            Assert.Single(fresh); // exactly one new point each poll, never a duplicate
            lastTs = ParseRoundtrip(TimestampString(fresh[^1]));
            received += fresh.Count;
        }

        Assert.Equal(6, received);
        // A final full reload should match what incremental polling accumulated.
        Assert.Equal(6, Points(await controller.GetRouteGpsData(RouteId)).Count);
    }

    [Fact]
    public async Task GetRouteGpsData_ExcludesPointsFromPreviousSession()
    {
        // Route (re)started at Base; earlier points belong to a previous session and must not show.
        var route = StartedRoute(Base);
        var data = new[]
        {
            Gps(-3600, 10.0, 10.0),  // an hour before the current session
            Gps(0, 45.10, 25.10),
            Gps(1, 45.11, 25.11)
        };
        var controller = GpsController(route, data);

        var points = Points(await controller.GetRouteGpsData(RouteId));

        Assert.Equal(2, points.Count);
        Assert.All(points, p => Assert.True(p.GetProperty("lat").GetDouble() > 40));
    }

    // ----------------------------------------------------------------------------------------
    // GetActiveVehicles — live OBD telemetry cards
    // ----------------------------------------------------------------------------------------

    [Fact]
    public async Task GetActiveVehicles_FreshObd_ReportsValues()
    {
        var now = DateTime.UtcNow;
        var route = StartedRoute(now.AddMinutes(-5));
        var data = new[]
        {
            Obd(now.AddSeconds(-2), rpm: "3000"),
            GpsAt(now.AddSeconds(-1), 45.10, 25.10)
        };
        var controller = ActiveVehiclesController(route, data);

        var vehicle = Vehicles(await controller.GetActiveVehicles()).Single();

        Assert.Equal("3000", vehicle.GetProperty("rpm").GetString());
    }

    [Fact]
    public async Task GetActiveVehicles_ObdStoppedButGpsLive_ReportsNullObd()
    {
        // OBD reader dropped out 30s ago, but GPS keeps streaming -> OBD must read N/A, not stale.
        var now = DateTime.UtcNow;
        var route = StartedRoute(now.AddMinutes(-5));
        var data = new[]
        {
            Obd(now.AddSeconds(-30), rpm: "3000"),
            GpsAt(now.AddSeconds(-1), 45.10, 25.10)
        };
        var controller = ActiveVehiclesController(route, data);

        var vehicle = Vehicles(await controller.GetActiveVehicles()).Single();

        Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("rpm").ValueKind);
    }

    [Fact]
    public async Task GetActiveVehicles_WholeFeedStopped_ReportsNullObd()
    {
        // Device fully disconnected: no GPS or OBD for 30s. The previous heuristic kept showing
        // the last frozen reading forever; now it must read N/A because the feed is dead vs now.
        var now = DateTime.UtcNow;
        var route = StartedRoute(now.AddMinutes(-5));
        var data = new[]
        {
            Obd(now.AddSeconds(-30), rpm: "3000"),
            GpsAt(now.AddSeconds(-30), 45.10, 25.10)
        };
        var controller = ActiveVehiclesController(route, data);

        var vehicle = Vehicles(await controller.GetActiveVehicles()).Single();

        Assert.Equal(JsonValueKind.Null, vehicle.GetProperty("rpm").ValueKind);
    }

    [Fact]
    public async Task GetActiveVehicles_NoStartedRoutes_ReturnsEmpty()
    {
        var route = StartedRoute(DateTime.UtcNow);
        route.Status = Status.Assigned; // not started
        var controller = ActiveVehiclesController(route, Array.Empty<CarData>());

        Assert.Empty(Vehicles(await controller.GetActiveVehicles()));
    }
}
