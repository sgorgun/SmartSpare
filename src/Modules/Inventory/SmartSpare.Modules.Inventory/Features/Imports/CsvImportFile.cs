using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Csv;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Imports;

/// <summary>
/// Reads an uploaded CSV into raw rows (DATABASE_DESIGN section 3: the raw layer keeps every value as text; validation is
/// the staging functions' job) with the shared reader <see cref="CsvFile"/> (STAGE_7_PLAN 2.6): the source's columns in
/// any order, at most <see cref="ImportLimits.MaxRows"/> data rows; errors keep their <c>Inventory.Import*</c> codes.
/// </summary>
internal static class CsvImportFile
{
    public static readonly Error EmptyFile = Error.Validation("Inventory.ImportEmpty", "The file has no header row.");

    public static Result<CsvFile.Parsed> Parse(string content, string source) =>
        ImportSources.Columns.TryGetValue(source, out var expected)
            ? CsvFile.Parse(content, expected, ImportLimits.MaxRows, "Inventory.Import")
            : Error.Validation("Inventory.ImportSource", $"The source must be one of: {string.Join(", ", ImportSources.All)}.");

    internal static char DetectSeparator(string content) => CsvFile.DetectSeparator(content);
}
