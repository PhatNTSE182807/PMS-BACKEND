using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using FizzWare.NBuilder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PMS.API;
using PMS.Api.IntegrationTests.Common.Constants;
using PMS.Application.Helpers;
using PMS.DataAccess.Identity;
using PMS.DataAccess.Persistence;

namespace PMS.Api.IntegrationTests.Common;

public static class FactoryExtension
{
    public static async Task<HttpClient> CreateDefaultClientAsync(this ApiApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        
        var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var databaseUser = Builder<ApplicationUser>.CreateNew()
            .With(u => u.EmailConfirmed = true)
            .With(u => u.Email = UserConstants.DefaultUserDb.Email)
            .With(u => u.UserName = UserConstants.DefaultUserDb.Username)
            .With(u => u.Role = "Admin")
            .With(u => u.IsActive = true)
            .Build();
        
        var userFromDb = await userManager.FindByEmailAsync(UserConstants.DefaultUserDb.Email);
        
        if (userFromDb == null)
        {
            await userManager.CreateAsync(databaseUser, UserConstants.DefaultUserDb.Password);
            await context.SaveChangesAsync();
        }
        
        var user = await userManager.FindByEmailAsync(UserConstants.DefaultUserDb.Email);
        
        var token = JwtHelper.GenerateToken(user, factory.Services.GetRequiredService<IConfiguration>());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    public static async Task<HttpClient> CreateClientWithRoleAsync(this ApiApplicationFactory<Program> factory, string role, string customEmail = null)
    {
        var client = factory.CreateClient();

        var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var email = customEmail ?? $"{role.ToLowerInvariant()}_{System.Guid.NewGuid():N}@test.com";
        var username = $"{role.ToLowerInvariant()}_{System.Guid.NewGuid():N}"[..15];

        var user = new ApplicationUser
        {
            EmailConfirmed = true,
            Email = email,
            UserName = username,
            Role = role,
            IsActive = true
        };

        await userManager.CreateAsync(user, "Password.1!");
        await context.SaveChangesAsync();

        var token = JwtHelper.GenerateToken(user, factory.Services.GetRequiredService<IConfiguration>());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    public static HttpClient CreateUnauthorizedClient(this ApiApplicationFactory<Program> factory)
    {
        return factory.CreateClient();
    }
    
    public static T GetRequiredService<T>(this ApiApplicationFactory<Program> factory) where T : notnull
    {
        var scope = factory.Services.CreateScope();
        
        return (T)scope.ServiceProvider.GetRequiredService(typeof(T));
    }
}
