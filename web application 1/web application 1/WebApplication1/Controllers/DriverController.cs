using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WebApplication1.DTOs;
using WebApplication1.Models;
using WebApplication1.Repository;
using Route = WebApplication1.Models.Route;

[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[Route("api/[controller]")]
[ApiController]
public class DriverController : ControllerBase
{
    private readonly IRepository<Route> _routeRepository;
    private readonly UserManager<AppUser> _userManager;

    public DriverController(IRepository<Route> routeRepo, UserManager<AppUser> userManager)
    {
        _routeRepository = routeRepo;
        _userManager = userManager;
    }

    [HttpGet("routes")]
    public async Task<IActionResult> GetRoutes()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var routes = await _routeRepository.GetAll();
        var result = routes.Where(x => x.UserId == userId).Select(x => new RouteDisplayDTO
        {
            Id = x.Id,
            CarId = x.CarId,
            Name = x.Name,
            Start = x.Start,
            End = x.End,
            Status = x.Status,
            StartDate = x.StartDate,
            EndDate = x.EndDate,
            CarSerialNumber = x.Car.SerialNumber
        }).ToList();
        //var routes = (await _routeRepository.GetAll())
        //             .Where(r => r.UserId == userId)
        //             .Select(r => new {
        //                 r.Id,
        //                 r.Name,
        //                 r.Start,
        //                 r.End,
        //                 r.Status,
        //                 r.StartDate,
        //                 r.EndDate
        //             });

        return Ok(result);
    }

    [HttpPost("updatestatus")]
    [Authorize(Roles = "Driver")]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateRouteStatusDTO dto)
    {
        var route = await _routeRepository.GetAsync(dto.RouteId);
        if (route == null || route.UserId != User.FindFirstValue(ClaimTypes.NameIdentifier))
            return Unauthorized();

        if (Enum.TryParse<WebApplication1.Enums.Status>(dto.Status, out var parsedStatus))
        {
            if ((route.Status == WebApplication1.Enums.Status.Assigned && parsedStatus == WebApplication1.Enums.Status.Started) ||
                (route.Status == WebApplication1.Enums.Status.Started && parsedStatus == WebApplication1.Enums.Status.Finished))
            {
                route.Status = parsedStatus;
                if (parsedStatus == WebApplication1.Enums.Status.Finished)
                {
                    route.EndDate = DateTime.UtcNow;
                }
                await _routeRepository.UpdateAsync(route);
                return Ok();
            }
            else
            {
                return BadRequest("Invalid status transition");
            }
        }

        return Ok();
    }
}