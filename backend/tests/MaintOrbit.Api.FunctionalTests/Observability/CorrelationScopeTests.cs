using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Telemetry.Logging;
using MaintOrbit.Shared.Abstractions;
using MaintOrbit.Shared.Primitives;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MaintOrbit.Api.FunctionalTests.Observability;

/// <summary>
/// Covers correlation propagation through the ambient accessor and into the logging scope.
/// </summary>
/// <remarks>
/// LG-4 requires the correlation identifier in every log entry, and NFR-OBS-002 requires it
/// to survive every subsystem boundary. Both fail silently — the logs simply come out
/// unlinked, with nothing raising an error — so they are asserted here rather than trusted.
/// </remarks>
public sealed class CorrelationScopeTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Application:Name"] = "MaintOrbit AI",
                ["Application:PublicBaseUrl"] = "https://api.example.test",
                ["Cors:AllowCredentials"] = "true",
                ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                ["Persistence:ConnectionString"] = "Host=localhost;Database=maintorbit_test;Username=maintorbit"
            })
            .Build();

        var services = new ServiceCollection();
        services
            .AddApplication()
            .AddInfrastructure(configuration)
            .AddApi(configuration)
            .AddObservability(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    [Fact]
    public void CorrelationAccessor_IsResolvableFromTheCompositionRoot()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<ICorrelationIdAccessor>());
    }

    [Fact]
    public void CorrelationAccessor_IsSingletonAcrossScopes()
    {
        // The accessor must be injectable into singletons — logging components are — and the
        // Worker has no request scope at all. A scoped registration would break both.
        using var provider = BuildProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<ICorrelationIdAccessor>(),
            second.ServiceProvider.GetRequiredService<ICorrelationIdAccessor>());
    }

    [Fact]
    public void Current_IsNull_OutsideACorrelatedOperation()
    {
        using var provider = BuildProvider();

        Assert.Null(provider.GetRequiredService<ICorrelationIdAccessor>().Current);
    }

    [Fact]
    public void BeginCorrelationScope_MakesTheIdentifierAmbient()
    {
        using var provider = BuildProvider();
        var accessor = provider.GetRequiredService<ICorrelationIdAccessor>();
        var correlationId = CorrelationId.New();

        using (accessor.BeginCorrelationScope(correlationId))
        {
            Assert.Equal(correlationId, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task CorrelationIdentifier_SurvivesAnAwait()
    {
        // The reason for AsyncLocal. If the identifier did not flow across continuations,
        // everything logged after the first await in a request would be uncorrelated — which
        // is most of what a request does.
        using var provider = BuildProvider();
        var accessor = provider.GetRequiredService<ICorrelationIdAccessor>();
        var correlationId = CorrelationId.New();

        using (accessor.BeginCorrelationScope(correlationId))
        {
            await Task.Yield();
            await Task.Run(static () => { }).ConfigureAwait(true);

            Assert.Equal(correlationId, accessor.Current);
        }
    }

    [Fact]
    public void NestedScope_RestoresTheOuterIdentifier()
    {
        // A Worker job spawned inside a request nests. Clearing on dispose instead of
        // restoring would drop the outer identifier at exactly the point correlation is
        // supposed to keep track of it.
        using var provider = BuildProvider();
        var accessor = provider.GetRequiredService<ICorrelationIdAccessor>();
        var outer = CorrelationId.New();
        var inner = CorrelationId.New();

        using (accessor.BeginCorrelationScope(outer))
        {
            using (accessor.BeginCorrelationScope(inner))
            {
                Assert.Equal(inner, accessor.Current);
            }

            Assert.Equal(outer, accessor.Current);
        }
    }

    [Fact]
    public void BeginCorrelationScope_WritesTheIdentifierAsAStructuredLogProperty()
    {
        // LG-1: the identifier must be its own field, not text inside a message. A structured
        // formatter can only emit it as a field if the scope state is key/value pairs.
        var recorder = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(recorder));
        var logger = factory.CreateLogger("test");
        var correlationId = CorrelationId.New();

        using (logger.BeginCorrelationScope(correlationId))
        {
            logger.LogInformation("Operation completed.");
        }

        var entry = Assert.Single(recorder.Entries);
        var scope = Assert.Single(entry.Scopes);
        var property = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object>>>(scope);

        Assert.Contains(
            property,
            pair => pair.Key == CorrelationLoggerExtensions.CorrelationIdPropertyName
                    && (string)pair.Value == correlationId);
    }

    [Fact]
    public void BeginCorrelationScope_FromTheAccessor_OpensNoScopeOutsideAnOperation()
    {
        // Startup and host-level activity have no originating request. Fabricating an
        // identifier for them would produce entries that appear to belong to a request that
        // never existed.
        using var provider = BuildProvider();
        var accessor = provider.GetRequiredService<ICorrelationIdAccessor>();

        var recorder = new RecordingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(recorder));
        var logger = factory.CreateLogger("test");

        Assert.Null(logger.BeginCorrelationScope(accessor));
    }

    /// <summary>
    /// Captures log entries together with the scopes that were open when they were written.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopeProvider;

        public List<(string Message, List<object?> Scopes)> Entries { get; } = [];

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
            _scopeProvider = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                provider._scopeProvider?.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scopes = new List<object?>();
                provider._scopeProvider?.ForEachScope(
                    static (scope, collected) => collected.Add(scope), scopes);

                provider.Entries.Add((formatter(state, exception), scopes));
            }
        }
    }
}
