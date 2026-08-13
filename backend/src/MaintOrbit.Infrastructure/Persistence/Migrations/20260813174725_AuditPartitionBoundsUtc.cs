using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MaintOrbit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Corrects audit partition boundaries to UTC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The 12.2 migration wrote partition bounds at local midnight, not UTC.</b> Its <c>DO</c>
    /// block computed months as <c>date</c> values and passed them through <c>%L</c>, producing a
    /// <c>timestamp without time zone</c> literal. Assigned to a <c>timestamptz</c> partition key,
    /// PostgreSQL interprets such a literal in the <b>server's</b> timezone — so on a server set to
    /// anything but UTC, every boundary was displaced by that offset.
    /// </para>
    /// <para>
    /// The consequence is not cosmetic. On a server at <c>+05:30</c>, <c>audit_events_2026_07</c>
    /// spanned 30 June 18:30Z to 31 July 18:30Z: an event at 31 July 20:00Z was stored in the
    /// partition named for <i>August</i>. Retention drops whole partitions by name, so the error
    /// would have destroyed part of one month while preserving part of another — and §1.7 requires
    /// UTC throughout precisely so this cannot happen.
    /// </para>
    /// <para>
    /// It was invisible on a UTC server, which is why it survived review: the defect only appears
    /// where the deployment's timezone differs, and correctness then depends on a setting nothing
    /// in the schema controls.
    /// </para>
    /// <para>
    /// <b>This migration refuses rather than destroys.</b> Recreating a partition means dropping
    /// it, and dropping a partition destroys audit history that has no other copy and no delete
    /// path. So it checks first: if any partition holds a row, it raises and stops, leaving a
    /// human to decide how to move the data. Only empty partitions are rebuilt.
    /// </para>
    /// </remarks>
    public partial class AuditPartitionBoundsUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    partition record;
                    occupied text[];
                    month_start timestamptz;
                    partition_name text;
                BEGIN
                    -- Refuse before touching anything. A partial rebuild that had already dropped
                    -- three empty partitions before meeting a populated one would be worse than
                    -- either outcome, so the check runs over all of them first.
                    SELECT array_agg(c.relname ORDER BY c.relname) INTO occupied
                    FROM pg_class c
                    JOIN pg_inherits i ON i.inhrelid = c.oid
                    WHERE i.inhparent = 'auditing.audit_events'::regclass
                      AND (SELECT count(*) FROM auditing.audit_events a
                           WHERE a.tableoid = c.oid) > 0;

                    IF occupied IS NOT NULL THEN
                        RAISE EXCEPTION
                            'Audit partitions % hold rows and their bounds are not UTC-aligned. '
                            'Rebuilding them would destroy audit history, so this migration stops. '
                            'Move the rows to correctly bounded partitions, then re-run.',
                            array_to_string(occupied, ', ');
                    END IF;

                    -- Rebuild every partition with an explicitly UTC-anchored range. The literals
                    -- below carry an offset, so the server's timezone no longer participates.
                    FOR partition IN
                        SELECT c.relname, c.oid
                        FROM pg_class c
                        JOIN pg_inherits i ON i.inhrelid = c.oid
                        WHERE i.inhparent = 'auditing.audit_events'::regclass
                        ORDER BY c.relname
                    LOOP
                        EXECUTE format('DROP TABLE auditing.%I', partition.relname);
                    END LOOP;

                    month_start := date_trunc('month', now() AT TIME ZONE 'UTC')
                                       AT TIME ZONE 'UTC' - interval '1 month';

                    FOR i IN 0..13 LOOP
                        partition_name := 'audit_events_'
                            || to_char((month_start + (i || ' month')::interval)
                                       AT TIME ZONE 'UTC', 'YYYY_MM');

                        EXECUTE format(
                            'CREATE TABLE auditing.%I PARTITION OF auditing.audit_events
                                 FOR VALUES FROM (%L) TO (%L)',
                            partition_name,
                            month_start + (i || ' month')::interval,
                            month_start + ((i + 1) || ' month')::interval);

                        -- Unchanged from 12.2, and still necessary: row-level security is not
                        -- inherited from the parent, so a partition without these is a relation
                        -- holding every Company's audit events with no isolation.
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately not reversed. Reverting would restore boundaries that are wrong on any
            // server outside UTC, and the partitions this created may hold audit events by the time
            // anybody runs it — which the forward direction refuses to destroy for the same reason.
        }
    }
}
