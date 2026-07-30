using MaintOrbit.Shared.Primitives;

namespace MaintOrbit.Api.FunctionalTests.Observability;

/// <summary>
/// Covers the admission rules for correlation identifiers.
/// </summary>
/// <remarks>
/// The API specification permits a client to supply its own identifier, which makes this
/// untrusted input that is written into every log entry for the request. These tests pin
/// both halves of the contract: a usable value is reused, and anything else is quietly
/// replaced rather than trusted or rejected.
/// </remarks>
public sealed class CorrelationIdTests
{
    [Fact]
    public void Resolve_GeneratesAnIdentifier_WhenTheCallerSuppliesNone()
    {
        var correlationId = CorrelationId.Resolve(null);

        Assert.True(CorrelationId.IsWellFormed(correlationId));
    }

    [Fact]
    public void Resolve_ReusesTheIdentifier_WhenTheCallerSuppliesOne()
    {
        // The point of accepting a client-supplied value: the customer's own logs and ours
        // share a key, so a support conversation can start from their identifier.
        const string Supplied = "client-supplied-0123456789";

        Assert.Equal(Supplied, CorrelationId.Resolve(Supplied));
    }

    [Fact]
    public void New_ProducesDistinctIdentifiers()
    {
        var identifiers = Enumerable.Range(0, 1_000).Select(_ => CorrelationId.New()).ToList();

        Assert.Equal(identifiers.Count, identifiers.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("forged\ninjected-log-line")]
    [InlineData("carriage\rreturn")]
    [InlineData("tab\tseparated")]
    public void Resolve_ReplacesIdentifiersCarryingLineBreaks(string hostile)
    {
        // A newline in a value written to a line-oriented log lets a caller forge log
        // entries. Replacement — not rejection — because correlation is a diagnostic aid and
        // failing the customer's request over a bad diagnostic header would be the wrong
        // trade.
        var resolved = CorrelationId.Resolve(hostile);

        Assert.NotEqual(hostile, resolved);
        Assert.True(CorrelationId.IsWellFormed(resolved));
    }

    [Fact]
    public void Resolve_ReplacesIdentifiersExceedingTheLengthCeiling()
    {
        // Unbounded input is cheap to send and expensive to absorb: it is copied into every
        // log entry for the request, then stored, indexed, and shipped.
        var oversized = new string('a', CorrelationId.MaxLength + 1);

        Assert.NotEqual(oversized, CorrelationId.Resolve(oversized));
    }

    [Fact]
    public void IsWellFormed_AcceptsIdentifiersAtExactlyTheLengthCeiling()
    {
        // Guards the boundary in the other direction, so a future tightening of MaxLength
        // cannot turn the limit into an off-by-one rejection.
        Assert.True(CorrelationId.IsWellFormed(new string('a', CorrelationId.MaxLength)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsWellFormed_RejectsAbsentIdentifiers(string? absent)
    {
        Assert.False(CorrelationId.IsWellFormed(absent));
    }
}
