using System.Data.Common;
using MaintOrbit.Shared.MultiTenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace MaintOrbit.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Applies the tenant session variable at connection checkout and clears it at return (TC-4).
/// </summary>
/// <remarks>
/// An interceptor rather than a call each repository makes, because ADR-0023 treats coverage as
/// the point: "for the tenant interceptor, incomplete coverage is a security defect". A single
/// forgotten call site is a query running under whatever tenant the connection last served.
/// <para>
/// <b>Set on open, cleared on close</b> — both halves, never relying on the next checkout to
/// overwrite (§6.7 requirement 1). Npgsql resets session state when a connection returns to its
/// own pool, so the clear is redundant there; it is not redundant with an external pooler in
/// front, which is the configuration DD-2 has not yet settled.
/// </para>
/// <para>
/// Untenanted connections are left with the variable cleared rather than unset. Both read as
/// <c>NULL</c> through <see cref="TenantSession.CurrentCompanyExpression"/>, so policies match
/// nothing and the query returns zero rows — the documented failure direction.
/// </para>
/// </remarks>
internal sealed class TenantConnectionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
        await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        base.ConnectionOpened(connection, eventData);
        Apply(connection);
    }

    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        await ClearAsync(connection, CancellationToken.None).ConfigureAwait(false);

        return await base.ConnectionClosingAsync(connection, eventData, result).ConfigureAwait(false);
    }

    public override InterceptionResult ConnectionClosing(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        Clear(connection);

        return base.ConnectionClosing(connection, eventData, result);
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var companyId = tenantContext.Current;

        await using var command = Command(
            connection,
            companyId is null ? TenantSession.ClearCompanySql : TenantSession.SetCompanySql,
            companyId);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Apply(DbConnection connection)
    {
        var companyId = tenantContext.Current;

        using var command = Command(
            connection,
            companyId is null ? TenantSession.ClearCompanySql : TenantSession.SetCompanySql,
            companyId);

        command.ExecuteNonQuery();
    }

    private static async Task ClearAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        await using var command = Command(connection, TenantSession.ClearCompanySql, companyId: null);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Clear(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        using var command = Command(connection, TenantSession.ClearCompanySql, companyId: null);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Builds the command, passing the Company as a parameter.
    /// </summary>
    /// <remarks>
    /// Parameterized, not interpolated. The value is a <see cref="Guid"/> and could not carry
    /// SQL, but a tenant identifier concatenated into a statement is a pattern that gets copied
    /// to places where the value is caller-influenced.
    /// </remarks>
    private static DbCommand Command(DbConnection connection, string sql, CompanyId? companyId)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;

        if (companyId is not null)
        {
            command.Parameters.Add(new NpgsqlParameter
            {
                Value = companyId.Value.Value.ToString()
            });
        }

        return command;
    }
}
