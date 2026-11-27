using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;
using SmartSpare.SharedKernel.MasterData;

namespace SmartSpare.Modules.Inventory.Domain.Stock;

/// <summary>
/// Why stock is corrected (catalogue <c>inventory.adjustment_reason</c>, STAGE_7_PLAN 3.1, ADR 0025). Every stock
/// adjustment names one: a reason allows surpluses (<see cref="In"/>), shortages (<see cref="Out"/>) or both, and may
/// require a note. Archived reasons stay on the ledger rows that used them but cannot be chosen (SS500); nothing
/// blocks archiving.
/// </summary>
internal sealed partial class AdjustmentReason : MasterRecord<short>
{
    public const string In = "IN";
    public const string Out = "OUT";
    public const string Both = "BOTH";

    private AdjustmentReason()
    {
        Direction = null!;
    }

    private AdjustmentReason(string code, string name, string direction, bool requiresNote)
        : base(code, name)
    {
        Direction = direction;
        RequiresNote = requiresNote;
    }

    /// <summary><see cref="In"/>, <see cref="Out"/> or <see cref="Both"/>.</summary>
    public string Direction { get; private set; }

    /// <summary>An adjustment with this reason must explain itself in the note.</summary>
    public bool RequiresNote { get; private set; }

    /// <summary>The code as stored: trimmed, in capitals, 2 to 30 letters, digits or underscores.</summary>
    public static Result<string> NormaliseCode(string? code) => MasterRecordRules.NormaliseCode(code, CodePattern(), InventoryErrors.InvalidReasonCode);

    public static Result<AdjustmentReason> Create(string? code, string? name, string? direction, bool requiresNote)
    {
        var normalisedCode = NormaliseCode(code);
        if (normalisedCode.IsFailure)
        {
            return normalisedCode.Error;
        }

        var normalisedName = MasterRecordRules.NormaliseName(name);
        if (normalisedName.IsFailure)
        {
            return normalisedName.Error;
        }

        var normalisedDirection = NormaliseDirection(direction);
        return normalisedDirection.IsFailure
            ? normalisedDirection.Error
            : new AdjustmentReason(normalisedCode.Value, normalisedName.Value, normalisedDirection.Value, requiresNote);
    }

    /// <summary>Changes name, direction and note rule; <see langword="true"/> if anything changed.</summary>
    public Result<bool> Update(string? name, string? direction, bool requiresNote)
    {
        var normalisedDirection = NormaliseDirection(direction);
        if (normalisedDirection.IsFailure)
        {
            return normalisedDirection.Error;
        }

        var renamed = Rename(name);
        if (renamed.IsFailure)
        {
            return renamed.Error;
        }

        var changed = renamed.Value || Direction != normalisedDirection.Value || RequiresNote != requiresNote;
        Direction = normalisedDirection.Value;
        RequiresNote = requiresNote;
        return changed;
    }

    /// <summary>Whether a correction of this sign may use the reason.</summary>
    public bool Allows(decimal signedQuantity) =>
        Direction == Both || (signedQuantity > 0 ? Direction == In : Direction == Out);

    private static Result<string> NormaliseDirection(string? direction)
    {
        var value = direction?.Trim().ToUpperInvariant();
        return value is In or Out or Both ? value : InventoryErrors.InvalidReasonDirection;
    }

    [GeneratedRegex("^[A-Z0-9_]{2,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
