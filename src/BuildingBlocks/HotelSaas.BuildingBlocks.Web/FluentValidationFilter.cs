using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HotelSaas.BuildingBlocks.Web;

// Runs FluentValidation on every action argument that has a validator.
//
// A filter rather than three lines at the top of every action: validation is
// a cross-cutting concern, and the version that has to be remembered per
// action is the version that gets forgotten on the fourteenth one.
//
// Registered globally in AddHotelSaasControllers. An argument with no
// registered IValidator<T> is simply skipped, so adding a validator is what
// switches validation on for a command - no wiring, no attribute.
public sealed class FluentValidationFilter(IServiceProvider services) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Dictionary<string, string[]>? errors = null;

        foreach (object? argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            // Resolve IValidator<TArgument> for the argument's RUNTIME type.
            Type validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());

            if (services.GetService(validatorType) is not IValidator validator)
            {
                continue;
            }

            ValidationResult result = await validator
                .ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted)
                .ConfigureAwait(false);

            if (result.IsValid)
            {
                continue;
            }

            errors ??= new Dictionary<string, string[]>(StringComparer.Ordinal);

            foreach (IGrouping<string, ValidationFailure> group in
                result.Errors.GroupBy(failure => ToCamelCase(failure.PropertyName)))
            {
                errors[group.Key] = group.Select(failure => failure.ErrorMessage).ToArray();
            }
        }

        if (errors is not null)
        {
            ProblemDetails problem = ProblemDetailsFactory.CreateValidation(errors, context.HttpContext);

            context.Result = new ObjectResult(problem)
            {
                StatusCode = problem.Status,
                ContentTypes = { "application/problem+json" },
            };

            return;
        }

        await next().ConfigureAwait(false);
    }

    // FluentValidation reports PropertyName in PascalCase, but the JSON the
    // client sent was camelCase. The field map has to match what they sent,
    // or the frontend cannot attach the message to the input that caused it.
    private static string ToCamelCase(string propertyName)
        => string.IsNullOrEmpty(propertyName) || char.IsLower(propertyName[0])
            ? propertyName
            : char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
}
