using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using UltraBlur.Plugin.ScheduledTasks;
using UltraBlur.Plugin.Services;

namespace UltraBlur.Plugin
{
    /// <summary>
    /// Registers the UltraBlur plugin services with Jellyfin's DI container.
    /// </summary>
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<ColorStore>();
            serviceCollection.AddSingleton<UltraBlurService>();
            serviceCollection.AddSingleton<UltraBlurWarmupTask>();

            // Request-time script injection into jellyfin-web's index.html
            // (pattern from Jellyfin-Enhanced). Kill-switchable via config.
            serviceCollection.AddSingleton<IStartupFilter, ScriptInjectionStartupFilter>();
        }
    }
}
