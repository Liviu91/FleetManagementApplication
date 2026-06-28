using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using WebApplication1.DTOs;
using WebApplication1.Models;
using WebApplication1.Repository;
using Route = WebApplication1.Models.Route;

namespace Project.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IRepository<CarData> _carDataRepository;
        private readonly IRepository<Route> _routeRepository;
        private readonly IRepository<Car> _carRepository;
        private readonly IRepository<Driver> _userRepository;
        private readonly UserManager<AppUser> _userManager;

        // OBD telemetry is considered live only if the latest real OBD reading is at most this
        // many seconds newer-than-stale relative to the rest of the feed.
        private const int ObdStaleSeconds = 15;
        // The whole telemetry feed (GPS + OBD) is considered dead if nothing at all arrived
        // within this window relative to the server clock. This catches a full device
        // disconnect, where the previous heuristic kept showing the last frozen values forever.
        private const int FeedStaleSeconds = 15;
        // A new driving session is assumed to begin after any gap in telemetry longer than this.
        // The current session is derived from the data itself (see FindSessionStart) instead of
        // the server-stamped StartDate, which made finished routes render "too short". Large
        // enough to absorb in-drive stops (traffic lights, loading) yet small enough to separate
        // genuinely distinct drives.
        private const int SessionGapMinutes = 20;

        public HomeController(IRepository<CarData> carDataRepository, IRepository<Route> routeRepository, IRepository<Car> carRepository, IRepository<Driver> userRepository, UserManager<AppUser> userManager)
        {
            _carDataRepository = carDataRepository;
            _routeRepository = routeRepository;
            _carRepository = carRepository;
            _userRepository = userRepository;
            _userManager = userManager;
        }

        //[Authorize]
        //public IActionResult Index()
        //{
        //    //var data = (await _carDataRepository.GetAll()).ToList();
        //    //var route = (await _routeRepository.GetAll()).ToList();
        //    //var users = (await _userRepository.GetAll()).ToList();
        //    //var cars = (await _carRepository.GetAll()).ToList();

        //    //var entries = new List<DisplayDTO>();
        //    //foreach(var e in data)
        //    //{
        //    //    entries.Add(new DisplayDTO
        //    //    {
        //    //        FirstName = users.Single(x => x.Id == route.Single(z => z.Id == e.RouteId).UserId).FirstName,
        //    //        LastName = users.Single(x => x.Id == route.Single(z => z.Id == e.RouteId).UserId).LastName,
        //    //        SerialNumber = cars.Single(x => x.Id == route.Single(z => z.Id == e.RouteId).CarId).SerialNumber,
        //    //        RPM = e.RPM,
        //    //        EngineCoolantTemperature = e.EngineCoolantTemperature,
        //    //        Timestamp = e.Timestamp
        //    //    });
        //    //}

        //    //var routeDisplay = route.Select(r => new RouteDisplayDTO
        //    //{
        //    //    UserFullName = users.FirstOrDefault(u => u.Id == r.UserId) is var user && user != null ? $"{user.FirstName} {user.LastName}" : "Unknown",
        //    //    CarSerialNumber = cars.FirstOrDefault(c => c.Id == r.CarId)?.SerialNumber ?? "Unknown",
        //    //    Name = r.Name,
        //    //    Start = r.Start,
        //    //    End = r.End,
        //    //    StartDate = r.StartDate,
        //    //    EndDate = r.EndDate,
        //    //    Status = r.Status
        //    //}).ToList();

        //    //ViewBag.Routes = routeDisplay;
        //    if (User.Identity.IsAuthenticated)
        //    {
        //        if (User.IsInRole("Admin"))
        //            return RedirectToAction("Admin");

        //        if (User.IsInRole("Driver"))
        //            return RedirectToAction("Driver");
        //    }
        //    return View();
        //    //return View(entries);


        //    // Show welcome or login prompt
        //}

        [Authorize]
        public async Task<IActionResult> Index()
        {
            // Avoid redirecting here during startup; just show a simple landing page
            var user = await Task.FromResult(User);

            if (user.Identity.IsAuthenticated)
            {
                if (user.IsInRole("Admin"))
                    return RedirectToAction("Admin");

                if (user.IsInRole("Driver"))
                    return RedirectToAction("Driver");
            }

            //return RedirectToAction("AccessDenied", "Account");
            return View();
        }

        [Authorize(Roles = "Driver")]
        public async Task<IActionResult> Driver()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var allRoutes = await _routeRepository.GetAll();
            var routes = allRoutes
                .Where(r => r.UserId == userId)
                .OrderByDescending(r => r.StartDate)
                .ToList();

            var routeIds = routes.Select(r => r.Id).ToList();

            var allCarData = await _carDataRepository.GetAll();
            var carData = allCarData
                .Where(cd => routeIds.Contains(cd.RouteId))
                .ToList();

            var cars = await _carRepository.GetAll();

            var routeDisplay = routes.Select(r => new RouteDisplayDTO
            {
                Id = r.Id,
                UserFullName = "", // Driver doesn't need this
                CarSerialNumber = cars.FirstOrDefault(c => c.Id == r.CarId)?.SerialNumber ?? "Unknown",
                Name = r.Name,
                Start = r.Start,
                End = r.End,
                StartDate = r.StartDate,
                EndDate = r.EndDate,
                Status = r.Status
            }).ToList();

            var carDataDisplay = carData.Select(cd =>
            {
                var route = routes.FirstOrDefault(r => r.Id == cd.RouteId);
                var car = cars.FirstOrDefault(c => c.Id == route.CarId);

                return new DisplayDTO
                {
                    RouteId = cd.RouteId,
                    FirstName = "", // Optional: set to logged-in user's name if needed
                    LastName = "",
                    SerialNumber = car?.SerialNumber ?? "Unknown",
                    RPM = cd.RPM,
                    EngineCoolantTemperature = cd.EngineCoolantTemperature,
                    Timestamp = cd.Timestamp,
                    Longitude = cd.Longitude,
                    Latitude = cd.Latitude
                };
            }).ToList();

            ViewBag.CarData = carDataDisplay;
            return View(routeDisplay);
        }

        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Admin()
        {
            var data = (await _carDataRepository.GetAll()).ToList();
            var routes = (await _routeRepository.GetAll()).ToList();
            var cars = (await _carRepository.GetAll()).ToList();
            var users = (await _userManager.GetUsersInRoleAsync("Driver"));

            var entries = new List<DisplayDTO>();
            foreach (var e in data)
            {
                var route = routes.FirstOrDefault(z => z.Id == e.RouteId);
                var user = users.FirstOrDefault(x => x.Id == route?.UserId);
                var car = cars.FirstOrDefault(x => x.Id == route?.CarId);

                if (user != null && car != null)
                {
                    entries.Add(new DisplayDTO
                    {
                        SerialNumber = car.SerialNumber,
                        RPM = e.RPM,
                        EngineCoolantTemperature = e.EngineCoolantTemperature,
                        Timestamp = e.Timestamp
                    });
                }
            }

            return View(entries);
        }

        [Route("/addUser")]
        [HttpPost]
        public async Task<IActionResult> AddUser([FromBody] CreateUserDTO user)
        {
            // Check if user already exists
            var existingUser = await _userManager.FindByEmailAsync(user.Email);
            if (existingUser != null)
            {
                return BadRequest(new { error = "A user with this email already exists." });
            }

            // Create the AppUser for Identity
            var appUser = new AppUser
            {
                UserName = user.Email,
                Email = user.Email,
                DisplayName = user.DisplayName
            };

            // Use provided password or generate a default one
            var password = string.IsNullOrWhiteSpace(user.Password) ? "Pass1234!" : user.Password;
            var result = await _userManager.CreateAsync(appUser, password);

            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return BadRequest(new { error = errors });
            }

            // Assign the role (default to Driver if not specified)
            var role = string.IsNullOrWhiteSpace(user.Role) ? "Driver" : user.Role;
            var roleResult = await _userManager.AddToRoleAsync(appUser, role);

            if (!roleResult.Succeeded)
            {
                // Rollback user creation if role assignment fails
                await _userManager.DeleteAsync(appUser);
                var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                return BadRequest(new { error = $"User created but role assignment failed: {errors}" });
            }

            return Ok(new { message = $"User '{user.DisplayName}' created successfully with role '{role}'." });
        }

        [Route("/addCar")]
        [HttpPost]
        public async Task<IActionResult> AddCar([FromBody] CarDTO car)
        {
            var entry = new Car { SerialNumber = car.SerialNumber };
            await _carRepository.AddAsync(entry);
            return Ok();
        }

        [Route("/addRoute")]
        [HttpPost]
        public async Task<IActionResult> AddRoute([FromBody] RouteDTO route)
        {
            var entry = new Route { 
                UserId = route.UserId, 
                CarId = route.CarId, 
                Name = route.Name, 
                StartDate = route.StartDate, 
                EndDate = null,
                End = route.End,
                Start = route.Start,
                Timestamp = DateTime.UtcNow,
                Status = WebApplication1.Enums.Status.Assigned
            };
            await _routeRepository.AddAsync(entry);
            return Ok();
        }

        //[Route("getUsers")]
        //[HttpGet]
        //public async Task<ActionResult<IEnumerable<UserDTO>>> GetUsers()
        //{
        //    var users = await _userRepository.GetAll();
        //    var result = users.Select(x => new UserDTO { Id = x.Id, FirstName = x.FirstName, LastName = x.LastName }).ToList();
        //    return Ok(result);
        //}

        [Route("getUsers")]
        [HttpGet]
        public async Task<ActionResult<IEnumerable<UserDTO>>> GetUsers()
        {
            try
            {
                var drivers = await _userManager.GetUsersInRoleAsync("Driver");
                var result = drivers.Select(x => new UserDTO
                {
                    Id = x.Id,
                    FirstName = x.DisplayName ?? x.Email, // fallback to email if no display name
                    LastName = "" // optionally keep empty or remove LastName from DTO
                }).ToList();

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Error retrieving users: {ex.Message}" });
            }
        }

        [Route("getCars")]
        [HttpGet]
        public async Task<ActionResult<IEnumerable<CarDTO>>> GetCars()
        {
            try
            {
                var cars = await _carRepository.GetAll();
                var result = cars.Select(x => new CarDTO { Id = x.Id, SerialNumber = x.SerialNumber }).ToList();
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = $"Error retrieving cars: {ex.Message}" });
            }
        }

        [HttpPost]
        [Authorize(Roles = "Driver")]
        [Route("updateRouteStatus")]
        public async Task<IActionResult> UpdateRouteStatus([FromBody] StatusUpdateDTO dto)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var route = (await _routeRepository.GetAll()).FirstOrDefault(r => r.Id == dto.RouteId && r.UserId == userId);

            if (route == null)
                return NotFound("Route not found or access denied");

            if (Enum.TryParse<WebApplication1.Enums.Status>(dto.NewStatus, out var parsedStatus))
            {
                // Allow: Assigned→Started, Started→Finished, Finished→Assigned (restart)
                bool validTransition =
                    (route.Status == WebApplication1.Enums.Status.Assigned  && parsedStatus == WebApplication1.Enums.Status.Started)  ||
                    (route.Status == WebApplication1.Enums.Status.Started   && parsedStatus == WebApplication1.Enums.Status.Finished) ||
                    (route.Status == WebApplication1.Enums.Status.Finished  && parsedStatus == WebApplication1.Enums.Status.Assigned);

                if (validTransition)
                {
                    route.Status = parsedStatus;
                    // Every time a route is (re)started, stamp a fresh StartDate so that
                    // GetRouteGpsData can use it as an exact session boundary instead of
                    // relying on a gap heuristic that breaks when routes are restarted quickly.
                    if (parsedStatus == WebApplication1.Enums.Status.Started)
                        route.StartDate = DateTime.UtcNow;
                    await _routeRepository.UpdateAsync(route);
                    return Ok();
                }
                else
                {
                    return BadRequest("Invalid status transition");
                }
            }


            return BadRequest("Invalid status value");
        }

        [Route("getRoutes")]
        [HttpGet]
        public async Task<ActionResult> GetRoutes()
        {
            var routes = (await _routeRepository.GetAll()).ToList();
            var cars = (await _carRepository.GetAll()).ToList();
            var users = (await _userManager.GetUsersInRoleAsync("Driver")).ToList();

            var result = routes.Select(r => 
            {
                var driver = string.IsNullOrEmpty(r.UserId) ? null : users.FirstOrDefault(u => u.Id == r.UserId);
                var car = r.CarId == 0 ? null : cars.FirstOrDefault(c => c.Id == r.CarId);
                
                return new
                {
                    id = r.Id,
                    driverName = driver != null ? (driver.DisplayName ?? driver.Email ?? "Not assigned") : "Not assigned",
                    carSerialNumber = car != null ? car.SerialNumber : "Not assigned",
                    name = r.Name,
                    start = r.Start,
                    end = r.End,
                    startDate = r.StartDate,
                    endDate = r.EndDate,
                    status = r.Status.ToString(),
                    isAssigned = driver != null && car != null
                };
            }).ToList();

            return Ok(result);
        }

        [Route("getActiveVehicles")]
        [HttpGet]
        public async Task<ActionResult> GetActiveVehicles()
        {
            var routes = (await _routeRepository.GetAll())
                .Where(r => r.Status == WebApplication1.Enums.Status.Started)
                .ToList();

            var cars = await _carRepository.GetAll();
            var users = await _userManager.GetUsersInRoleAsync("Driver");
            var allCarData = await _carDataRepository.GetAll();

            var result = new List<object>();

            foreach (var route in routes)
            {
                var routeData = allCarData
                    .Where(cd => cd.RouteId == route.Id)
                    .OrderBy(cd => cd.Timestamp)
                    .ToList();

                // Only consider data from the current driving session. The boundary is derived
                // from the telemetry timestamps themselves (gap heuristic) rather than the
                // server-stamped StartDate, so it is immune to clock skew between the server and
                // the driver's phone — the same skew that made finished routes render "too short".
                var sessionStart = FindSessionStart(routeData);
                var sessionData = routeData.Where(cd => AsUtc(cd.Timestamp) >= sessionStart).ToList();

                var latestData = sessionData.LastOrDefault();
                var latestObd = sessionData.LastOrDefault(cd => cd.RPM != null && cd.RPM != "0");

                // OBD is only "live" when BOTH hold:
                //  1. a real OBD reading arrived close to the latest received data (catches the
                //     OBD reader dropping out while GPS keeps streaming), and
                //  2. the feed itself is still producing data right now (catches a full device
                //     disconnect where no GPS or OBD rows arrive at all). Without (2) the last
                //     frozen reading would be reported as live indefinitely.
                var nowUtc = DateTime.UtcNow;
                bool obdLive = latestObd != null && latestData != null
                    && (AsUtc(latestData.Timestamp) - AsUtc(latestObd.Timestamp)).TotalSeconds <= ObdStaleSeconds
                    && (nowUtc - AsUtc(latestData.Timestamp)).TotalSeconds <= FeedStaleSeconds;
                var obd = obdLive ? latestObd : null;

                var driver = users.FirstOrDefault(u => u.Id == route.UserId);
                var car = cars.FirstOrDefault(c => c.Id == route.CarId);

                result.Add(new
                {
                    routeId = route.Id,
                    routeName = route.Name,
                    driverName = driver?.DisplayName ?? driver?.Email ?? "Unknown",
                    carSerialNumber = car?.SerialNumber ?? "Unknown",
                    rpm = obd?.RPM,
                    speed = obd?.Speed,
                    throttlePosition = obd?.ThrottlePosition,
                    engineLoad = obd?.EngineLoad,
                    engineCoolantTemperature = obd?.EngineCoolantTemperature,
                    intakeAirTemperature = obd?.IntakeAirTemperature,
                    maf = obd?.MAF,
                    map = obd?.MAP,
                    fuelRailPressure = obd?.FuelRailPressure,
                    o2SensorVoltage = obd?.O2SensorVoltage,
                    lambdaValue = obd?.LambdaValue,
                    catalystTemperature = obd?.CatalystTemperature,
                    lastUpdate = AsUtc(latestData?.Timestamp ?? route.StartDate)
                });
            }

            return Ok(result);
        }

        [Route("getRouteDetails/{id}")]
        [HttpGet]
        public async Task<ActionResult> GetRouteDetails(int id)
        {
            var route = (await _routeRepository.GetAll()).FirstOrDefault(r => r.Id == id);
            if (route == null)
                return NotFound();

            var cars = await _carRepository.GetAll();
            var users = await _userManager.GetUsersInRoleAsync("Driver");
            var driver = string.IsNullOrEmpty(route.UserId) ? null : users.FirstOrDefault(u => u.Id == route.UserId);
            var car = route.CarId == 0 ? null : cars.FirstOrDefault(c => c.Id == route.CarId);

            var result = new
            {
                id = route.Id,
                name = route.Name,
                driverName = driver != null ? (driver.DisplayName ?? driver.Email ?? "Not assigned") : "Not assigned",
                carSerialNumber = car?.SerialNumber ?? "Not assigned",
                start = route.Start,
                end = route.End,
                startDate = route.StartDate,
                endDate = route.EndDate,
                status = route.Status.ToString(),
                isAssigned = driver != null && car != null
            };

            return Ok(result);
        }

        [Route("getRouteGpsData/{id}")]
        [HttpGet]
        public async Task<ActionResult> GetRouteGpsData(int id, [FromQuery] DateTime? since = null)
        {
            var route = (await _routeRepository.GetAll()).FirstOrDefault(r => r.Id == id);
            if (route == null)
                return NotFound();

            var carData = (await _carDataRepository.GetAll())
                .Where(cd => cd.RouteId == id)
                .OrderBy(cd => cd.Timestamp)
                .ToList();

            if (carData.Count == 0)
                return Ok(new List<object>());

            var obdEntries = carData.Where(cd => cd.RPM != null && cd.RPM != "0").ToList();

            // Only show GPS points from the current driving session. The session boundary is the
            // first point after the last long gap in the telemetry (see FindSessionStart) — derived
            // purely from the phone-stamped timestamps. The previous boundary compared those phone
            // timestamps against route.StartDate (a server-clock value); when the phone clock lagged
            // the server, the start of the drive fell before StartDate and was silently dropped,
            // rendering the route "too short" or as just a couple of points.
            var sessionStart = FindSessionStart(carData);

            // Valid-coordinate points within the current session only.
            var sessionWithCoords = carData
                .Where(cd => AsUtc(cd.Timestamp) >= sessionStart)
                .Where(cd => !string.IsNullOrEmpty(cd.Latitude) && !string.IsNullOrEmpty(cd.Longitude))
                .Where(cd =>
                {
                    double.TryParse(cd.Latitude, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var lat);
                    double.TryParse(cd.Longitude, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var lng);
                    return lat != 0 && lng != 0;
                })
                .ToList();

            // Deduplicate: keep one point per 0.5-second window.
            var deduplicated = new List<CarData>();
            DateTime lastTime = DateTime.MinValue;
            foreach (var cd in sessionWithCoords)
            {
                if ((cd.Timestamp - lastTime).TotalSeconds >= 0.5)
                {
                    deduplicated.Add(cd);
                    lastTime = cd.Timestamp;
                }
            }

            // Incremental polling: return only points strictly newer than 'since'. This is
            // robust because it is based on real timestamps, not fragile list indices that
            // shift as the deduplicated set grows. 'since' is normalized to UTC so the value
            // the browser echoes back (from the UTC timestamp we serialized) compares correctly
            // no matter what local time zone the server runs in.
            var sinceUtc = since.HasValue ? AsUtc(since.Value) : (DateTime?)null;
            var pointsToReturn = sinceUtc.HasValue
                ? deduplicated.Where(cd => AsUtc(cd.Timestamp) > sinceUtc.Value).ToList()
                : deduplicated;

            var gpsPoints = pointsToReturn.Select(cd =>
            {
                double.TryParse(cd.Latitude, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var latitude);
                double.TryParse(cd.Longitude, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var longitude);

                string? rpm = cd.RPM, speed = cd.Speed;
                if (rpm == null && obdEntries.Count > 0)
                {
                    var closest = obdEntries
                        .OrderBy(o => Math.Abs((o.Timestamp - cd.Timestamp).TotalSeconds))
                        .First();
                    if (Math.Abs((closest.Timestamp - cd.Timestamp).TotalSeconds) < 15)
                    {
                        rpm = closest.RPM;
                        speed = closest.Speed;
                    }
                }

                return new { lat = latitude, lng = longitude, timestamp = AsUtc(cd.Timestamp), rpm, speed };
            }).ToList();

            return Ok(gpsPoints);
        }

        // Normalize a DateTime read from the DB into an explicit UTC value. The MAUI app and the
        // worker both timestamp data with DateTime.UtcNow, but SQL Server's datetime2 column does
        // not preserve DateTimeKind, so EF returns the values as Kind=Unspecified. Serializing
        // those without a zone designator (and re-parsing the ?since echo) is timezone-ambiguous
        // and was a likely cause of the live map appearing frozen. Forcing UTC end-to-end makes
        // the polling round-trip deterministic regardless of the server's local time zone.
        private static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        // Determine the start of the current driving session for a route: the timestamp of the
        // first point after the most recent gap longer than SessionGapMinutes. Points must be
        // ordered by Timestamp ascending. Because it only ever compares phone-stamped timestamps
        // to each other (all normalized to UTC), the result is independent of the server clock and
        // therefore of any skew between the server and the driver's phone. Returns the first
        // point's timestamp when there is no such gap, and DateTime.MinValue (UTC) for an empty
        // set so every point passes the >= sessionStart filter.
        private static DateTime FindSessionStart(List<CarData> orderedPoints)
        {
            if (orderedPoints.Count == 0)
                return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

            for (int i = orderedPoints.Count - 1; i > 0; i--)
            {
                if ((AsUtc(orderedPoints[i].Timestamp) - AsUtc(orderedPoints[i - 1].Timestamp)).TotalMinutes > SessionGapMinutes)
                    return AsUtc(orderedPoints[i].Timestamp);
            }
            return AsUtc(orderedPoints[0].Timestamp);
        }


        [Route("deleteUser/{id}")]
        [HttpDelete]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteUser(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
                return NotFound(new { error = "User not found" });

            var result = await _userManager.DeleteAsync(user);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return BadRequest(new { error = errors });
            }

            return Ok(new { message = "User deleted successfully" });
        }

        [Route("deleteCar/{id}")]
        [HttpDelete]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteCar(int id)
        {
            var car = (await _carRepository.GetAll()).FirstOrDefault(c => c.Id == id);
            if (car == null)
                return NotFound(new { error = "Car not found" });

            await _carRepository.DeleteAsync(id);
            return Ok(new { message = "Car deleted successfully" });
        }

        [Route("getCarDetails/{id}")]
        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult> GetCarDetails(int id)
        {
            var car = (await _carRepository.GetAll()).FirstOrDefault(c => c.Id == id);
            if (car == null)
                return NotFound(new { error = "Car not found" });

            var routes = (await _routeRepository.GetAll()).Where(r => r.CarId == id).ToList();
            var routeIds = routes.Select(r => r.Id).ToList();
            
            var allCarData = await _carDataRepository.GetAll();
            var carDataList = allCarData
                .Where(cd => routeIds.Contains(cd.RouteId))
                .OrderByDescending(cd => cd.Timestamp)
                .Take(100)
                .ToList();

            var latestData = carDataList.FirstOrDefault(cd => cd.RPM != null) ?? carDataList.FirstOrDefault();
            var vinData = latestData?.VIN != null ? WebApplication1.Helpers.VinDecoder.Decode(latestData.VIN) : null;
            
            var result = new
            {
                id = car.Id,
                serialNumber = car.SerialNumber,
                totalRoutes = routes.Count,
                activeRoutes = routes.Count(r => r.Status == WebApplication1.Enums.Status.Started),
                vinDecoded = vinData,
                latestData = latestData != null ? new
                {
                    fuelType = latestData.FuelType,
                    fuelLevel = latestData.FuelLevel,
                    batteryVoltage = latestData.BatteryVoltage,
                    timestamp = latestData.Timestamp
                } : null,
                recentCarData = carDataList.Where(cd => cd.RPM != null).Take(20).Select(cd => new
                {
                    routeId = cd.RouteId,
                    rpm = cd.RPM,
                    speed = cd.Speed,
                    engineCoolantTemperature = cd.EngineCoolantTemperature,
                    fuelLevel = cd.FuelLevel,
                    batteryVoltage = cd.BatteryVoltage,
                    timestamp = cd.Timestamp,
                }).ToList()
            };

            return Ok(result);
        }

        [Route("getDriverDetails/{id}")]
        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult> GetDriverDetails(string id)
        {
            var driver = await _userManager.FindByIdAsync(id);
            if (driver == null)
                return NotFound(new { error = "Driver not found" });

            var driverRoutes = (await _routeRepository.GetAll()).Where(r => r.UserId == id).ToList();
            var routeIds = driverRoutes.Select(r => r.Id).ToList();
            
            var allCarData = await _carDataRepository.GetAll();
            var carDataList = allCarData
                .Where(cd => routeIds.Contains(cd.RouteId))
                .OrderByDescending(cd => cd.Timestamp)
                .Take(100)
                .ToList();

            var cars = await _carRepository.GetAll();

            var result = new
            {
                id = driver.Id,
                displayName = driver.DisplayName ?? driver.Email,
                email = driver.Email,
                totalRoutes = driverRoutes.Count,
                activeRoutes = driverRoutes.Count(r => r.Status == WebApplication1.Enums.Status.Started),
                completedRoutes = driverRoutes.Count(r => r.Status == WebApplication1.Enums.Status.Finished),
                routes = driverRoutes.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    carSerialNumber = cars.FirstOrDefault(c => c.Id == r.CarId)?.SerialNumber ?? "Unknown",
                    start = r.Start,
                    end = r.End,
                    startDate = r.StartDate,
                    endDate = r.EndDate,
                    status = r.Status.ToString()
                }).ToList(),
                recentTelemetry = carDataList.Select(cd => new
                {
                    routeId = cd.RouteId,
                    rpm = cd.RPM,
                    speed = cd.Speed,
                    engineCoolantTemperature = cd.EngineCoolantTemperature,
                    fuelLevel = cd.FuelLevel,
                    batteryVoltage = cd.BatteryVoltage,
                    timestamp = cd.Timestamp
                }).Take(10).ToList()
            };

            return Ok(result);
        }
    }
}