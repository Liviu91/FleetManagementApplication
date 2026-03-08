using Microsoft.EntityFrameworkCore;
using WorkerService;
using WorkerService.Models;
using WorkerService.Repository;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddDbContext<WebAppDbContext>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("WebAppDbConnectionString")));
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

var host = builder.Build();
host.Run();
