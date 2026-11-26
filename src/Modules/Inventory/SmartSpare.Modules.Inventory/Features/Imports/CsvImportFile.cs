using System.Text;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Imports;

/// <summary>
/// Reads an uploaded CSV into raw rows (DATABASE_DESIGN section 3: the raw layer keeps every value as text; validation is
/// the staging functions' job). Rules: UTF-8 with or without BOM; a header row naming the columns of the source in any
/// order; comma or semicolon as separator, detected from the header; RFC 4180 quoting (quotes around a field, doubled
/// quotes inside, separators and line breaks inside quotes); empty lines skipped; at most <see cref="ImportLimits.MaxRows"/>
/// data rows. A row with more fields than the header is refused as a whole file error, because its values would land in
/// the wrong columns. Empty values become <see langword="null"/>.
/// </summary>
internal static class CsvImportFile
{
    public static readonly Error EmptyFile = Error.Validation("Inventory.ImportEmpty", "The file has no header row.");

    /// <summary>The rows of the file with their line numbers (header = line 1) and the values in the source's column order.</summary>
    public sealed record Parsed(IReadOnlyList<string> Columns, IReadOnlyList<(int LineNo, string?[] Values)> Rows);

    public static Result<Parsed> Parse(string content, string source)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!ImportSources.Columns.TryGetValue(source, out var expected))
        {
            return Error.Validation("Inventory.ImportSource", $"The source must be one of: {string.Join(", ", ImportSources.All)}.");
        }

        var records = Records(content.TrimStart('﻿')).GetEnumerator();
        using (records)
        {
            if (!records.MoveNext())
            {
                return EmptyFile;
            }

            var header = records.Current.Fields.Select(f => (f ?? string.Empty).Trim().ToLowerInvariant()).ToArray();
            var missing = expected.Except(header, StringComparer.Ordinal).ToList();
            var unknown = header.Except(expected, StringComparer.Ordinal).ToList();
            if (missing.Count > 0 || unknown.Count > 0 || header.Distinct(StringComparer.Ordinal).Count() != header.Length)
            {
                return Error.Validation(
                    "Inventory.ImportHeader",
                    $"The header must name exactly these columns: {string.Join(", ", expected)}."
                    + (missing.Count > 0 ? $" Missing: {string.Join(", ", missing)}." : string.Empty)
                    + (unknown.Count > 0 ? $" Unknown: {string.Join(", ", unknown)}." : string.Empty));
            }

            var positions = expected.Select(c => Array.IndexOf(header, c)).ToArray();
            var rows = new List<(int, string?[])>();
            while (records.MoveNext())
            {
                var (lineNo, fields) = records.Current;
                if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0]))
                {
                    continue;
                }

                if (fields.Count > header.Length)
                {
                    return Error.Validation(
                        "Inventory.ImportShape", $"Line {lineNo} has {fields.Count} values but the header has {header.Length} columns.");
                }

                if (rows.Count == ImportLimits.MaxRows)
                {
                    return Error.Validation("Inventory.ImportTooLarge", $"A file has at most {ImportLimits.MaxRows} data rows.");
                }

                rows.Add((lineNo, [.. positions.Select(p => p < fields.Count && !string.IsNullOrWhiteSpace(fields[p]) ? fields[p] : null)]));
            }

            return new Parsed(expected, rows);
        }
    }

    /// <summary>Splits the text into records; each record knows the line it starts on.</summary>
    private static IEnumerable<(int LineNo, List<string?> Fields)> Records(string content)
    {
        var separator = DetectSeparator(content);
        var fields = new List<string?>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var recordLine = 1;
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }

                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                i++;
                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                yield return (recordLine, fields);
                fields = [];
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }

                line++;
                recordLine = line;
            }
            else
            {
                field.Append(c);
            }

            i++;
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (recordLine, fields);
        }
    }

    /// <summary>Semicolon if the header line has more semicolons than commas (spreadsheets with a decimal comma), else comma.</summary>
    internal static char DetectSeparator(string content)
    {
        var end = content.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? content : content[..end];
        return header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
    }
}
