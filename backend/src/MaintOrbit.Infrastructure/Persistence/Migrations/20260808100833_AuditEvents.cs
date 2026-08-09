using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates <c>auditing.audit_events</c> — partitioned, tenant-scoped, and append-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written as raw SQL rather than through <c>CreateTable</c>.</b> Declarative partitioning
    /// has no <c>MigrationBuilder</c> expression, and DD-12 requires the table to be partitioned
    /// <i>from the first migration</i>: retrofitting partitioning onto a populated table rewrites
    /// it entirely. The column list below is the one EF generated, kept identical so the model and
    /// the database agree — <c>AuditEventSchemaTests</c> reads the applied schema back and checks.
    /// </para>
    /// </remarks>
    public partial class AuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "auditing");

            // ---- The relation --------------------------------------------------------------
            //
            // PARTITION BY RANGE (occurred_at_utc), monthly (§9.2). Retention on an append-only
            // relation is a partition drop and nothing else (DB-P5, §7.2) — there is no delete
            // path to use instead, by design.
            //
            // The primary key is composite because PostgreSQL requires the partition key to be
            // part of it (DD-2). id alone would be rejected outright.
            migrationBuilder.Sql(
                """
                CREATE TABLE auditing.audit_events (
                    id uuid NOT NULL,
                    occurred_at_utc timestamp with time zone NOT NULL,
                    action character varying(128) NOT NULL,
                    outcome character varying(16) NOT NULL,
                    actor_type character varying(16) NOT NULL,
                    company_id uuid NULL,
                    actor_employee_id uuid NULL,
                    target_type character varying(64) NULL,
                    target_id character varying(128) NULL,
                    correlation_id character varying(128) NULL,
                    context jsonb NULL,
                    stream_entry_id character varying(64) NULL,
                    CONSTRAINT pk_audit_events PRIMARY KEY (id, occurred_at_utc),
                    CONSTRAINT ck_audit_events_outcome
                        CHECK (outcome IN ('Success', 'Failure', 'Denied')),
                    CONSTRAINT ck_audit_events_actor_type
                        CHECK (actor_type IN ('Anonymous', 'Employee', 'System')),
                    CONSTRAINT ck_audit_events_actor_identified
                        CHECK (actor_type <> 'Employee' OR actor_employee_id IS NOT NULL)
                ) PARTITION BY RANGE (occurred_at_utc);
                """);

            // ---- Indexes -------------------------------------------------------------------
            //
            // The four from database-design §4.10. Created on the parent, so PostgreSQL creates a
            // matching index on every partition, now and whenever one is added.
            //
            // ux_audit_events_stream_entry_id carries occurred_at_utc, and that is a documented
            // decision rather than an embellishment. DD-6 specifies the column unique; DD-12
            // requires the table partitioned; PostgreSQL refuses a unique index on a partitioned
            // table that omits the partition key. The two frozen decisions cannot both be honoured
            // literally.
            //
            // Including the partition key keeps DD-6's intent intact: redelivery replays the same
            // stream entry as the same event with the same occurred_at_utc, so the duplicate is
            // still refused. What is given up is only the ability to reject the same stream entry
            // id bearing a *different* timestamp — which would not be a redelivery.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_audit_events_stream_entry_id
                    ON auditing.audit_events (stream_entry_id, occurred_at_utc);
                """);

            // Every documented query is within one Company, and the row-level security policy adds
            // a company_id predicate to each of them regardless — so leading with company_id is
            // what lets the planner use the index rather than filter after it.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_audit_events_company_id_occurred_at_utc
                    ON auditing.audit_events (company_id, occurred_at_utc);
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_audit_events_company_id_actor_employee_id_occurred_at_utc
                    ON auditing.audit_events (company_id, actor_employee_id, occurred_at_utc);
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX ix_audit_events_company_id_action_occurred_at_utc
                    ON auditing.audit_events (company_id, action, occurred_at_utc);
                """);

            // ---- Partitions ----------------------------------------------------------------
            //
            // One month before through twelve months after the moment this migration is applied.
            // The month behind covers an event timestamped just before a boundary; the twelve
            // ahead are runway.
            //
            // §9.2 says partitions are created ahead of need by a scheduled job, and that job is
            // the Worker — not built, and out of scope here. Until it exists this runway is finite,
            // and T-5 states the consequence plainly: a missing partition is an outage of the
            // ingestion path. Because audit emission is fail-open (ADR-0021), the visible symptom
            // would not be an error to the caller but AU-8 incidents in the log and events lost.
            // Recorded in the deferred-work register as the first thing the Worker must do.
            //
            // No DEFAULT partition, deliberately. It would convert that outage into silent
            // misfiling, and rows sitting in a DEFAULT partition then block creation of the real
            // partition covering their range — turning a loud, fixable gap into one that resists
            // the fix.
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    month_start date := date_trunc('month', now() AT TIME ZONE 'UTC')::date
                                        - interval '1 month';
                    i integer;
                    partition_name text;
                BEGIN
                    FOR i IN 0..13 LOOP
                        partition_name := 'audit_events_'
                            || to_char(month_start + (i || ' month')::interval, 'YYYY_MM');

                        EXECUTE format(
                            'CREATE TABLE auditing.%I PARTITION OF auditing.audit_events
                                 FOR VALUES FROM (%L) TO (%L)',
                            partition_name,
                            month_start + (i || ' month')::interval,
                            month_start + ((i + 1) || ' month')::interval);

                        -- A partition is a table. Policies on the parent apply when rows are
                        -- reached through the parent, which is how the application reads and
                        -- writes — but a partition addressed directly answers to its own. Leaving
                        -- them off would make "every tenant-scoped relation carries a policy" true
                        -- of the parent and false of the thirteen relations actually holding rows.
                        EXECUTE format(
                            'ALTER TABLE auditing.%I ENABLE ROW LEVEL SECURITY', partition_name);
                        EXECUTE format(
                            'ALTER TABLE auditing.%I FORCE ROW LEVEL SECURITY', partition_name);

                        EXECUTE format(
                            'CREATE POLICY rls_audit_events_read ON auditing.%I
                                 FOR SELECT
                                 USING (company_id = NULLIF(
                                     current_setting(''app.current_company_id'', true), '''')::uuid)',
                            partition_name);

                        EXECUTE format(
                            'CREATE POLICY rls_audit_events_append ON auditing.%I
                                 FOR INSERT
                                 WITH CHECK (company_id IS NOT DISTINCT FROM NULLIF(
                                     current_setting(''app.current_company_id'', true), '''')::uuid)',
                            partition_name);

                        EXECUTE format(
                            'REVOKE UPDATE, DELETE ON auditing.%I FROM CURRENT_USER',
                            partition_name);
                    END LOOP;
                END $$;
                """);

            // ---- Row-level security --------------------------------------------------------
            //
            // In the same migration as the table (DB-P9). ENABLE makes the policies apply; FORCE
            // makes them apply to the owner too, and migrations run as owner.
            migrationBuilder.Sql(
                "ALTER TABLE auditing.audit_events ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                "ALTER TABLE auditing.audit_events FORCE ROW LEVEL SECURITY;");

            // Reads are tenant-scoped exactly like every other relation: an unset, cleared, or
            // malformed session variable yields NULL, `company_id = NULL` is NULL rather than
            // true, and the policy matches nothing. Zero rows, never unfiltered rows (§5.2).
            //
            // Platform-level events — a sign-in attempt against an address matching no Employee —
            // carry a NULL company_id and are therefore invisible to every tenant, which is
            // correct: they belong to no tenant.
            migrationBuilder.Sql(
                """
                CREATE POLICY rls_audit_events_read ON auditing.audit_events
                    FOR SELECT
                    USING (company_id = NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                """);

            // Writes use IS NOT DISTINCT FROM rather than =, and the difference is the whole
            // reason authentication events can be recorded at all.
            //
            // `=` is NULL when either side is NULL, so a WITH CHECK written that way would refuse
            // every platform-level event: the failed sign-in, the lockout, the denial before a
            // tenant is known. Those are the records most worth having.
            //
            // IS NOT DISTINCT FROM treats NULL as equal to NULL, giving exactly the rule wanted:
            // a row may be written for the Company in scope, or with no Company when no Company is
            // in scope — and never for a *different* Company. Cross-tenant injection into the
            // evidence store is still refused, which is what the policy is for.
            migrationBuilder.Sql(
                """
                CREATE POLICY rls_audit_events_append ON auditing.audit_events
                    FOR INSERT
                    WITH CHECK (company_id IS NOT DISTINCT FROM NULLIF(current_setting('app.current_company_id', true), '')::uuid);
                """);

            // ---- Append-only ---------------------------------------------------------------
            //
            // Two independent mechanisms, which §8.2 calls the belt and the braces.
            //
            // First: there is no UPDATE or DELETE policy above. Under FORCE row-level security a
            // command with no policy matches no rows, so an UPDATE affects nothing.
            //
            // Second, and the one that fails loudly: REVOKE (DD-11). A revoked grant raises
            // "permission denied" rather than silently reporting zero rows affected — the
            // difference between a caller learning immediately and a caller believing an edit
            // worked.
            //
            // CURRENT_USER is the role running the migration, which is the role the application
            // connects as. A deployment separating the two must repeat the REVOKE for the
            // application role; noted in the deferred-work register.
            migrationBuilder.Sql(
                "REVOKE UPDATE, DELETE ON auditing.audit_events FROM CURRENT_USER;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the parent removes its partitions, their policies, and their indexes. The
            // policies on the parent are dropped explicitly anyway, so the reversal reads as the
            // inverse of Up rather than relying on a cascade to be remembered.
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS rls_audit_events_append ON auditing.audit_events;");
            migrationBuilder.Sql(
                "DROP POLICY IF EXISTS rls_audit_events_read ON auditing.audit_events;");

            migrationBuilder.Sql("DROP TABLE IF EXISTS auditing.audit_events;");
        }
    }
}
