using System.Globalization;
using SmartSpare.ApiContracts.MasterData;

namespace SmartSpare.Infrastructure.MasterData;

/// <summary>
/// How a catalogue reads and writes CSV (STAGE_7_PLAN 2.6). The first column is always <c>code</c>, the business key the
/// import upserts by; references to other catalogues are codes too (brand <c>CLAAS</c>, unit <c>PCS</c>). The import
/// validates each row with the catalogue's API validator, so a row passes exactly when the same create or update over
/// the API would.
/// </summary>
public sealed record CatalogCsv<TDto, TWrite>
    where TWrite : ICatalogWrite
{
    /// <summary>Column names in file order, lower snake_case; the first is <c>code</c>.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>A record as an export row, in <see cref="Columns"/> order.</summary>
    public required Func<TDto, IReadOnlyList<string?>> ToRow { get; init; }

    /// <summary>A row as a write; value errors are recorded on the row (<see cref="CsvRow.Errors"/>).</summary>
    public required Func<CsvRow, TWrite> FromRow { get; init; }

    /// <summary>The example row of the template.</summary>
    public required IReadOnlyList<string?> ExampleRow { get; init; }
}

/// <summary>One data row of an import with typed readers; a value that cannot be read records an error and gives a default.</summary>
public sealed class CsvRow(int line, IReadOnlyList<string> columns, IReadOnlyList<string?> values)
{
    private readonly List<CatalogImportError> _errors = [];

    public int Line { get; } = line;

    public IReadOnlyList<CatalogImportError> Errors => _errors;

    /// <summary>The trimmed text of <paramref name="column"/>, or <see langword="null"/> if empty.</summary>
    public string? Text(string column)
    {
        var index = IndexOf(column);
        return values[index]?.Trim() is { Length: > 0 } text ? text : null;
    }

    /// <summary><c>true</c>/<c>false</c>, <c>yes</c>/<c>no</c> or <c>1</c>/<c>0</c>; empty is <paramref name="empty"/>.</summary>
    public bool Bool(string column, bool empty = false)
    {
        switch (Text(column)?.ToLowerInvariant())
        {
            case null:
                return empty;
            case "true" or "yes" or "1":
                return true;
            case "false" or "no" or "0":
                return false;
            default:
                Invalid(column, "must be true or false");
                return empty;
        }
    }

    /// <summary>A decimal with a point or a comma as the decimal separator; empty is <see langword="null"/>.</summary>
    public decimal? Number(string column)
    {
        if (Text(column) is not { } text)
        {
            return null;
        }

        if (decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Invalid(column, "must be a number");
        return null;
    }

    /// <summary>Records an error of this row.</summary>
    public void Error(string? column, string code, string message) => _errors.Add(new CatalogImportError(Line, column, code, message));

    private void Invalid(string column, string what) => Error(column, "INVALID", $"'{column}' {what}.");

    private int IndexOf(string column)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], column, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new ArgumentException($"The catalogue has no CSV column '{column}'.", nameof(column));
    }
}
