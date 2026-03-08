using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using WebApplication1;
using WebApplication1.Models;
using WebApplication1.Repository;

// Default to Development if not explicitly set (e.g. when running the exe directly)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<WebAppDbContext>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("WebAppDbConnectionString")));
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

//builder.Services.AddDefaultIdentity<AppUser>(options =>
//{
//    options.SignIn.RequireConfirmedAccount = false;
//})
//.AddRoles<IdentityRole>() // Add role support
//.AddEntityFrameworkStores<WebAppDbContext>();
builder.Services.AddIdentity<AppUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
})
.AddEntityFrameworkStores<WebAppDbContext>()
.AddDefaultTokenProviders();

builder.Services.Configure<IdentityOptions>(options =>
{
    options.ClaimsIdentity.RoleClaimType = ClaimTypes.Role;
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.LogoutPath = "/Identity/Account/Logout";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
});

var jwt = builder.Configuration.GetSection("JwtSettings");   // Issuer, Audience, Key
builder.Services
    .AddAuthentication()      // do NOT set a default scheme – each controller chooses
                              //.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                                   Encoding.UTF8.GetBytes(jwt["Key"])),
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        };

        //   log every failure / success to the console
        opts.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"JWT-FAIL  ➜  {ctx.Exception.Message}");
                Console.ResetColor();
                return Task.CompletedTask;
            },
            OnTokenValidated = ctx =>
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"JWT-OK    ➜  user {ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)}");
                Console.ResetColor();
                return Task.CompletedTask;
            }
        };
    });

var allowedOrigin = builder.Configuration["Cors:MobileOrigin"]      // optional config file
                   ?? "http://10.67.157.226:7292";                  // fallback hard-coded

builder.Services.AddCors(opt =>
{
    opt.AddPolicy("MobilePolicy", p =>
    {
        p.WithOrigins(allowedOrigin)   // exact origin of MAUI app
         .AllowAnyHeader()
         .AllowAnyMethod()
         .AllowCredentials();          // if you ever send cookies
    });
});
builder.WebHost.UseUrls("http://0.0.0.0:7292");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    //app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

//app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseCors("MobilePolicy");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    await DataSeeder.SeedRolesAndAdminAsync(services);
}

app.Run();
