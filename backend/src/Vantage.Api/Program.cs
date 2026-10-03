using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Vantage.Api.Auth;
using Vantage.Infrastructure;
using Vantage.Infrastructure.Data;
using Vantage.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

// Enums travel as names ("Active", "Edit", "Pass"), both ways.
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<CurrentUser>());

if (builder.Environment.IsDevelopment())
{
    // Local build: the portals' dev sign-in picker stands in for Cognito and ADFS.
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
}
else
{
    // Cognito (with ADFS federation for @rrd.com) is wired in a later step.
    throw new InvalidOperationException("Only the Development environment is configured in this build.");
}
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    await PrepareDatabaseAsync(app);
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/health", async (AppDbContext db, CancellationToken ct) =>
    Results.Ok(new { status = "ok", database = await db.Database.CanConnectAsync(ct) ? "ok" : "unreachable" }));

app.Run();

static async Task PrepareDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var config = app.Configuration;
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    if (config.GetValue("Database:MigrateOnStartup", true))
    {
        log.LogInformation("Applying database migrations...");
        await db.Database.MigrateAsync();
    }

    var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
    await seeder.SeedReferenceDataAsync();
    await seeder.SeedBootstrapSuperAdminAsync(config["Bootstrap:SuperAdminEmail"] ?? "nimal.perera@rrd.com");
}

public partial class Program;
