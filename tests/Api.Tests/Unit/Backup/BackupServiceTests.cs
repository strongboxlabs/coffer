using Coffer.Api.Backup;

namespace Coffer.Api.Tests.Unit.Backup;

/// <summary>
/// Unit tests for the pure parts of <see cref="BackupService"/>. The
/// pg_dump/pg_restore shell-out and the database swap are exercised end-to-end
/// by the maintainer's restore drill, against the real images; here we lock
/// down what the tools are handed.
/// </summary>
public sealed class BackupServiceTests
{
    [Fact]
    public void BuildPgEnvironment_maps_npgsql_connection_to_libpq_vars()
    {
        var (env, database) = BackupService.BuildPgEnvironment(
            "Host=db.internal;Port=6543;Username=coffer_service;Password=p@ss;Database=coffer");

        Assert.Equal("db.internal", env["PGHOST"]);
        Assert.Equal("6543", env["PGPORT"]);
        Assert.Equal("coffer_service", env["PGUSER"]);
        Assert.Equal("coffer", env["PGDATABASE"]);
        Assert.Equal("coffer", database);
        // The password goes to the tools in a file of their own, never in their
        // environment (FU-083).
        Assert.DoesNotContain("PGPASSWORD", env.Keys);
        Assert.DoesNotContain(env.Values, v => v is not null && v.Contains("p@ss", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "prefer")]                  // Npgsql's default, and libpq's
    [InlineData("SSL Mode=Disable", "disable")]
    [InlineData("SSL Mode=Require", "require")]
    [InlineData("SSL Mode=VerifyCA", "verify-ca")]
    [InlineData("SSL Mode=VerifyFull", "verify-full")]
    public void BuildPgEnvironment_carries_the_tls_mode_the_app_connects_with(string setting, string libpq)
    {
        // The app verified an external server while every backup connected with libpq's
        // own default, which falls back to plaintext and checks no certificate.
        var (env, _) = BackupService.BuildPgEnvironment(
            "Host=db.example;Username=u;Password=p;Database=coffer;" + setting);

        Assert.Equal(libpq, env["PGSSLMODE"]);
    }

    [Fact]
    public void BuildPgEnvironment_carries_the_certificates()
    {
        var (env, _) = BackupService.BuildPgEnvironment(
            "Host=db.example;Username=u;Password=p;Database=coffer;SSL Mode=VerifyFull;"
            + "Root Certificate=/certs/ca.pem;SSL Certificate=/certs/client.pem;SSL Key=/certs/client.key");

        Assert.Equal("/certs/ca.pem", env["PGSSLROOTCERT"]);
        Assert.Equal("/certs/client.pem", env["PGSSLCERT"]);
        Assert.Equal("/certs/client.key", env["PGSSLKEY"]);
    }

    [Fact]
    public void BuildPgEnvironment_refuses_a_client_key_passphrase_it_cannot_hand_over()
    {
        // libpq takes one only as a connection parameter, so a backup would have tried
        // the key without it, and failed only when it ran.
        Assert.Throws<BackupException>(() => BackupService.BuildPgEnvironment(
            "Host=db.example;Username=u;Password=p;Database=coffer;SSL Key=/k;SSL Password=x"));
    }

    [Theory]
    [InlineData("plain", "*:*:*:*:plain")]
    [InlineData(@"a:b\c", @"*:*:*:*:a\:b\\c")]   // libpq's two escapes
    public void A_password_file_line_matches_any_connection_and_escapes_the_separator(string password, string line)
    {
        Assert.Equal(line, BackupService.PassFileLine(password));
    }

    [Fact]
    public void A_password_with_a_line_break_is_refused_rather_than_truncated()
    {
        Assert.Throws<BackupException>(() => BackupService.PassFileLine("one\ntwo"));
    }

    private const string TocWithData =
        """
        ;
        2; 3079 266597 EXTENSION - pg_trgm
        216; 1259 266700 TABLE public accounts -
        4600; 0 266700 TABLE DATA public accounts -
        4601; 0 266710 TABLE DATA public txn_header coffer_service
        4657; 0 267107 TABLE DATA public ledger_snapshot_parts -
        """;

    [Fact]
    public void An_archive_holding_data_for_every_live_table_has_nothing_missing()
    {
        Assert.Empty(BackupService.TablesMissingData(
            TocWithData, ["public.accounts", "public.txn_header"]));
    }

    [Fact]
    public void A_table_whose_data_is_absent_from_the_archive_is_reported()
    {
        // The failure that went unseen for two months: the archive existed, was the
        // right size, and held a table's definition without its rows.
        var missing = BackupService.TablesMissingData(
            TocWithData, ["public.accounts", "public.txn_header", "public.payees"]);

        Assert.Equal(new[] { "public.payees" }, missing);
    }

    [Fact]
    public void A_table_definition_without_a_data_entry_is_not_data()
    {
        // "TABLE public accounts" is the definition; only "TABLE DATA" counts.
        var toc = "216; 1259 266700 TABLE public accounts -\n";

        Assert.Equal(new[] { "public.accounts" },
            BackupService.TablesMissingData(toc, ["public.accounts"]));
    }

    [Fact]
    public void The_snapshot_tables_are_declared_exclusions_not_missing_data()
    {
        Assert.Empty(BackupService.TablesMissingData(
            "", ["public.ledger_snapshots", "public.ledger_snapshot_parts"]));
    }

    [Fact]
    public void A_table_in_another_schema_is_matched_by_its_schema()
    {
        var toc = "4600; 0 266700 TABLE DATA public events -\n";

        Assert.Equal(new[] { "audit.events" },
            BackupService.TablesMissingData(toc, ["public.events", "audit.events"]));
    }

    [Fact]
    public void BuildPgEnvironment_defaults_the_port_when_unspecified()
    {
        var (env, _) = BackupService.BuildPgEnvironment(
            "Host=localhost;Username=u;Password=p;Database=coffer");

        // Npgsql's default port; the libpq tools must get an explicit value.
        Assert.Equal("5432", env["PGPORT"]);
    }

    [Fact]
    public void Restore_list_skips_the_snapshot_tables_data_and_nothing_else()
    {
        // Backups taken before ledger_snapshot_parts was excluded carry chunks
        // without their parent rows; replaying them fails the FK between the two,
        // which made every such backup unrestorable. Their DATA is skipped. Their
        // TABLES are kept (the restored install needs them, empty), and so is
        // everything else — the extensions included: the new database's owner
        // creates them, since pgcrypto and pg_trgm are trusted.
        string[] toc =
        [
            ";",
            "; Archive created at 2026-10-09 04:14:56 UTC",
            "2; 3079 266597 EXTENSION - pg_trgm ",
            "4691; 0 0 COMMENT - EXTENSION pg_trgm ",
            "300; 1259 267107 TABLE public ledger_snapshot_parts coffer_service",
            "4656; 0 267100 TABLE DATA public ledger_snapshots coffer_service",
            "4657; 0 267107 TABLE DATA public ledger_snapshot_parts coffer_service",
            "4600; 0 266900 TABLE DATA public txn_headers coffer_service",
            "4601; 0 266901 TABLE DATA public ledger_snapshots_archive coffer_service",
        ];

        // CRLF in, as pg_restore --list writes it on Windows; LF out.
        var list = BackupService.BuildRestoreList(string.Join("\r\n", toc)).Split('\n');

        Assert.Equal(toc.Length, list.Length);
        for (var i = 0; i < toc.Length; i++)
        {
            var skipped = toc[i].StartsWith("4656;", StringComparison.Ordinal)
                       || toc[i].StartsWith("4657;", StringComparison.Ordinal);
            Assert.Equal(skipped ? ";" + toc[i] : toc[i], list[i]);
        }
    }

    [Fact]
    public void A_backup_from_a_newer_build_is_recognised_by_its_migrations()
    {
        // DbUp ignores journal rows it has no script for, so a newer backup would
        // come up half-working and be reported restored. The restore refuses it.
        var unknown = BackupService.UnknownMigrations(
            appliedInBackup: ["245_report_pins.sql", "246_restore_readiness.sql", "251_future.sql", "250_future.sql"],
            shippedByThisBuild: ["245_report_pins.sql", "246_restore_readiness.sql"]);
        Assert.Equal(["250_future.sql", "251_future.sql"], unknown);

        Assert.Empty(BackupService.UnknownMigrations(["245_report_pins.sql"], ["245_report_pins.sql", "246_restore_readiness.sql"]));
    }

    [Fact]
    public void A_restore_error_shown_to_the_browser_never_quotes_row_data()
    {
        // The outcome is readable by whoever holds the attempt id, and pg_restore
        // quotes rows: COPY's CONTEXT line, a constraint's Key (...)=(...).
        var summary = BackupService.SummarizeForOutcome(
            "pg_restore failed (exit 1): pg_restore: error: COPY failed for table \"txn_headers\": "
            + "ERROR:  duplicate key value violates unique constraint \"txn_headers_pkey\"\n"
            + "DETAIL:  Key (id)=(6f1e...) already exists.\n"
            + "CONTEXT:  COPY txn_headers, line 17: \"6f1e...\tGroceries R Us\t-48.00\"");

        Assert.Contains("duplicate key value violates unique constraint", summary);
        Assert.DoesNotContain("Groceries", summary);
        Assert.DoesNotContain("Key (", summary);
        Assert.DoesNotContain("CONTEXT", summary);

        Assert.True(BackupService.SummarizeForOutcome(new string('x', 5000)).Length <= 601);
    }

    [Fact]
    public void A_restore_never_drops_a_database_WITH_FORCE()
    {
        // FORCE has a non-superuser terminate every other session first, and an
        // autovacuum worker counts as a superuser's: the drop then fails with
        // "permission denied to terminate process" whenever autovacuum is in the
        // database — which a freshly loaded one draws within a minute. That race
        // left abandoned restore databases behind. A plain DROP has Postgres cancel
        // the worker itself.
        var sql = BackupService.DropDatabaseSql("coffer_restore_20261010T154154");
        Assert.Equal("DROP DATABASE IF EXISTS \"coffer_restore_20261010T154154\";", sql);
        Assert.DoesNotContain("FORCE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_restore_stamp_round_trips_and_nothing_else_parses_as_one()
    {
        var attempt = Guid.NewGuid();
        var stamp = BackupService.MarkerFor(attempt, "coffer_pre_restore_20261009T185701");
        Assert.Equal((attempt, "coffer_pre_restore_20261009T185701"), BackupService.ParseMarker(stamp));

        Assert.Null(BackupService.ParseMarker(null));
        Assert.Null(BackupService.ParseMarker("an operator's own comment"));
        Assert.Null(BackupService.ParseMarker("coffer-restore:not-a-guid:x"));
    }

    [Fact]
    public void Readiness_names_every_reason_a_swap_could_not_run()
    {
        static BackupService.RestoreReadiness Ready(
            string db = "coffer", bool createDb = true, bool owns = true, string provider = "c") =>
            new("coffer_service", db, createDb, owns, provider, RestoreMarker: null);

        Assert.True(Ready().IsReady);

        // Not provisioned: the fix is named, including the SQL for an install that
        // never received the script.
        var unprovisioned = Ready(createDb: false, owns: false);
        Assert.False(unprovisioned.IsReady);
        Assert.Contains("provision-restore-privileges.sh", unprovisioned.Explain());
        Assert.Contains("ALTER ROLE coffer_service CREATEDB", unprovisioned.Explain());
        Assert.Contains("ALTER DATABASE \"coffer\" OWNER TO coffer_service", unprovisioned.Explain());
        Assert.Contains("Nothing was changed", unprovisioned.Explain());

        // Each of these used to fail only after the restart.
        Assert.False(Ready(db: new string('d', BackupService.RestoreReadiness.MaxDatabaseNameLength + 1)).IsReady);
        Assert.True(Ready(db: new string('d', BackupService.RestoreReadiness.MaxDatabaseNameLength)).IsReady);
        Assert.False(Ready(db: "postgres").IsReady);
        Assert.False(Ready(provider: "i").IsReady);
    }
}
