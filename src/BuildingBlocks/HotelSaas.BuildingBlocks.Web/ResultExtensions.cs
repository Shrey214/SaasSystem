using HotelSaas.BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;

namespace HotelSaas.BuildingBlocks.Web;

// The one place a Result becomes an HTTP response.
//
// Success returns the resource itself, with the status code carrying the
// meaning. There is no { success, data, message } envelope: the status code
// already says whether the call worked, and duplicating that into the body
// means clients check two places and eventually trust the wrong one.
public static class ResultExtensions
{
    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess
            ? Results.NoContent()
            : Problem(result.Error);
    }

    public static IResult ToHttpResult<TValue>(this Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Problem(result.Error);
    }

    // For a create: 201 plus a Location header pointing at the new resource.
    public static IResult ToCreatedResult<TValue>(this Result<TValue> result, Func<TValue, string> location)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(location);

        return result.IsSuccess
            ? Results.Created(location(result.Value), result.Value)
            : Problem(result.Error);
    }

    private static IResult Problem(Error error)
    {
        Microsoft.AspNetCore.Mvc.ProblemDetails problem = ProblemDetailsFactory.Create(error);

        return Results.Problem(
            title: problem.Title,
            detail: problem.Detail,
            statusCode: problem.Status,
            type: problem.Type,
            extensions: problem.Extensions);
    }
}
