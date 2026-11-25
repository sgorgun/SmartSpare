using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SmartSpare.Infrastructure.Http;

/// <summary>An example request body (<see cref="StatusCode"/> is <see langword="null"/>) or response body of an endpoint.</summary>
public sealed record OpenApiExampleMetadata(int? StatusCode, object Value);

/// <summary>Marks an endpoint whose 429 response carries <c>Retry-After</c> (the rate limiter adds it).</summary>
public sealed class RetryAfterMetadata
{
    public static RetryAfterMetadata Instance { get; } = new();
}

/// <summary>
/// What every SmartSpare API document adds to the generated descriptions (ADR 0019 §1): request and response examples
/// declared on the endpoint, the <c>code</c> and <c>traceId</c> members of every ProblemDetails, and the
/// <c>Retry-After</c> header of 429 responses.
/// </summary>
public static class OpenApiConventions
{
    /// <summary>A synthetic W3C trace id for examples.</summary>
    public const string ExampleTraceId = "0af7651916cd43dd8448eb211c80319c";

    /// <summary>The JSON options of the API: ASP.NET Core's web defaults (camelCase).</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Documents <paramref name="value"/> as the example request body.</summary>
    public static TBuilder WithRequestExample<TBuilder>(this TBuilder builder, object value)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new OpenApiExampleMetadata(null, value));

    /// <summary>Documents <paramref name="value"/> as the example body of the <paramref name="statusCode"/> response.</summary>
    public static TBuilder WithResponseExample<TBuilder>(this TBuilder builder, int statusCode, object value)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new OpenApiExampleMetadata(statusCode, value));

    /// <summary>Documents a 429 ProblemDetails with <c>Retry-After</c> and <see cref="SmartSpare.ApiContracts.ProblemCodes.TooManyRequests"/>.</summary>
    public static TBuilder ProducesTooManyRequests<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithMetadata(RetryAfterMetadata.Instance)
            .WithResponseExample(StatusCodes.Status429TooManyRequests, Problem(
                StatusCodes.Status429TooManyRequests, "Too Many Requests", SmartSpare.ApiContracts.ProblemCodes.TooManyRequests));

    /// <summary>An example ProblemDetails body, member for member as the API writes it.</summary>
    public static IReadOnlyDictionary<string, object?> Problem(int status, string title, string code, string? detail = null)
    {
        var problem = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = TypeOf(status),
            ["title"] = title,
            ["status"] = status,
        };
        if (detail is not null)
        {
            problem["detail"] = detail;
        }

        problem[ProblemDetailsRegistration.CodeKey] = code;
        problem[ProblemDetailsRegistration.TraceIdKey] = ExampleTraceId;
        return problem;
    }

    /// <summary>Registers the transformers on one document.</summary>
    public static void Apply(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddOperationTransformer(TransformOperation);
        options.AddSchemaTransformer(TransformSchema);
    }

    private static Task TransformOperation(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        foreach (var example in metadata.OfType<OpenApiExampleMetadata>())
        {
            var content = example.StatusCode is { } status
                ? operation.Responses?.GetValueOrDefault(status.ToString(CultureInfo.InvariantCulture))?.Content
                : operation.RequestBody?.Content;
            foreach (var media in content?.Values ?? [])
            {
                if (media is OpenApiMediaType writable)
                {
                    writable.Example = JsonSerializer.SerializeToNode(example.Value, example.Value.GetType(), Json);
                }
            }
        }

        if (metadata.OfType<RetryAfterMetadata>().Any()
            && operation.Responses?.GetValueOrDefault("429") is OpenApiResponse tooMany)
        {
            tooMany.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
            tooMany.Headers["Retry-After"] = new OpenApiHeader
            {
                Description = "Seconds to wait before the next attempt.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Minimum = "1" },
            };
        }

        return Task.CompletedTask;
    }

    private static Task TransformSchema(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        // The web defaults read numbers from strings too, so the generator types them "integer | string" with a pattern.
        // The API always writes JSON numbers; that is the contract.
        if (schema.Type is { } type && (type.HasFlag(JsonSchemaType.Integer) || type.HasFlag(JsonSchemaType.Number)) && type.HasFlag(JsonSchemaType.String))
        {
            schema.Type = type & ~JsonSchemaType.String;
            schema.Pattern = null;
        }

        if (context.JsonTypeInfo.Type == typeof(ProblemDetails) || context.JsonTypeInfo.Type == typeof(HttpValidationProblemDetails))
        {
            schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
            schema.Properties[ProblemDetailsRegistration.CodeKey] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Stable error code: SS-AUTH-001…005, SS-SYS-001, SS100–SS402 or <Module>.<Error> (API_DOCUMENTATION.md §3).",
            };
            schema.Properties[ProblemDetailsRegistration.TraceIdKey] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "W3C trace id of the request, shown to users as \"Reference\".",
            };
        }

        return Task.CompletedTask;
    }

    /// <summary>The <c>type</c> ASP.NET Core writes for the status (<c>ProblemDetailsDefaults</c>).</summary>
    private static string TypeOf(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        StatusCodes.Status401Unauthorized => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        StatusCodes.Status403Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        StatusCodes.Status404NotFound => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        StatusCodes.Status429TooManyRequests => "https://tools.ietf.org/html/rfc6585#section-4",
        _ => "https://tools.ietf.org/html/rfc9110#section-15",
    };
}
