using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PMS.Api.IntegrationTests.Helpers.Services;
using PMS.Application.Services;
using PMS.DataAccess.Persistence;

namespace PMS.Api.IntegrationTests.Common;

public class ApiApplicationFactory<TProgram> : WebApplicationFactory<TProgram> where TProgram : class
{
    private readonly string _inMemoryDatabaseName = Guid.NewGuid().ToString().Replace("-", string.Empty);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseTestServer();
        
        builder.ConfigureAppConfiguration((context, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(
                new Dictionary<string, string>
                {
                    ["Database:UseInMemoryDatabase"] = "true",
                    ["JwtConfiguration:SecretKey"] = "tI079UygByXy52J552Xb4odrUjYXjrPBDuK6FhFv6Qa6eD6SZG",
                    ["SmtpSettings:Server"] = "",
                    ["SmtpSettings:Port"] = "548",
                    ["SmtpSettings:SenderName"] = "",
                    ["SmtpSettings:SenderEmail"] = "",
                    ["SmtpSettings:Username"] = "",
                    ["SmtpSettings:Password"] = ""
                });
        });

        builder.ConfigureServices(services =>
        {
            var dbContextDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<DatabaseContext>));

            services.Remove(dbContextDescriptor);

            services.AddDbContext<DatabaseContext>(options =>
            {
                options.UseInMemoryDatabase(_inMemoryDatabaseName);
                options.ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            });

            // Isolate the PMS tables per factory too; a shared in-memory store makes the seed run once per test
            var pmsContextDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PmsDbContext>));

            services.Remove(pmsContextDescriptor);

            services.AddDbContext<PmsDbContext>(options =>
            {
                options.UseInMemoryDatabase($"{_inMemoryDatabaseName}-pms");
                options.ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailService, EmailTestService>();
            services.AddScoped<ITemplateService, TemplateTestService>();
        });
    }
    
    
}
