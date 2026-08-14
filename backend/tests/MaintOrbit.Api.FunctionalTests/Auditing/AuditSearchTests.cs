using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MaintOrbit.Api.Endpoints;
using MaintOrbit.Api.Extensions;
using MaintOrbit.Application.Abstractions.Messaging;
using MaintOrbit.Application.Common.Authorization;
using MaintOrbit.Application.DependencyInjection;
using MaintOrbit.Application.Modules.Identity.Commands.AcceptInvitation;
using MaintOrbit.Domain.Modules.Identity.Entities;
using MaintOrbit.Domain.Modules.Identity.Enums;
using MaintOrbit.Domain.Modules.Identity.ValueObjects;
using MaintOrbit.Infrastructure.DependencyInjection;
using MaintOrbit.Infrastructure.Persistence;
using MaintOrbit.Shared.Constants;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace MaintOrbit.Api.FunctionalTests.Auditing;

/// <summary>
/// Covers <c>/api/v1/audit-events</c> — search, export, and their isolation.
/// </summary>
/// <remarks>
/// <b>Two Companies, both real, driven through real HTTP.</b> Tenant isolation on a read surface
/// cannot be asserted with one tenant: a query returning "only A's rows" is indistinguishable from
/// a query returning "all rows" until a second Company's data exists to be wrongly included.
/// <para>
/// The API's connection is the developer's account, which locally is a superuser and therefore
/// bypasses row-level security. That would make an isolation test meaningless, so the host here
/// runs as an unprivileged role built for the purpose — the same reason
/// <c>AuditStoreSchemaTests</c> does it.
/// </para>
/// </remarks>
public sealed class AuditSearchTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string AuditorEmail = "auditor@example.test";
    private const string PlainEmail = "plain@example.test";
    private const string AuditorRole = "auditor";
    /// <summary>
    /// A role name unique to this run.
    /// </summary>
    /// <remarks>
    /// A fixed name was the proximate cause of this suite passing vacuously: an aborted run left
    /// the role owning a database, the next run could not drop it, and the failure was swallowed
    /// into a skip. A unique name means one run's wreckage cannot silence the next.
    /// </remarks>
    /// <remarks>
    /// Per instance, not static. xUnit constructs the class once per test, so a shared name would
    /// have every test after the first fail to create it — the same class of setup failure that
    /// hid this suite in the first place.
    /// </remarks>
    private readonly string _role = $"mo_audit_read_{Guid.CreateVersion7():n}"[..40];

    /// <summary>
    /// The role authentication runs as.
    /// </summary>
    /// <remarks>
    /// <b>Two roles, because the documented deployment has two.</b> Ordinary reads run as
    /// <c>_role</c> — <c>NOSUPERUSER NOBYPASSRLS</c> — so row-level security is what isolates audit
    /// queries, and a test asserting isolation asserts something.
    /// <para>
    /// Authentication cannot run as that role. `ElevatedCredentialDirectory` resolves an Employee
    /// by email <i>before</i> a tenant is known (<c>04-tenant-security</c> §3.4 path 13), which is
    /// a cross-Company read by necessity — under <c>NOBYPASSRLS</c> it returns nothing and every
    /// sign-in fails with 401. `Persistence:ElevatedConnectionString` exists for exactly this, and
    /// a deployment points it at a role permitted to bypass. Leaving it unset makes it fall back to
    /// the ordinary connection, which is how this suite first failed.
    /// </para>
    /// </remarks>
    private readonly string _elevatedRole = $"mo_audit_elev_{Guid.CreateVersion7():n}"[..40];
    private const string RolePassword = "audit-read";

    private readonly CompanyId _companyA = new(Guid.CreateVersion7());
    private readonly CompanyId _companyB = new(Guid.CreateVersion7());

    private IHost? _host;
    private string? _skip;
    private string? _database;
    private string _unprivileged = string.Empty;
    private EmployeeId _auditorId;
    private EmployeeId _plainId;
    private string _auditorToken = string.Empty;
    private string _plainToken = string.Empty;

    public async Task InitializeAsync()
    {
        var owner = await TestDatabase.CreateAsync().ConfigureAwait(false);

        if (owner is null)
        {
            _skip = "No PostgreSQL reachable.";
            return;
        }

        _database = owner;
        var databaseName = new NpgsqlConnectionStringBuilder(owner).Database!;

        try
        {
            await AdministerAsync(
                $"""
                 DROP ROLE IF EXISTS {_role};
                 CREATE ROLE {_role} LOGIN PASSWORD '{RolePassword}'
                     NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
                 CREATE ROLE {_elevatedRole} LOGIN PASSWORD '{RolePassword}'
                     NOSUPERUSER BYPASSRLS NOCREATEDB NOCREATEROLE;
                 """).ConfigureAwait(false);
        }
        catch (NpgsqlException error)
        {
            _skip = $"Cannot create a test role: {error.Message}";
            return;
        }

        _unprivileged =
            $"Host=localhost;Port=5432;Database={databaseName};" +
            $"Username={_role};Password={RolePassword}";

        await ExecuteAsync(owner, $"ALTER DATABASE {databaseName} OWNER TO {_role};")
            .ConfigureAwait(false);

        var builder = new DbContextOptionsBuilder<MaintOrbitDbContext>();
        NpgsqlConfiguration.Apply(builder, new PersistenceOptions { ConnectionString = _unprivileged });

        await using (var context = new MaintOrbitDbContext(builder.Options))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        var elevated =
            $"Host=localhost;Port=5432;Database={databaseName};" +
            $"Username={_elevatedRole};Password={RolePassword}";

        await ExecuteAsync(owner, $"GRANT ALL ON SCHEMA identity TO {_elevatedRole};")
            .ConfigureAwait(false);
        await ExecuteAsync(owner,
            $"GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA identity TO {_elevatedRole};")
            .ConfigureAwait(false);

        _host = BuildHost(_unprivileged, elevated);
        _host.Start();

        await SeedAsync().ConfigureAwait(false);

        _auditorToken = await SignInAsync(AuditorEmail).ConfigureAwait(false);
        _plainToken = await SignInAsync(PlainEmail).ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        _host?.Dispose();
        await TestDatabase.DropAsync(_database).ConfigureAwait(false);

        if (_skip is null)
        {
            await AdministerAsync(
                $"DROP ROLE IF EXISTS {_role}; DROP ROLE IF EXISTS {_elevatedRole};")
                .ConfigureAwait(false);
        }
    }

    private bool Unavailable() => _skip is not null;

    /// <summary>
    /// Fails if the suite is skipping for any reason other than an absent server.
    /// </summary>
    /// <remarks>
    /// The previous version asserted <c>_skip is null || _skip.Length > 0</c>, which is true
    /// however setup went — a guard that could not fail, guarding the thing that did. Skipping is
    /// now only possible when there is no server at all, and this says so out loud.
    /// </remarks>
    [Fact]
    public void TheSuiteIsNotSkippingForAnAvoidableReason()
    {
        Assert.True(
            _skip is null || _skip.Contains("No PostgreSQL", StringComparison.Ordinal),
            $"Skipped for an avoidable reason: {_skip}");
    }

    // ---- Search --------------------------------------------------------------------------------

    [Fact]
    public async Task AnAuditorSeesTheirOwnCompanysEvents()
    {
        if (Unavailable()) { return; }

        var page = await SearchAsync(_auditorToken, "");

        Assert.NotEmpty(page.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task AnotherCompanysEventsAreInvisible()
    {
        // The test that matters. Company B's events exist in the same table and the same
        // partitions; row-level security is the only thing keeping them out of this response.
        if (Unavailable()) { return; }

        await InsertEventAsync(_companyB.Value, "authentication.sign-in", "b-correlation");

        var page = await SearchAsync(_auditorToken, "");

        var correlations = page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("correlationId").GetString())
            .ToList();

        Assert.DoesNotContain("b-correlation", correlations);
    }

    [Fact]
    public async Task ACompanyCannotFilterItsWayToAnotherCompanysEvents()
    {
        // Filtering by a value only the other Company's rows carry still returns nothing — the
        // filter narrows within the tenant, it cannot widen beyond it.
        if (Unavailable()) { return; }

        await InsertEventAsync(_companyB.Value, "authentication.sign-in", "b-only-correlation");

        var page = await SearchAsync(_auditorToken, "&correlationId=b-only-correlation");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task TheResponseCarriesNoTotalCount()
    {
        // §4.4 removes totalCount from audit collections. It also happens to close a side channel:
        // a total would let a caller probe for rows they cannot read.
        if (Unavailable()) { return; }

        var page = await SearchAsync(_auditorToken, "");

        Assert.False(page.TryGetProperty("totalCount", out _));
    }

    [Fact]
    public async Task FilteringByActionWorks()
    {
        if (Unavailable()) { return; }

        var page = await SearchAsync(_auditorToken, "&action=authentication.sign-in");

        Assert.All(
            page.GetProperty("items").EnumerateArray(),
            item => Assert.Equal("authentication.sign-in", item.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task FilteringByOutcomeWorks()
    {
        if (Unavailable()) { return; }

        var page = await SearchAsync(_auditorToken, "&outcome=Success");

        Assert.All(
            page.GetProperty("items").EnumerateArray(),
            item => Assert.Equal("Success", item.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task FilteringByActorWorks()
    {
        if (Unavailable()) { return; }

        var page = await SearchAsync(
            _auditorToken, $"&actorEmployeeId={_auditorId.Value}");

        Assert.All(
            page.GetProperty("items").EnumerateArray(),
            item => Assert.Equal(
                _auditorId.Value.ToString("n"),
                item.GetProperty("actorEmployeeId").GetString()));
    }

    [Fact]
    public async Task CombinedFiltersWork()
    {
        if (Unavailable()) { return; }

        var page = await SearchAsync(
            _auditorToken, "&action=authentication.sign-in&outcome=Success&targetType=session");

        Assert.All(page.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("authentication.sign-in", item.GetProperty("action").GetString());
            Assert.Equal("Success", item.GetProperty("outcome").GetString());
        });
    }

    [Fact]
    public async Task AnUnknownOutcomeIsRejected()
    {
        // Rather than silently matching nothing, which reads as "no events happened".
        if (Unavailable()) { return; }

        using var response = await SendAsync(_auditorToken, Url("&outcome=Sideways"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Pagination ----------------------------------------------------------------------------

    [Fact]
    public async Task OrderingIsNewestFirstAndDeterministic()
    {
        if (Unavailable()) { return; }

        var page = await SearchAsync(_auditorToken, "&pageSize=200");

        var stamps = page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("occurredAtUtc").GetDateTimeOffset())
            .ToList();

        Assert.Equal(stamps.OrderByDescending(s => s), stamps);
    }

    [Fact]
    public async Task PagingReachesEveryEventExactlyOnce()
    {
        // A keyset written over different columns from the ORDER BY compiles, returns
        // plausible-looking pages, and silently skips rows. Paging one at a time across a known
        // set and comparing identifiers is what catches that; a page count would not.
        if (Unavailable()) { return; }

        // A action nothing else in this class writes. The tests share one database, so a filter
        // any other test also inserts under makes the expected count depend on execution order —
        // which is how this test first passed against a deliberately broken keyset.
        const string PagingAction = "paging.probe";

        for (var i = 0; i < 6; i++)
        {
            await InsertEventAsync(_companyA.Value, PagingAction, $"page-{i}");
        }

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var page = await SearchAsync(
                _auditorToken,
                $"&action={PagingAction}&pageSize=2" +
                (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"));

            seen.AddRange(page.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!));

            cursor = page.TryGetProperty("nextCursor", out var next) && next.ValueKind is JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (cursor is not null);

        // Exactly six, each exactly once. Both halves matter: a keyset using <= instead of <
        // repeats the boundary row (count grows, distinct stays 6), while a keyset over the wrong
        // columns skips rows (count shrinks). Asserting only a page count would catch neither.
        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task TheSecondPageStartsAfterTheFirstPagesLastRow()
    {
        // The decisive keyset test, and the one an aggregate count cannot replace.
        //
        // Rows are given explicit, distinct timestamps so the boundary is deterministic, and the
        // page size is one so page two consists of exactly the row that must follow. A keyset
        // written with <= instead of < repeats the boundary row here and nowhere else obvious;
        // a keyset over the wrong columns skips it.
        if (Unavailable()) { return; }

        const string BoundaryAction = "boundary.probe";
        var baseTime = DateTimeOffset.UtcNow.AddDays(-3);

        for (var i = 0; i < 3; i++)
        {
            await InsertAtAsync(_companyA.Value, BoundaryAction, $"boundary-{i}",
                baseTime.AddMinutes(i));
        }

        var first = await SearchAsync(_auditorToken, $"&action={BoundaryAction}&pageSize=1");
        var firstId = first.GetProperty("items")[0].GetProperty("id").GetString();
        var cursor = first.GetProperty("nextCursor").GetString()!;

        var second = await SearchAsync(
            _auditorToken,
            $"&action={BoundaryAction}&pageSize=1&cursor={Uri.EscapeDataString(cursor)}");

        var secondId = second.GetProperty("items")[0].GetProperty("id").GetString();

        Assert.NotEqual(firstId, secondId);

        // Newest first, so page one is boundary-2 and page two is boundary-1.
        Assert.Equal("boundary-2", first.GetProperty("items")[0].GetProperty("correlationId").GetString());
        Assert.Equal("boundary-1", second.GetProperty("items")[0].GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task SixEventsPageAsThreeThenThree_WithNoOverlap()
    {
        // The brief's shape: page one A/B/C, page two D/E/F, nothing repeated, nothing skipped.
        // Distinct timestamps a minute apart, so an ordering or predicate error cannot coincide
        // with the right answer.
        if (Unavailable()) { return; }

        const string Probe = "sixpage.probe";
        var baseTime = DateTimeOffset.UtcNow.AddDays(-8);

        for (var i = 0; i < 6; i++)
        {
            await InsertAtAsync(_companyA.Value, Probe, $"six-{i}", baseTime.AddMinutes(i));
        }

        var first = await SearchAsync(_auditorToken, $"&action={Probe}&pageSize=3");
        var firstCorr = Correlations(first);

        // Newest first.
        Assert.Equal(["six-5", "six-4", "six-3"], firstCorr);
        Assert.True(first.GetProperty("hasMore").GetBoolean());

        var cursor = first.GetProperty("nextCursor").GetString()!;

        var second = await SearchAsync(
            _auditorToken, $"&action={Probe}&pageSize=3&cursor={Uri.EscapeDataString(cursor)}");
        var secondCorr = Correlations(second);

        Assert.Equal(["six-2", "six-1", "six-0"], secondCorr);
        Assert.False(second.GetProperty("hasMore").GetBoolean());

        // No overlap, and everything exactly once.
        Assert.Empty(firstCorr.Intersect(secondCorr, StringComparer.Ordinal));
        Assert.Equal(6, firstCorr.Concat(secondCorr).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task EventsSharingATimestamp_ArePagedByTheIdTieBreak()
    {
        // The second half of the composite predicate, which nothing else exercises. All three rows
        // share one instant, so paging can only work if `id < afterId` is applied — and only if the
        // ordering agrees with it.
        //
        // This is not a contrived case: one request emits several audit events within the same
        // microsecond, so a broken tie-break loses real records.
        if (Unavailable()) { return; }

        const string Probe = "tiebreak.probe";
        var instant = DateTimeOffset.UtcNow.AddDays(-9);

        // Ids chosen so their ordering is unambiguous and independent of insert order.
        var id1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var id2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var id3 = Guid.Parse("33333333-3333-3333-3333-333333333333");

        await InsertAtWithIdAsync(_companyA.Value, Probe, "tie-1", instant, id1);
        await InsertAtWithIdAsync(_companyA.Value, Probe, "tie-3", instant, id3);
        await InsertAtWithIdAsync(_companyA.Value, Probe, "tie-2", instant, id2);

        var first = await SearchAsync(_auditorToken, $"&action={Probe}&pageSize=2");

        // id DESC within the shared timestamp.
        Assert.Equal(["tie-3", "tie-2"], Correlations(first));

        var cursor = first.GetProperty("nextCursor").GetString()!;

        var second = await SearchAsync(
            _auditorToken, $"&action={Probe}&pageSize=2&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(["tie-1"], Correlations(second));
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task ADeepPage_IsReachedWithoutRepeatingOrSkipping()
    {
        // Not just the first two pages. Twenty rows at size two is ten pages, so a predicate that
        // drifts by one row per page fails here even when the first boundary happens to be right.
        if (Unavailable()) { return; }

        const string Probe = "deep.probe";
        var baseTime = DateTimeOffset.UtcNow.AddDays(-10);

        for (var i = 0; i < 20; i++)
        {
            await InsertAtAsync(_companyA.Value, Probe, $"deep-{i:00}", baseTime.AddMinutes(i));
        }

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await SearchAsync(
                _auditorToken,
                $"&action={Probe}&pageSize=2" +
                (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"));

            seen.AddRange(Correlations(page));
            cursor = page.GetProperty("hasMore").GetBoolean()
                ? page.GetProperty("nextCursor").GetString()
                : null;

            pages++;
        }
        while (cursor is not null && pages < 50);

        Assert.Equal(10, pages);
        Assert.Equal(20, seen.Count);
        Assert.Equal(20, seen.Distinct(StringComparer.Ordinal).Count());

        // Strictly descending across every page boundary.
        Assert.Equal(seen.OrderByDescending(c => c, StringComparer.Ordinal), seen);
    }

    [Fact]
    public async Task RepeatingTheSecondPageRequest_ReturnsTheSameRows()
    {
        if (Unavailable()) { return; }

        const string Probe = "stable.probe";
        var baseTime = DateTimeOffset.UtcNow.AddDays(-11);

        for (var i = 0; i < 4; i++)
        {
            await InsertAtAsync(_companyA.Value, Probe, $"stable-{i}", baseTime.AddMinutes(i));
        }

        var first = await SearchAsync(_auditorToken, $"&action={Probe}&pageSize=2");
        var cursor = Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!);

        var a = await SearchAsync(_auditorToken, $"&action={Probe}&pageSize=2&cursor={cursor}");
        var b = await SearchAsync(_auditorToken, $"&action={Probe}&pageSize=2&cursor={cursor}");

        Assert.Equal(Correlations(a), Correlations(b));
    }

    [Fact]
    public async Task ATamperedCursorIsRejected()
    {
        if (Unavailable()) { return; }

        const string Probe = "tamper.probe";
        var baseTime = DateTimeOffset.UtcNow.AddDays(-12);

        for (var i = 0; i < 4; i++)
        {
            await InsertAtAsync(_companyA.Value, Probe, $"tamper-{i}", baseTime.AddMinutes(i));
        }

        var first = await SearchAsync(_auditorToken, $"&action={Probe}&pageSize=2");
        var cursor = first.GetProperty("nextCursor").GetString()!;

        // Flip the middle of the encoded value.
        var bytes = Convert.FromBase64String(cursor);
        bytes[bytes.Length / 2] ^= 0xFF;

        using var response = await SendAsync(
            _auditorToken,
            Url($"&action={Probe}&pageSize=2&cursor={Uri.EscapeDataString(Convert.ToBase64String(bytes))}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheFinalPageReportsNoMore()
    {
        if (Unavailable()) { return; }

        var page = await SearchAsync(_auditorToken, "&action=nothing.matches.this&pageSize=5");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task AMalformedCursorIsRejected()
    {
        if (Unavailable()) { return; }

        using var response = await SendAsync(_auditorToken, Url("&cursor=not-a-real-cursor"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ACursorFromADifferentFilterIsRejected()
    {
        // §5.4: a filter change invalidates the cursor, and it must be an error rather than
        // silent misbehaviour — continuing with the old keyset would skip rows without saying so.
        if (Unavailable()) { return; }

        for (var i = 0; i < 4; i++)
        {
            await InsertEventAsync(_companyA.Value, "filter.probe", $"filter-{i}");
        }

        var first = await SearchAsync(_auditorToken, "&action=filter.probe&pageSize=2");
        var cursor = first.GetProperty("nextCursor").GetString()!;

        using var response = await SendAsync(
            _auditorToken, Url($"&action=authentication.sign-in&pageSize=2&cursor={Uri.EscapeDataString(cursor)}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ACursorIssuedForAnotherCompanyReturnsNothingOfTheirs()
    {
        // A cursor carries a position, not an authority. Replaying B's cursor as A re-runs A's
        // tenant-scoped query — the database filters, not the cursor.
        if (Unavailable()) { return; }

        for (var i = 0; i < 4; i++)
        {
            await InsertEventAsync(_companyB.Value, "session.revoke", $"b-page-{i}");
        }

        // Built by hand for B's data, with the fingerprint A's identical filter would produce.
        var page = await SearchAsync(_auditorToken, "&action=session.revoke&pageSize=2");

        var correlations = page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("correlationId").GetString())
            .ToList();

        Assert.DoesNotContain(correlations, c => c?.StartsWith("b-page", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("&pageSize=0")]
    [InlineData("&pageSize=201")]
    [InlineData("&pageSize=-1")]
    public async Task AnOutOfRangePageSizeIsRejected(string query)
    {
        if (Unavailable()) { return; }

        using var response = await SendAsync(_auditorToken, Url(query));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ARangeWiderThanNinetyDaysIsRejected()
    {
        // §5.5 requires a time range on ledger queries and caps it at 90 days. An unbounded range
        // would scan every partition — the query shape partitioning exists to avoid.
        if (Unavailable()) { return; }

        var from = DateTimeOffset.UtcNow.AddDays(-200).ToString("O");
        var to = DateTimeOffset.UtcNow.AddDays(1).ToString("O");

        using var response = await SendAsync(
            _auditorToken,
            $"/api/v1/audit-events?fromUtc={Uri.EscapeDataString(from)}&toUtc={Uri.EscapeDataString(to)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Authorization -------------------------------------------------------------------------

    [Fact]
    public async Task AnEmployeeWithoutAuditReadIsDenied()
    {
        if (Unavailable()) { return; }

        using var response = await SendAsync(_plainToken, Url(""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefused()
    {
        if (Unavailable()) { return; }

        using var client = _host!.GetTestClient();
        using var response = await client.GetAsync(new Uri(Url(""), UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AMalformedTokenIsRefused()
    {
        if (Unavailable()) { return; }

        using var response = await SendAsync("not.a.token", Url(""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExportRequiresTheSamePermission()
    {
        if (Unavailable()) { return; }

        using var response = await SendAsync(_plainToken, Url("", "/export"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Export --------------------------------------------------------------------------------

    [Fact]
    public async Task AnAuthorisedExportReturnsJson()
    {
        if (Unavailable()) { return; }

        using var response = await SendAsync(_auditorToken, Url("", "/export"));

        response.EnsureSuccessStatusCode();

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString() ?? "",
            StringComparison.Ordinal);

        var body = await response.Content.ReadAsStringAsync();
        using var parsed = JsonDocument.Parse(body);

        Assert.Equal(JsonValueKind.Array, parsed.RootElement.ValueKind);
        Assert.NotEmpty(parsed.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task AnExportIsTenantScoped()
    {
        if (Unavailable()) { return; }

        await InsertEventAsync(_companyB.Value, "authentication.sign-in", "b-export-correlation");

        using var response = await SendAsync(_auditorToken, Url("", "/export"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("b-export-correlation", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExportLeaksNoCredentialMaterial()
    {
        if (Unavailable()) { return; }

        using var response = await SendAsync(_auditorToken, Url("", "/export"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(Password, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$argon2", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_auditorToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain("streamEntryId", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyExportIsAWellFormedEmptyArray()
    {
        if (Unavailable()) { return; }

        using var response = await SendAsync(
            _auditorToken, Url("&action=nothing.matches.this", "/export"));

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("[]", body.Trim());
    }

    [Fact]
    public async Task AnExportIsItselfAudited()
    {
        // AC-i and §3.6: bulk data leaving is a security-relevant act, and §3.5 lists export among
        // the events an exfiltration investigation looks for.
        if (Unavailable()) { return; }

        using (var export = await SendAsync(_auditorToken, Url("", "/export")))
        {
            export.EnsureSuccessStatusCode();
        }

        var page = await SearchAsync(_auditorToken, "&action=audit.export");

        var item = Assert.Single(page.GetProperty("items").EnumerateArray().Take(1).ToList());

        Assert.Equal("audit-trail", item.GetProperty("targetType").GetString());
        Assert.Equal(
            _auditorId.Value.ToString("n"), item.GetProperty("actorEmployeeId").GetString());
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>
    /// A range fixed for the lifetime of the test, not recomputed per request.
    /// </summary>
    /// <remarks>
    /// <b>This was the pagination bug.</b> The range was previously built from
    /// <c>DateTimeOffset.UtcNow</c> inside <see cref="Url"/>, so page one and page two asked for
    /// microscopically different ranges — a different filter, therefore a different fingerprint,
    /// therefore a cursor the handler correctly refused with 400.
    /// <para>
    /// The refusal was right: §5.4 requires a filter change to invalidate the cursor rather than
    /// silently continue. What was wrong was a test helper that changed the filter behind its own
    /// back, which is indistinguishable from a client doing it by accident — and is precisely the
    /// case the fingerprint exists to catch.
    /// </para>
    /// </remarks>
    private readonly DateTimeOffset _rangeFrom = DateTimeOffset.UtcNow.AddDays(-30);
    private readonly DateTimeOffset _rangeTo = DateTimeOffset.UtcNow.AddDays(1);

    private static List<string> Correlations(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("correlationId").GetString()!)];

    private string Url(string extra, string path = "/")
    {
        var from = _rangeFrom.ToString("O");
        var to = _rangeTo.ToString("O");

        return $"/api/v1/audit-events{(path == "/" ? "" : path)}" +
               $"?fromUtc={Uri.EscapeDataString(from)}&toUtc={Uri.EscapeDataString(to)}{extra}";
    }

    private async Task<JsonElement> SearchAsync(string token, string extra)
    {
        using var response = await SendAsync(token, Url(extra));

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> SendAsync(string token, string url)
    {
        var client = _host!.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
    }

    /// <summary>Inserts with a chosen id, so the tie-break can be tested deterministically.</summary>
    private Task InsertAtWithIdAsync(
        Guid company, string action, string correlation, DateTimeOffset at, Guid id) =>
        ExecuteAsync(
            _unprivileged,
            $"""
             INSERT INTO auditing.audit_events
                 (id, occurred_at_utc, action, outcome, actor_type, company_id, correlation_id)
             VALUES ('{id}', timestamptz '{at:O}', '{action}', 'Success', 'Anonymous',
                     '{company}', '{correlation}');
             """,
            company);

    private Task InsertAtAsync(Guid company, string action, string correlation, DateTimeOffset at) =>
        ExecuteAsync(
            _unprivileged,
            $"""
             INSERT INTO auditing.audit_events
                 (id, occurred_at_utc, action, outcome, actor_type, company_id, correlation_id)
             VALUES (gen_random_uuid(), timestamptz '{at:O}', '{action}', 'Success', 'Anonymous',
                     '{company}', '{correlation}');
             """,
            company);

    private Task InsertEventAsync(Guid company, string action, string correlation) =>
        ExecuteAsync(
            _unprivileged,
            $"""
             INSERT INTO auditing.audit_events
                 (id, occurred_at_utc, action, outcome, actor_type, company_id, correlation_id)
             VALUES (gen_random_uuid(), now(), '{action}', 'Success', 'Anonymous',
                     '{company}', '{correlation}');
             """,
            company);

    private static async Task AdministerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            $"Host=localhost;Port=5432;Database=postgres;Username={Environment.UserName}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, Guid? company = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using (var tenant = new NpgsqlCommand(
            company is null
                ? "SELECT set_config('app.current_company_id', '', false);"
                : $"SELECT set_config('app.current_company_id', '{company}', false);",
            connection))
        {
            await tenant.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<string> SignInAsync(string email)
    {
        using var client = _host!.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new { email, password = Password, clientType = "WebConsole" }).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);

        return payload.GetProperty("accessToken").GetString()!;
    }

    private async Task SeedAsync()
    {
        using (var scope = _host!.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

            context.Permissions.Add(Permission.Define(AuditPermissions.AuditRead, "Read the audit trail"));
            context.RoleDefinitions.Add(
                RoleDefinition.Define(RoleCode.Create(AuditorRole), "Auditor", isBuiltIn: true));
            context.RolePermissions.Add(
                RolePermission.Grant(RoleCode.Create(AuditorRole), AuditPermissions.AuditRead));

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        _auditorId = await CreateEmployeeAsync(_companyA, AuditorEmail, AuditorRole).ConfigureAwait(false);
        _plainId = await CreateEmployeeAsync(_companyA, PlainEmail, role: null).ConfigureAwait(false);
    }

    private async Task<EmployeeId> CreateEmployeeAsync(CompanyId company, string email, string? role)
    {
        var tenant = _host!.Services.GetRequiredService<ITenantContext>();
        using var _ = tenant.BeginTenantScope(company);

        EmployeeId employeeId;

        using (var scope = _host.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MaintOrbitDbContext>();

            var employee = Employee.Invite(company, Email.Create(email), DateTimeOffset.UtcNow);
            context.Employees.Add(employee);
            await context.SaveChangesAsync().ConfigureAwait(false);
            employeeId = employee.Id;

            if (role is not null)
            {
                context.EmployeeRoles.Add(EmployeeRole.Assign(
                    company, employeeId, RoleCode.Create(role),
                    PermissionScope.Company, scopeId: null, DateTimeOffset.UtcNow));

                await context.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<ICommandHandler<AcceptInvitationCommand>>()
                .HandleAsync(
                    new AcceptInvitationCommand(
                        employeeId,
                        InvitationToken.Create("hVJ8kQ2mNpR4tS7wZ1xC3vB5nM6aD9fG"),
                        Password),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return employeeId;
    }

    private static IHost BuildHost(string connectionString, string elevatedConnectionString) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .UseEnvironment(EnvironmentNames.Development)
                .ConfigureServices(services =>
                {
                    var configuration = new ConfigurationBuilder()
                        .AddInMemoryCollection(TestJwtConfiguration.With(new Dictionary<string, string?>
                        {
                            ["Application:Name"] = "MaintOrbit AI",
                            ["Application:PublicBaseUrl"] = "https://api.example.test",
                            ["Cors:AllowCredentials"] = "true",
                            ["Cors:AllowedOrigins:0"] = "https://console.example.test",
                            ["Persistence:ConnectionString"] = connectionString,
                            ["Persistence:ElevatedConnectionString"] = elevatedConnectionString,
                            ["PasswordHashing:MemoryKibibytes"] = "19456",
                            ["PasswordHashing:Iterations"] = "2",
                            ["PasswordHashing:Parallelism"] = "1",
                            ["PasswordHashing:Version"] = "1"
                        }))
                        .Build();

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddApplication().AddInfrastructure(configuration)
                        .AddApi(configuration).AddObservability(configuration);
                })
                .Configure(app =>
                {
                    app.UseApiPipeline();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAuthenticationEndpoints();
                        endpoints.MapAuditEventEndpoints();
                    });
                }))
            .Build();
}
