using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NetServerLogic.Abstractions;
using NetServerLogic.Connectors;
using NetServerLogic.Connectors.OData;
using NetServerLogic.Metadata;
using NetServerLogic.Options;
using NetServerLogic.Runtime;
using NetServerLogic.Validation;

namespace NetServerLogic.Hosting;

/// <summary>
/// Fluent builder for customizing NetServerLogic services and mock handlers.
/// </summary>
public sealed class ServerLogicEmulatorBuilder
{
    private readonly IServiceCollection _services;

    internal ServerLogicEmulatorBuilder(IServiceCollection services)
    {
        _services = services;
    }

    public IServiceCollection Services => _services;

    /// <summary>
    /// Registers a mock handler for <c>Server.Connector.Dataverse.InvokeCustomApi(method, url, payload)</c>.
    /// </summary>
    /// <param name="methodAndUrl">Key in the form <c>"POST cr123_CalculateDiscount"</c>.</param>
    public ServerLogicEmulatorBuilder AddCustomApiMock(
        string methodAndUrl,
        Func<string?, CancellationToken, string> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodAndUrl);
        ArgumentNullException.ThrowIfNull(handler);

        _services.Configure<ServerLogicEmulatorOptions>(opts =>
        {
            opts.CustomApiMocks[methodAndUrl.Trim()] = handler;
        });

        return this;
    }

    /// <summary>
    /// Registers a synchronous mock handler for <c>Server.Connector.Dataverse.InvokeCustomApi(method, url, payload)</c>.
    /// </summary>
    public ServerLogicEmulatorBuilder AddCustomApiMock(
        string methodAndUrl,
        Func<string?, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddCustomApiMock(methodAndUrl, (payload, _) => handler(payload));
    }
}

/// <summary>
/// Dependency injection registration extensions for NetServerLogic.
/// </summary>
public static class ServerLogicServiceCollectionExtensions
{
    public static IServiceCollection AddPowerPagesServerLogicEmulator(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<ServerLogicEmulatorBuilder>? configureBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ServerLogicEmulatorOptions.SectionName);
        var configToBind = section.Exists() ? section : configuration;

        services.AddOptions<ServerLogicEmulatorOptions>()
            .Bind(configToBind);

        return RegisterCoreServices(services, configureBuilder);
    }

    public static IServiceCollection AddPowerPagesServerLogicEmulator(
        this IServiceCollection services,
        Action<ServerLogicEmulatorOptions> configureOptions,
        Action<ServerLogicEmulatorBuilder>? configureBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddOptions<ServerLogicEmulatorOptions>()
            .Configure(configureOptions);

        return RegisterCoreServices(services, configureBuilder);
    }

    private static IServiceCollection RegisterCoreServices(
        IServiceCollection services,
        Action<ServerLogicEmulatorBuilder>? configureBuilder)
    {
        services.AddHttpClient(ServerLogicHttpClientBridge.HttpClientName);

        services.TryAddSingleton<SimpleODataQueryEvaluator>();
        services.TryAddSingleton<MockDataverseProvider>();
        services.TryAddSingleton<LiveDataverseSdkProvider>();

        services.TryAddSingleton<IServerLogicDataverseProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ServerLogicEmulatorOptions>>().Value;
            return opts.DataverseMode == DataverseMode.Live
                ? sp.GetRequiredService<LiveDataverseSdkProvider>()
                : sp.GetRequiredService<MockDataverseProvider>();
        });

        services.TryAddSingleton<IServerLogicHttpClient, ServerLogicHttpClientBridge>();
        services.TryAddSingleton<IServerLogicCloudFlowProvider, MockCloudFlowProvider>();
        services.TryAddSingleton<IServerLogicScriptValidator, AcornimaScriptValidator>();
        services.TryAddSingleton<IServerLogicRoleProvider, ServerLogicYmlParser>();

        services.TryAddSingleton<ServerLogicFileResolver>();
        services.TryAddSingleton<ServerGlobalFactory>();
        services.TryAddSingleton<ServerLogicExecutionEngine>();

        if (configureBuilder is not null)
        {
            var builder = new ServerLogicEmulatorBuilder(services);
            configureBuilder(builder);
        }

        return services;
    }
}
