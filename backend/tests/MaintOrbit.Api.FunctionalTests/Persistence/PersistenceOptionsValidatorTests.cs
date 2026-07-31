using MaintOrbit.Infrastructure.Persistence;

namespace MaintOrbit.Api.FunctionalTests.Persistence;

/// <summary>
/// Covers database settings validation.
/// </summary>
/// <remarks>
/// The application opens no connection at startup, so a bad connection string would otherwise
/// surface on the first request that touches the database — long after deployment, and to a
/// customer rather than to an operator.
/// </remarks>
public sealed class PersistenceOptionsValidatorTests
{
    private static readonly PersistenceOptionsValidator Validator = new();

    private const string Valid = "Host=localhost;Port=5432;Database=maintorbit;Username=maintorbit";

    [Fact]
    public void WellFormedConnectionString_IsAccepted()
    {
        var result = Validator.Validate(null, new PersistenceOptions { ConnectionString = Valid });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void MalformedConnectionString_IsRejected()
    {
        var result = Validator.Validate(null, new PersistenceOptions
        {
            ConnectionString = "this is not a connection string"
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void ConnectionStringWithoutHost_IsRejected()
    {
        var result = Validator.Validate(null, new PersistenceOptions
        {
            ConnectionString = "Database=maintorbit;Username=maintorbit"
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void ConnectionStringWithoutDatabase_IsRejected()
    {
        var result = Validator.Validate(null, new PersistenceOptions
        {
            ConnectionString = "Host=localhost;Username=maintorbit"
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Multiplexing_IsRejected()
    {
        // Multiplexing interleaves commands from different operations over one physical
        // connection, so per-connection session state stops being per-operation. The tenant
        // variable that row-level security reads is session state, and every pooling mode §6.7
        // assesses as viable assumes it holds for the duration of an operation.
        var result = Validator.Validate(null, new PersistenceOptions
        {
            ConnectionString = $"{Valid};Multiplexing=true"
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void ValidationFailure_DoesNotEchoTheConnectionString()
    {
        // The string carries the database password and a validation failure is written to the
        // log (LG-2, NFR-OBS-009).
        var result = Validator.Validate(null, new PersistenceOptions
        {
            ConnectionString = "Host=localhost;Database=maintorbit;Password=hunter2;Multiplexing=true"
        });

        Assert.True(result.Failed);
        Assert.DoesNotContain(
            "hunter2",
            string.Join(' ', result.Failures ?? []),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyConnectionString_ProducesNoParseFailure()
    {
        // DataAnnotations already reports the empty case. Reporting it twice would present one
        // mistake as two.
        var result = Validator.Validate(null, new PersistenceOptions { ConnectionString = "" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void RetryIsDisabledByDefault()
    {
        // A retried operation can land on a different connection, which under session-scoped
        // row-level security means running without tenant context. Off until DD-2 is settled.
        Assert.Equal(0, new PersistenceOptions().MaxRetryAttempts);
    }
}
