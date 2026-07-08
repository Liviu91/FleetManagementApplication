using Microsoft.EntityFrameworkCore;

namespace WorkerService.Models
{
    public class WebAppDbContext : DbContext
    {
        public WebAppDbContext(DbContextOptions<WebAppDbContext> options) : base(options)
        {

        }

        public DbSet<Car> Cars { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Route> Routes { get; set; }
        public DbSet<CarData> CarDatas { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            //modelBuilder.Entity<Route>().HasIndex(nameof(Route.CarId), nameof(Route.UserId)).IsUnique();
        }
    }
}
