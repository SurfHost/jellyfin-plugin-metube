using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.MeTube.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MeTube.Tests;

/// <summary>
/// Another plugin that answers index.html itself (GuestPass up to 0.2.x) hid the
/// MeTube menu entry on a real server: its startup filter was registered first, so
/// it was the outer middleware and never called the rest of the pipeline.
/// </summary>
public class InjectionOrderTests
{
    private const string OtherPage = "<html><body><script src=\"/GuestPass/ClientScript\" defer></script></body></html>";

    [Fact]
    public void RegistersItsStartupFilterFirst()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStartupFilter, ShortCircuitInjector>();

        new PluginServiceRegistrator().RegisterServices(services, null!);

        var first = services.First(d => d.ServiceType == typeof(IStartupFilter));
        Assert.Equal(typeof(WebInjectionStartupFilter), first.ImplementationType);
    }

    [Fact]
    public async Task WrapsAnInjectorThatAnswersTheRequestItself()
    {
        // Registration order as produced by RegisterServices: MeTube first, the
        // short-circuiting injector after it.
        IStartupFilter[] filters =
        {
            new WebInjectionStartupFilter(null!, new WebInjectionStatus(), NullLogger<WebInjectionStartupFilter>.Instance),
            new ShortCircuitInjector()
        };

        var body = await ServeAsync(filters, "/web/");

        Assert.Contains("/GuestPass/ClientScript", body, StringComparison.Ordinal);
        Assert.Contains("/MeTube/ClientScript", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavesOtherRequestsAlone()
    {
        IStartupFilter[] filters =
        {
            new WebInjectionStartupFilter(null!, new WebInjectionStatus(), NullLogger<WebInjectionStartupFilter>.Instance),
            new ShortCircuitInjector()
        };

        var body = await ServeAsync(filters, "/web/main.js");

        Assert.Equal("next", body);
    }

    /// <summary>Composes startup filters exactly like the ASP.NET Core host does and serves one request.</summary>
    private static async Task<string> ServeAsync(IStartupFilter[] filters, string path)
    {
        Action<IApplicationBuilder> configure = app => app.Run(context => context.Response.WriteAsync("next"));
        foreach (var filter in filters.Reverse())
        {
            configure = filter.Configure(configure);
        }

        var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        configure(builder);
        var pipeline = builder.Build();

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        var output = new MemoryStream();
        context.Response.Body = output;
        await pipeline(context);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Behaves like GuestPass 0.2.x: writes index.html itself and never calls next.</summary>
    private sealed class ShortCircuitInjector : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path == "/web/")
                {
                    var bytes = Encoding.UTF8.GetBytes(OtherPage);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength = bytes.Length;
                    await context.Response.Body.WriteAsync(bytes);
                    return;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
