namespace MaintOrbit.Infrastructure.Persistence;

/// <summary>
/// The PostgreSQL session state that row-level security policies read.
/// </summary>
/// <remarks>
/// One definition, shared by the migration that writes the policies and the interceptor that
/// sets the variable. If those two disagreed the policies would compare against a variable
/// nothing sets — every query would return zero rows, which is at least the safe direction, but
/// the same mistake in reverse is not detectable from behaviour at all.
/// </remarks>
internal static class TenantSession
{
    /// <summary>
    /// The session variable naming the Company in scope.
    /// </summary>
    /// <remarks>
    /// PostgreSQL requires a customized option name to contain a dot, so the prefix is not
    /// decoration — <c>current_company_id</c> alone is rejected. <c>app</c> marks it as belonging
    /// to this application rather than to an extension.
    /// <para>
    /// <b>The name itself is an assumption.</b> database-design §5.2 requires a session variable
    /// holding the current Company and specifies the behaviour around it; it does not name it.
    /// </para>
    /// </remarks>
    public const string CompanyVariable = "app.current_company_id";

    /// <summary>
    /// SQL reading the variable as a <c>uuid</c>, yielding <c>NULL</c> when it is not usable.
    /// </summary>
    /// <remarks>
    /// Two failure modes are folded into one result, deliberately:
    /// <list type="bullet">
    /// <item><description>
    /// <c>current_setting(..., true)</c> — the <c>true</c> is <c>missing_ok</c>. Without it, a
    /// connection that never set the variable raises an error instead of returning
    /// <see langword="null"/>, and the documented failure direction is zero rows, not a fault.
    /// </description></item>
    /// <item><description>
    /// <c>NULLIF(..., '')</c> — clearing the variable sets it to the empty string, and
    /// <c>''::uuid</c> raises <c>invalid input syntax</c>. Without this, a correctly cleared
    /// connection would error on the next query rather than see nothing.
    /// </description></item>
    /// </list>
    /// The result is that an absent, cleared, or malformed tenant all produce <c>NULL</c>.
    /// Comparing any value to <c>NULL</c> yields <c>NULL</c>, which a policy treats as
    /// not-satisfied — so the row is invisible. This is §5.2's stated property: <b>unset variable
    /// → policies match nothing → zero rows</b>.
    /// </remarks>
    public const string CurrentCompanyExpression =
        $"NULLIF(current_setting('{CompanyVariable}', true), '')::uuid";

    /// <summary>Sets the variable for the remainder of the session (TC-4, at checkout).</summary>
    /// <remarks>
    /// <c>set_config</c> rather than <c>SET</c> because the value is a parameter — <c>SET</c>
    /// takes only literals, which would mean interpolating a value into SQL. The third argument
    /// <c>false</c> makes it session-scoped rather than transaction-local, so it survives the
    /// many transactions a pooled connection serves.
    /// </remarks>
    public const string SetCompanySql =
        $"SELECT set_config('{CompanyVariable}', $1, false)";

    /// <summary>Clears the variable (TC-4, at return).</summary>
    /// <remarks>
    /// Cleared explicitly rather than left for the next checkout to overwrite. §6.7 requirement 1
    /// states the rule and §5.2 states why: a pooled connection returned with the variable still
    /// set, then handed to a request for a different Company, is a cross-tenant exposure that
    /// presents as an ordinary successful query.
    /// </remarks>
    public const string ClearCompanySql =
        $"SELECT set_config('{CompanyVariable}', '', false)";

    /// <summary>Policy name for a table, per the <c>rls_&lt;table&gt;</c> convention (§1.5).</summary>
    public static string PolicyName(string table) => $"rls_{table}";
}
