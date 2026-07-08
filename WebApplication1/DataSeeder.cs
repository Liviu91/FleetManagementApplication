using Microsoft.AspNetCore.Identity;
using WebApplication1.Models;

namespace WebApplication1
{
    public class DataSeeder
    {
        public static async Task SeedRolesAndAdminAsync(IServiceProvider serviceProvider)
        {
            var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = serviceProvider.GetRequiredService<UserManager<AppUser>>();

            string[] roles = { "Admin", "Driver" };

            foreach (var role in roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                    await roleManager.CreateAsync(new IdentityRole(role));
            }

            var adminEmail = "Bogdan@email.com";
            var adminPassword = "Pass1234!";

            if (await userManager.FindByEmailAsync(adminEmail) == null)
            {
                var adminUser = new AppUser { UserName = adminEmail, Email = adminEmail };
                var result = await userManager.CreateAsync(adminUser, adminPassword);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(adminUser, "Admin");
                }
            }

            var driverEmail = "Driver1@email.com";
            var driverPassword = "Pass1234!";

            if (await userManager.FindByEmailAsync(driverEmail) == null)
            {
                var driverUser = new AppUser { UserName = driverEmail, Email = driverEmail, DisplayName = "Driver 1 Bogdan Capus" };
                var result = await userManager.CreateAsync(driverUser, driverPassword);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(driverUser, "Driver");
                }
            }
        }
    }
}
