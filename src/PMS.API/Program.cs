using FluentValidation;
using FluentValidation.AspNetCore;
using PMS.API;
using PMS.API.Filters;
using PMS.API.Middleware;
using PMS.Application;
using PMS.Application.Models.Validators;
using PMS.DataAccess;
using PMS.DataAccess.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddControllers(
    config => config.Filters.Add(typeof(ValidateModelAttribute))
);

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining(typeof(IValidationsMarker));

builder.Services.AddSwagger();

builder.Services.AddDataAccess(builder.Configuration)
    .AddApplication(builder.Environment)
    .AddVehicleDocuments(builder.Configuration, builder.Environment)
    .AddParkingOperations(builder.Configuration);

builder.Services.AddJwt(builder.Configuration);

builder.Services.AddPublicRateLimits();

builder.Services.AddEmailConfiguration(builder.Configuration);

builder.Services.AddRealtimeAndJobs(builder.Configuration);

var app = builder.Build();

using var scope = app.Services.CreateScope();

await AutomatedMigration.MigrateAsync(scope.ServiceProvider);

var pmsContext = scope.ServiceProvider.GetRequiredService<PmsDbContext>();
await PMS.Application.Helpers.ParkingLotDataSeed.SeedParkingLotsAsync(pmsContext);

app.UseSwagger();
app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "XXIV Parking V1"); });

app.UseHttpsRedirection();

app.UseCors(corsPolicyBuilder =>
    corsPolicyBuilder.AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader()
);

app.UseRouting();

app.UseRateLimiter();

app.UseAuthentication();

app.UseAuthorization();

app.UseMiddleware<PerformanceMiddleware>();

app.UseMiddleware<TransactionMiddleware>();

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.MapControllers();

app.UseRealtimeAndJobs();

app.Run();

namespace PMS.API
{
    public partial class Program { }
}
