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
    
    public static T GetRequiredService<T>(this ApiApplicationFactory<Program> factory) where T : notnull
    {
        var scope = factory.Services.CreateScope();
        
        return (T)scope.ServiceProvider.GetRequiredService(typeof(T));
    }
}
