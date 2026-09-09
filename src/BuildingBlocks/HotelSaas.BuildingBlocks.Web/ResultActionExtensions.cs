using HotelSaas.BuildingBlocks.Domain;
using Microsoft.AspNetCore.Mvc;

namespace HotelSaas.BuildingBlocks.Web;

// The one place a Result becomes an MVC response.
//
// Success returns the resource itself, with the status code carrying the
// meaning. There is no { success, data, message } envelope: the status code
// already says whether the call worked, and duplicating that into the body
// means clients check two places and eventually trust the wrong one.
public static class ResultActionExtensions
{
    // 204 on success. For commands that have nothing to return.
    public static ActionResult ToActionResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess
            ? new NoContentResult()
            : result.Error.ToProblemActionResult();
    }

    // 200 with the value.
    public static ActionResult<TValue> ToActionResult<TValue>(this Result<TValue> result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess
            ? new OkObjectResult(result.Value)
            : result.Error.ToProblemActionResult();
    }

    // 201 plus a Location header, so the client learns where the new
    // resource lives instead of having to build the URL itself.
    public static ActionResult<TValue> ToCreatedResult<TValue>(
        this Result<TValue> result,
        Func<TValue, string> location)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(location);

        return result.IsSuccess
            ? new CreatedResult(location(result.Value), result.Value)
            : result.Error.ToProblemActionResult();
    }

    // An Error straight to a problem response, for read paths that never
    // build a Result in the first place.
    public static ObjectResult ToProblemActionResult(this Error error)
    {
        ProblemDetails problem = ProblemDetailsFactory.Create(error);

        return new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
