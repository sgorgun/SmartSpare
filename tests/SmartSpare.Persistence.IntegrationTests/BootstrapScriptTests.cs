using SmartSpare.MigrationRunner;

namespace SmartSpare.Persistence.IntegrationTests;

/// <summary>Parsing only, no Docker: deliberately not tagged Category=Integration.</summary>
public sealed class BootstrapScriptTests
{
    private static readonly Dictionary<string, string> Variables = new(StringComparer.Ordinal)
    {
        ["migrator_pwd"] = "it's secret",
        ["api_pwd"] = "a",
        ["jobs_pwd"] = "j",
        ["report_pwd"] = "r",
    };

    [Fact]
    public void RealScript_ParsesIntoStatementsAndGExecQueries()
    {
        var commands = BootstrapScript.Parse(BootstrapScript.Load(), Variables);

        commands.ShouldContain(c => c.GExec && c.Sql.Contains("CREATE DATABASE smartspare", StringComparison.Ordinal));
        commands.ShouldContain(c => !c.GExec && c.Sql.StartsWith("GRANT smartspare_owner TO smartspare_migrator", StringComparison.Ordinal));
        commands.ShouldAllBe(c => !c.Sql.Contains(@"\gexec") && !c.Sql.Contains(@"\set") && !c.Sql.Contains(":'"));
    }

    [Fact]
    public void Variables_BecomeQuotedLiterals()
    {
        var commands = BootstrapScript.Parse(BootstrapScript.Load(), Variables);

        commands.ShouldContain(c => c.Sql.Contains("'it''s secret'", StringComparison.Ordinal));
    }

    [Fact]
    public void TrailingComments_AreRemoved_ButQuotedDashesAreKept()
    {
        var commands = BootstrapScript.Parse("SELECT '--x' AS a; -- note\nGRANT a TO b;   -- why\n", Variables);

        commands.Select(c => c.Sql).ShouldBe(["SELECT '--x' AS a;", "GRANT a TO b;"]);
    }

    [Theory]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$;", "$$")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS $body$\nSELECT 1;\n$body$;", "$body$")]
    public void DollarQuotedBodies_AreRejectedBeforeAnythingRuns(string script, string quote)
    {
        var error = Should.Throw<FormatException>(() => BootstrapScript.Parse("GRANT a TO b;\n" + script, Variables));

        error.Message.ShouldContain($"'{quote}' on line 2");
        error.Message.ShouldContain("does not support dollar-quoted bodies");
    }

    [Fact]
    public void PositionalParameters_AreNotMistakenForDollarQuotes() =>
        BootstrapScript.Parse("SELECT $1, $2;", Variables).ShouldHaveSingleItem();

    [Fact]
    public void MissingVariable_Fails() =>
        Should.Throw<InvalidOperationException>(() => BootstrapScript.Parse("SELECT :'unknown';", Variables));
}
