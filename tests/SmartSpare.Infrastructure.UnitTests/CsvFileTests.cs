using SmartSpare.Infrastructure.Csv;
using SmartSpare.Infrastructure.MasterData;

namespace SmartSpare.Infrastructure.UnitTests;

/// <summary>The shared CSV writer and row readers (STAGE_7_PLAN 2.6): what the export writes, the import reads back.</summary>
public sealed class CsvFileTests
{
    private static readonly string[] Columns = ["code", "name", "requires_note"];

    [Fact]
    public void The_writer_quotes_only_where_needed_and_the_reader_reads_it_back()
    {
        var text = CsvFile.Write(Columns, [["A", "Plain", "true"], ["B", "With, comma; and \"quotes\"", null], ["C", "Two\nlines", "false"]]);

        text[0].ShouldBe((char)0xFEFF, "a BOM so spreadsheets open it as UTF-8");
        text.ShouldContain("A,Plain,true\r\n");
        text.ShouldContain("B,\"With, comma; and \"\"quotes\"\"\",\r\n");

        var parsed = CsvFile.Parse(text, Columns, 10, "Test.Csv").Value;
        parsed.Rows.Select(r => r.Values[1]).ShouldBe(["Plain", "With, comma; and \"quotes\"", "Two\nlines"]);
        parsed.Rows.Select(r => r.LineNo).ShouldBe([2, 3, 4]);
        parsed.Rows[1].Values[2].ShouldBeNull();
    }

    [Theory]
    [InlineData("code,name\n", "Test.CsvHeader")]
    [InlineData("", "Test.CsvEmpty")]
    [InlineData("code,name,requires_note\na,b,c,d\n", "Test.CsvShape")]
    [InlineData("code,name,requires_note\na,b,c\nd,e,f\n", "Test.CsvTooLarge")]
    public void File_errors_carry_the_callers_prefix(string text, string code) =>
        CsvFile.Parse(text, Columns, 1, "Test.Csv").Error.Code.ShouldBe(code);

    [Theory]
    [InlineData(true, "true")]
    [InlineData(1.5, "1.5")]
    [InlineData(null, null)]
    [InlineData("x", "x")]
    public void Values_are_formatted_in_the_invariant_culture(object? value, string? expected) =>
        CsvFile.Format(value).ShouldBe(expected);

    [Fact]
    public void A_row_reads_typed_values_and_records_what_it_cannot_read()
    {
        var row = new CsvRow(7, ["code", "flag", "cost", "bad_flag", "bad_cost"], [" A1 ", "yes", "12,50", "maybe", "ten"]);

        (row.Text("code"), row.Bool("flag"), row.Number("cost")).ShouldBe(("A1", true, 12.50m));
        row.Bool("bad_flag", empty: true).ShouldBeTrue("the default after an error");
        row.Number("bad_cost").ShouldBeNull();
        row.Errors.Select(e => (e.Line, e.Column, e.Code)).ShouldBe([(7, "bad_flag", "INVALID"), (7, "bad_cost", "INVALID")]);
        Should.Throw<ArgumentException>(() => row.Text("missing"));
    }
}
