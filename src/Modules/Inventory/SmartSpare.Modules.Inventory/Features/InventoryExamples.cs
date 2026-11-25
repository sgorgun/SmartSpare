using Microsoft.AspNetCore.Http;
using SmartSpare.ApiContracts;
using SmartSpare.Infrastructure.Http;

namespace SmartSpare.Modules.Inventory.Features;

/// <summary>Example bodies shared by the module's endpoint descriptions (ADR 0019 §1).</summary>
internal static class InventoryExamples
{
    public static IReadOnlyDictionary<string, object?> Unauthorized { get; } =
        OpenApiConventions.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", ProblemCodes.Unauthenticated);

    /// <summary>A 400 validation problem with one failed field, as <c>ResultHttpExtensions.ToProblem</c> writes it.</summary>
    public static IReadOnlyDictionary<string, object?> ValidationProblem(string field, string message) =>
        new Dictionary<string, object?>(
            OpenApiConventions.Problem(StatusCodes.Status400BadRequest, "Validation failed", "Validation", "One or more validation errors occurred."),
            StringComparer.Ordinal)
        {
            ["errors"] = new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] },
        };
}
