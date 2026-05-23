using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Trips.Api.Validation;

/// <summary>
/// Endpoint filter that runs a FluentValidation <see cref="IValidator{T}"/> against the first
/// argument of type <typeparamref name="T"/> in the endpoint signature. Returns a 400 ProblemDetails
/// with field-level errors when invalid; otherwise lets the request through.
/// </summary>
public sealed class ValidationFilter<T> : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var validator = context.HttpContext.RequestServices.GetService<IValidator<T>>();
        if (validator is null)
        {
            return await next(context).ConfigureAwait(false);
        }

        var instance = context.Arguments.OfType<T>().FirstOrDefault();
        if (instance is null)
        {
            return await next(context).ConfigureAwait(false);
        }

        var result = await validator.ValidateAsync(instance, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (result.IsValid)
        {
            return await next(context).ConfigureAwait(false);
        }

        var errors = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return TypedResults.ValidationProblem(
            errors: errors,
            type: "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            title: "Validation failed",
            detail: "One or more fields failed validation.",
            instance: context.HttpContext.Request.Path,
            extensions: new Dictionary<string, object?> { ["traceId"] = context.HttpContext.TraceIdentifier });
    }
}
