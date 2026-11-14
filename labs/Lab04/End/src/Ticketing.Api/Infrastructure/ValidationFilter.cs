using FluentValidation;
using System.Text.Json;

namespace Ticketing.Api.Infrastructure;

/// <summary>
/// Runs the FluentValidation validator for <typeparamref name="TRequest"/> before the handler.
/// Built-in validation (the attributes) has already checked the shape by the time this runs;
/// this is for rules attributes can't express, like one field depending on another.
/// </summary>
public class ValidationFilter<TRequest> : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        // Resolved per request: validators are scoped, so they can't be captured at startup
        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>();
        if (request is null || validator is null)
            return await next(context);

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (result.IsValid)
            return await next(context);

        // Same shape as built-in validation: a 400 ValidationProblem, keyed the way the caller sent the JSON
        var errors = result.Errors
            .GroupBy(e => JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        return TypedResults.ValidationProblem(errors);
    }
}

public static class ValidationFilterExtensions
{
    /// <summary>Validate <typeparamref name="TRequest"/> with its FluentValidation validator before the handler runs.</summary>
    public static RouteHandlerBuilder WithValidator<TRequest>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>()
               .ProducesValidationProblem();
}
