using System;
using System.Net;
using System.Net.Http;
using Jellyfin.Plugin.MeTube.Api;
using Jellyfin.Plugin.MeTube.Jobs;
using Jellyfin.Plugin.MeTube.Library;
using Jellyfin.Plugin.MeTube.MeTube;
using Jellyfin.Plugin.MeTube.Search;
using Jellyfin.Plugin.MeTube.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MeTube;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;

        // Named clients are shared by every plugin in the server, hence the prefix.
        // Cookies are off so the YouTube client sends exactly the consent cookie it
        // sets on each request and nothing YouTube hands back accumulates.
        serviceCollection.AddHttpClient(MeTubeClient.HttpClientName);
        serviceCollection.AddHttpClient(InnerTubeClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });

        // Startup filters are resolved from the root provider, so these must be
        // singletons (a scoped registration throws under scope validation).
        serviceCollection.AddSingleton<WebInjectionStatus>();
        serviceCollection.AddSingleton<IStartupFilter, WebInjectionStartupFilter>();

        serviceCollection.AddSingleton<YouTubeGuard>();
        serviceCollection.AddSingleton<InnerTubeClient>();
        serviceCollection.AddSingleton<MeTubeClient>();
        serviceCollection.AddSingleton<VideoInfoService>();
        serviceCollection.AddSingleton<SearchService>();
        serviceCollection.AddSingleton<LibraryLocator>();
        serviceCollection.AddSingleton<LibraryIndex>();
        serviceCollection.AddSingleton<LibraryRefresher>();
        serviceCollection.AddSingleton<JobStore>();
        serviceCollection.AddSingleton<UserAccess>();
        serviceCollection.AddSingleton<DownloadWatcher>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<DownloadWatcher>());
        serviceCollection.AddSingleton<JobService>();
    }
}
