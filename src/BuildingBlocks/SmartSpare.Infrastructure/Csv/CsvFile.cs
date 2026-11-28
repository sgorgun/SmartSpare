using System.Globalization;
using System.Text;
using SmartSpare.SharedKernel;

namespace SmartSpare.Infrastructure.Csv;

/// <summary>
/// The one CSV reader and writer of SmartSpare (STAGE_7_PLAN 2.6), shared by the parts and demand-history pipeline and
/// every master-data catalogue. Reading: UTF-8 text with or without BOM; a header row naming the expected columns in any
/// order (case-insensitive), plus any of the optional columns; comma or semicolon as separator, detected from the header; RFC 4180 quoting (quotes around
/// a field, doubled quotes inside, separators and line breaks inside quotes); empty lines skipped; at most
/// <c>maxRows</c> data rows. A row with more fields than the header is refused as a whole-file error, because its values
/// would land in the wrong columns. Empty values become <see langword="null"/>. Writing: comma separated, CRLF, a BOM so
/// spreadsheets open it as UTF-8, quoting only where a value needs it.
/// </summary>
public static class CsvFile
{
    /// <summary>The rows of a file with their line numbers (header = line 1) and the values in the expected column order.</summary>
    public sealed record Parsed(IReadOnlyList<string> Columns, IReadOnlyList<(int LineNo, string?[] Values)> Rows);

    /// <summary>
    /// Parses <paramref name="content"/>. Errors are validation errors whose codes start with <paramref name="errorPrefix"/>,
    /// followed by <c>Empty</c>, <c>Header</c>, <c>Shape</c> or <c>TooLarge</c> (for example <c>Inventory.ImportHeader</c>).
    /// </summary>
    public static Result<Parsed> Parse(string content, IReadOnlyList<string> expected, int maxRows, string errorPrefix, IReadOnlyList<string>? optional = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(expected);
        optional ??= [];
        var records = Records(content.TrimStart((char)0xFEFF)).GetEnumerator();
        using (records)
        {
            if (!records.MoveNext())
            {
                return Error.Validation(errorPrefix + "Empty", "The file has no header row.");
            }

            var header = records.Current.Fields.Select(f => (f ?? string.Empty).Trim().ToLowerInvariant()).ToArray();
            var missing = expected.Except(header, StringComparer.Ordinal).ToList();
            var unknown = header.Except(expected, StringComparer.Ordinal).Except(optional, StringComparer.Ordinal).ToList();
            if (missing.Count > 0 || unknown.Count > 0 || header.Distinct(StringComparer.Ordinal).Count() != header.Length)
            {
                return Error.Validation(
                    errorPrefix + "Header",
                    $"The header must name exactly these columns: {string.Join(", ", expected)}."
                    + (optional.Count > 0 ? $" Optional: {string.Join(", ", optional)}." : string.Empty)
                    + (missing.Count > 0 ? $" Missing: {string.Join(", ", missing)}." : string.Empty)
                    + (unknown.Count > 0 ? $" Unknown: {string.Join(", ", unknown)}." : string.Empty));
            }

            // The optional columns the file has follow the expected ones, so the rows match Parsed.Columns.
            var columns = expected.Concat(optional.Where(o => header.Contains(o, StringComparer.Ordinal))).ToArray();
            var positions = columns.Select(c => Array.IndexOf(header, c)).ToArray();
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
                    return Error.Validation(errorPrefix + "Shape", $"Line {lineNo} has {fields.Count} values but the header has {header.Length} columns.");
                }

                if (rows.Count == maxRows)
                {
                    return Error.Validation(errorPrefix + "TooLarge", $"A file has at most {maxRows} data rows.");
                }

                rows.Add((lineNo, [.. positions.Select(p => p < fields.Count && !string.IsNullOrWhiteSpace(fields[p]) ? fields[p] : null)]));
            }

            return new Parsed(columns, rows);
        }
    }

    /// <summary>A CSV document: the header, then one line per row; <see langword="null"/> is an empty value.</summary>
    public static string Write(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder().Append((char)0xFEFF);
        AppendLine(text, columns);
        foreach (var row in rows)
        {
            AppendLine(text, row);
        }

        return text.ToString();
    }

    /// <summary>A value as CSV writes it: invariant culture, booleans as <c>true</c>/<c>false</c>.</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Semicolon if the header line has more semicolons than commas (spreadsheets with a decimal comma), else comma.</summary>
    public static char DetectSeparator(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var end = content.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? content : content[..end];
        return header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
    }

    private static void AppendLine(StringBuilder text, IReadOnlyList<string?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            var value = values[i] ?? string.Empty;
            if (value.AsSpan().IndexOfAny(",;\"\r\n") >= 0 || value != value.Trim())
            {
                text.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                text.Append(value);
            }
        }

        text.Append("\r\n");
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
}
