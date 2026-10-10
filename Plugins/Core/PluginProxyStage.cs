namespace LyWaf.Plugins.Core;

/// <summary>Host-defined execution positions, independent of plugin business logic.</summary>
public enum PluginProxyStage
{
    RequestProtection,
    ResponseMonitoring,
    /// <summary>Runs before all other proxy middleware, including request body readers.</summary>
    First
}

/// <summary>Allows a plugin to supply middleware at a stable proxy execution position.</summary>
public interface IPluginProxyStages
{
    void ConfigureProxyStage(IApplicationBuilder app, PluginProxyStage stage);
}

public static class PluginProxyStages
{
    public static IApplicationBuilder UseLyWafPluginStage(this IApplicationBuilder app, PluginProxyStage stage)
    {
        var manager = app.ApplicationServices.GetRequiredService<PluginManager>();
        foreach (var plugin in manager.GetAllPlugins().OrderBy(p => p.Metadata.Priority))
        {
            if (plugin is IPluginProxyStages stages)
                UseWhenActive(app, () => plugin.State, branch => stages.ConfigureProxyStage(branch, stage));
        }
        return app;
    }

    /// <summary>Build once, but evaluate lifecycle state for every request, including late activation.</summary>
    public static void UseWhenActive(IApplicationBuilder app, Func<PluginState> state, Action<IApplicationBuilder> configure)
    {
        app.Use(next =>
        {
            var branch = app.New();
            configure(branch);
            branch.Run(next);
            var pipeline = branch.Build();
            return context => state() == PluginState.Running ? pipeline(context) : next(context);
        });
    }
}
