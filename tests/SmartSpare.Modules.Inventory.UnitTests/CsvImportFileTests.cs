using FluentValidation.TestHelper;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Modules.Inventory.Features.Imports;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;

namespace SmartSpare.Modules.Inventory;

/// <summary>Reading an uploaded CSV into raw rows (STAGE_5_PLAN section 5): header, separators, quoting, limits.</summary>
public sealed class CsvImportFileTests
{
    private const string DemandHeader = "doc_date,warehouse_code,brand_code,oem_part_number,quantity,doc_ref";

    [Fact]
    public void Rows_keep_their_file_line_and_the_source_column_order()
    {
        var parsed = Parse("warehouse_code,doc_date,brand_code,oem_part_number,quantity,doc_ref\nKLP,2026-11-02,JD,RE900001,3,INV-1\n");

        parsed.Columns.ShouldBe(ImportSources.Columns[ImportSources.DemandHistory]);
        var (lineNo, values) = parsed.Rows.ShouldHaveSingleItem();
        lineNo.ShouldBe(2, "the header is line 1");
        values.ShouldBe(["2026-11-02", "KLP", "JD", "RE900001", "3", "INV-1"]);
    }

    [Fact]
    public void A_byte_order_mark_semicolons_and_windows_line_ends_are_understood()
    {
        var parsed = Parse("﻿doc_date;warehouse_code;brand_code;oem_part_number;quantity;doc_ref\r\n2026-11-02;KLP;JD;RE900001;2,5;\r\n");

        parsed.Rows.ShouldHaveSingleItem().Values.ShouldBe(["2026-11-02", "KLP", "JD", "RE900001", "2,5", null]);
    }

    [Fact]
    public void Quoted_fields_may_hold_separators_quotes_and_line_breaks()
    {
        var content = "brand_code,oem_part_number,description,category_code,uom_code,weight_kg,unit_cost,condition_based\n"
            + "JD,RE900001,\"Filter, oil \"\"premium\"\"\nfor 6R\",OIL_FILTER,PCS,0.45,18.40,no\n"
            + "JD,RE900002,Seal kit,SEALS,SET,,7.10,\n";

        var parsed = CsvImportFile.Parse(content, ImportSources.PartCatalog).Value;

        parsed.Rows.Count.ShouldBe(2);
        parsed.Rows[0].Values[2].ShouldBe("Filter, oil \"premium\"\nfor 6R");
        parsed.Rows[1].LineNo.ShouldBe(4, "the quoted line break moves the next record one line down");
        parsed.Rows[1].Values[5].ShouldBeNull("an empty value is null in the raw layer");
    }

    [Fact]
    public void Empty_lines_are_skipped_and_short_rows_are_padded_with_nulls()
    {
        var parsed = Parse(DemandHeader + "\n\n2026-11-02,KLP\n");

        parsed.Rows.ShouldHaveSingleItem().Values.ShouldBe(["2026-11-02", "KLP", null, null, null, null]);
    }

    [Theory]
    [InlineData("", "Inventory.ImportEmpty")]
    [InlineData("doc_date,warehouse_code,brand_code,oem_part_number,quantity", "Inventory.ImportHeader")]
    [InlineData(DemandHeader + ",extra", "Inventory.ImportHeader")]
    [InlineData("doc_date,doc_date,brand_code,oem_part_number,quantity,doc_ref", "Inventory.ImportHeader")]
    [InlineData(DemandHeader + "\n2026-11-02,KLP,JD,RE900001,3,INV-1,surplus", "Inventory.ImportShape")]
    public void Malformed_files_are_refused_as_a_whole(string content, string code) =>
        CsvImportFile.Parse(content, ImportSources.DemandHistory).Error.Code.ShouldBe(code);

    [Fact]
    public void The_header_error_names_what_is_missing() =>
        CsvImportFile.Parse("doc_date,warehouse_code,brand_code,oem_part_number,quantity", ImportSources.DemandHistory)
            .Error.Description.ShouldEndWith("Missing: doc_ref.");

    [Fact]
    public void An_unknown_source_is_refused() =>
        CsvImportFile.Parse(DemandHeader, "suppliers").Error.Code.ShouldBe("Inventory.ImportSource");

    [Fact]
    public void A_file_has_at_most_the_row_limit()
    {
        var rows = string.Concat(Enumerable.Repeat("2026-11-02,KLP,JD,RE900001,1,x\n", ImportLimits.MaxRows));

        CsvImportFile.Parse(DemandHeader + "\n" + rows, ImportSources.DemandHistory).Value.Rows.Count.ShouldBe(ImportLimits.MaxRows);
        CsvImportFile.Parse(DemandHeader + "\n" + rows + "2026-11-02,KLP,JD,RE900001,1,x\n", ImportSources.DemandHistory)
            .Error.Code.ShouldBe("Inventory.ImportTooLarge");
    }

    [Theory]
    [InlineData("a,b;c", ',')]
    [InlineData("a;b;c,d", ';')]
    [InlineData("single", ',')]
    public void The_separator_is_taken_from_the_header(string header, char separator) =>
        CsvImportFile.DetectSeparator(header + "\nx;y;z;w").ShouldBe(separator);

    [Fact]
    public void The_command_is_validated()
    {
        var validator = new ImportFileCommandValidator();
        validator.TestValidate(new ImportFileCommand(ImportSources.PartCatalog, "a.csv", [1])).IsValid.ShouldBeTrue();
        validator.TestValidate(new ImportFileCommand("suppliers", "a.csv", [1])).ShouldHaveValidationErrorFor(c => c.Source);
        validator.TestValidate(new ImportFileCommand(ImportSources.PartCatalog, "", [1])).ShouldHaveValidationErrorFor(c => c.FileName);
        validator.TestValidate(new ImportFileCommand(ImportSources.PartCatalog, "a.csv", [])).ShouldHaveValidationErrorFor(c => c.Content);
        validator.TestValidate(new ImportFileCommand(ImportSources.PartCatalog, "a.csv", new byte[ImportLimits.MaxFileBytes + 1]))
            .ShouldHaveValidationErrorFor(c => c.Content);
    }

    [Fact]
    public void Sources_and_statuses_have_their_api_names()
    {
        ImportMapping.ToApi(ImportMapping.ToDomain(ImportSources.PartCatalog)).ShouldBe(ImportSources.PartCatalog);
        ImportMapping.ToApi(ImportMapping.ToDomain(ImportSources.DemandHistory)).ShouldBe(ImportSources.DemandHistory);
        Enum.GetValues<ImportStatus>().Select(ImportMapping.ToApi).ShouldBe(
            [ImportStatuses.Uploaded, ImportStatuses.Validated, ImportStatuses.Loaded, ImportStatuses.Failed]);
    }

    private static CsvImportFile.Parsed Parse(string content) => CsvImportFile.Parse(content, ImportSources.DemandHistory).Value;
}
