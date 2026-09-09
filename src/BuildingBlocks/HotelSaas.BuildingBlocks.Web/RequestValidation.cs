using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;

namespace HotelSaas.BuildingBlocks.Web;

// FluentValidation to RFC 9457, in exactly one place.
//
// Deliberately NOT Results.ValidationProblem: that emits a different body
// (no machine-readable `code`, PascalCase field names, a different `type`
// URI). docs/00-conventions.md 5 says no service invents its own error
// shape, and two shapes in one service is the same problem in miniature.
public static class RequestValidation
{
    // Returns null when valid, so a caller reads as:
    //     if (RequestValidation.Validate(validator, command) is { } problem)
    //     {
    //         return problem;
    //     }
    public static IResult? Validate<T>(IValidator<T> validator, T instance)
    {
        ArgumentNullException.ThrowIfNull(validator);

        ValidationResult result = validator.Validate(instance);

        if (result.IsValid)
        {
            return null;
        }

        Dictionary<string, string[]> errors = result.Errors
            .GroupBy(e => ToCamelCase(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray(), StringComparer.Ordinal);

        Microsoft.AspNetCore.Mvc.ProblemDetails problem =
            ProblemDetailsFactory.CreateValidation(errors);

        return Results.Problem(
            title: problem.Title,
            statusCode: problem.Status,
            type: problem.Type,
            extensions: problem.Extensions);
    }

    // FluentValidation reports PropertyName in PascalCase, but the JSON the
    // client sent was camelCase. The field map has to match what they sent,
    // or the frontend cannot attach the message to the input that caused it.
    private static string ToCamelCase(string propertyName)
        => string.IsNullOrEmpty(propertyName) || char.IsLower(propertyName[0])
            ? propertyName
            : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
}
