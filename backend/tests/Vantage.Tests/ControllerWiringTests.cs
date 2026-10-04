using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vantage.Api.Auth;
using Vantage.Infrastructure;
using Vantage.Infrastructure.Services;

namespace Vantage.Tests;

/// <summary>Catches a service that was added to a controller but never registered, which otherwise shows up as a 500 in the browser.</summary>
public class ControllerWiringTests
{
    [Fact]
    public void Every_controller_dependency_is_registered()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=none;Database=none;",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddInfrastructure(config);
        services.AddScoped<CurrentUser>();
        services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<CurrentUser>());
        using var provider = services.BuildServiceProvider();
        var isService = provider.GetRequiredService<IServiceProviderIsService>();

        var missing = typeof(CurrentUser).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => (Controller: t.Name, Dependency: p.ParameterType)))
            .Where(x => x.Dependency != typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment)) // supplied by the web host itself
            .Where(x => !isService.IsService(x.Dependency))
            .Select(x => $"{x.Controller} needs {x.Dependency.Name}")
            .ToList();

        Assert.Empty(missing);
    }
}
