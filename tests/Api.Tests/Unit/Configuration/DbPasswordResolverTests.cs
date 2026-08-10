using Coffer.Api.Configuration;

using Microsoft.Extensions.Configuration;

using Npgsql;

namespace Coffer.Api.Tests.Unit.Configuration;

/// <summary>
/// The contract for getting the database role passwords out of the environment
/// and into files. Two failure modes are worth guarding against specifically: a
/// silent fall-back that leaves the password in the environment while appearing
/// to have moved it, and an install that starts with no password at all — which
/// against a Postgres still configured with <c>trust</c> would authenticate by
/// accident.
/// </summary>
public sealed class DbPasswordResolverTests
{
    private const string AppConn = "Host=postgres;Database=coffer;Username=coffer_app";
    private const string ServiceConn = "Host=postgres;Database=coffer;Username=coffer_service";

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static string PasswordOf(IConfiguration config, string key)
        => new NpgsqlConnectionStringBuilder(config[key]).Password ?? string.Empty;

    [Fact]
    public void Leaves_connection_strings_alone_when_no_password_file_is_configured()
    {
        // The arrangement before this landed, and still the simplest one for a
        // bare-metal install: password inline in the connection string. Nobody is
        // forced into a file they don't want.
        var config = Config(
            ("Api:ConnectionString", $"{AppConn};Password=inline-app"),
            ("Api:ServiceConnectionString", $"{ServiceConn};Password=inline-svc"));

        var outcomes = DbPasswordResolver.ApplyTo(config, _ => throw new InvalidOperationException("must not read any file"));

        Assert.All(outcomes, o => Assert.False(o.FromFile));
        Assert.All(outcomes, o => Assert.False(o.InlinePasswordIgnored));
        Assert.Equal("inline-app", PasswordOf(config, "Api:ConnectionString"));
        Assert.Equal("inline-svc", PasswordOf(config, "Api:ServiceConnectionString"));
    }

    [Fact]
    public void Injects_each_password_from_its_own_file()
    {
        var config = Config(
            ("Api:ConnectionString", AppConn),
            ("Api:ServiceConnectionString", ServiceConn),
            (DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"),
            (DbPasswordResolver.ServicePasswordFileKey, "/run/secrets/svc"));

        var outcomes = DbPasswordResolver.ApplyTo(config, path => path switch
        {
            "/run/secrets/app" => "app-secret",
            "/run/secrets/svc" => "svc-secret",
            _ => throw new FileNotFoundException(path),
        });

        Assert.All(outcomes, o => Assert.True(o.FromFile));
        Assert.Equal("app-secret", PasswordOf(config, "Api:ConnectionString"));
        Assert.Equal("svc-secret", PasswordOf(config, "Api:ServiceConnectionString"));

        // The rest of the connection string survives — this replaces the password,
        // it doesn't rebuild the string.
        var builder = new NpgsqlConnectionStringBuilder(config["Api:ConnectionString"]);
        Assert.Equal("postgres", builder.Host);
        Assert.Equal("coffer", builder.Database);
        Assert.Equal("coffer_app", builder.Username);
    }

    [Fact]
    public void File_wins_over_an_inline_password_and_says_so()
    {
        // The direction that matters. During the transition an install has the
        // password in both places; if the inline one won, moving the secret into a
        // file would appear to work while changing nothing — the same silent
        // failure ADR-0092 D6 hit when env-first precedence quietly undid
        // rotations. The flag exists so startup can tell the operator.
        var config = Config(
            ("Api:ConnectionString", $"{AppConn};Password=stale-inline"),
            ("Api:ServiceConnectionString", ServiceConn),
            (DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"));

        var outcomes = DbPasswordResolver.ApplyTo(config, _ => "from-file");

        var app = outcomes.Single(o => o.Role == "coffer_app");
        Assert.True(app.FromFile);
        Assert.True(app.InlinePasswordIgnored);
        Assert.Equal("from-file", PasswordOf(config, "Api:ConnectionString"));
    }

    [Theory]
    [InlineData("secret\n")]
    [InlineData("secret\r\n")]
    [InlineData("secret\n\n")]
    public void Strips_the_trailing_newline_a_file_carries(string contents)
    {
        // A secret file written by `echo`, printf or a text editor ends in a
        // newline that is not part of the password. Getting this wrong produces an
        // authentication failure with a correct-looking password, which is a
        // miserable thing to debug.
        var config = Config(
            ("Api:ConnectionString", AppConn),
            (DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"));

        DbPasswordResolver.ApplyTo(config, _ => contents);

        Assert.Equal("secret", PasswordOf(config, "Api:ConnectionString"));
    }

    [Fact]
    public void Preserves_spaces_inside_the_password()
    {
        // Deliberately not a full Trim(): a password may legitimately begin or end
        // with a space, and silently altering it would be indistinguishable from
        // the wrong password.
        var config = Config(
            ("Api:ConnectionString", AppConn),
            (DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"));

        DbPasswordResolver.ApplyTo(config, _ => "  pad ded  \n");

        Assert.Equal("  pad ded  ", PasswordOf(config, "Api:ConnectionString"));
    }

    [Fact]
    public void Fails_closed_when_the_configured_file_is_empty()
    {
        var config = Config(
            ("Api:ConnectionString", AppConn),
            (DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => DbPasswordResolver.ApplyTo(config, _ => "\n"));

        Assert.Contains("/run/secrets/app", ex.Message);
    }

    [Fact]
    public void Fails_closed_when_the_configured_file_cannot_be_read()
    {
        var config = Config(
            ("Api:ConnectionString", AppConn),
            (DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => DbPasswordResolver.ApplyTo(config, _ => throw new FileNotFoundException("nope")));

        // The path is named because it locates the secret without being the
        // secret, and naming it is the difference between a one-minute fix and
        // guessing which of two files is wrong.
        Assert.Contains("/run/secrets/app", ex.Message);
        Assert.Contains("coffer_app", ex.Message);
    }

    [Fact]
    public void Fails_when_a_password_file_is_set_but_the_connection_string_is_not()
    {
        // The file supplies only the password; host/database/username still have
        // to come from somewhere.
        var config = Config((DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => DbPasswordResolver.ApplyTo(config, _ => "secret"));

        Assert.Contains("Api:ConnectionString", ex.Message);
    }

    [Fact]
    public void Reports_an_unparseable_connection_string_as_such()
    {
        // Otherwise this surfaces as a raw Npgsql ArgumentException at startup
        // with no indication of which setting is malformed.
        var config = Config(
            ("Api:ConnectionString", "Host=postgres;ThisIsNotAKeyword=1"),
            (DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => DbPasswordResolver.ApplyTo(config, _ => "secret"));

        Assert.Contains("Api:ConnectionString", ex.Message);
    }

    [Fact]
    public void Never_puts_the_password_in_an_exception_message()
    {
        // Startup exceptions get logged, shipped and pasted into issues.
        var config = Config((DbPasswordResolver.AppPasswordFileKey, "/run/secrets/app"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => DbPasswordResolver.ApplyTo(config, _ => "hunter2"));

        Assert.DoesNotContain("hunter2", ex.Message);
    }
}
