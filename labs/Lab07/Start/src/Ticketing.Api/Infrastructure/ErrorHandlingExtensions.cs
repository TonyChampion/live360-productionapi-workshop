using FluentValidation;
using System.Diagnostics;
using System.Text.Json;

namespace Ticketing.Api.Infrastructure;

public static class ErrorHandlingExtensions
{
    /// <summary>
    /// Problem Details for every failure: designed ones, validation, routing, and unhandled exceptions.
    /// </summary>
    public static IServiceCollection AddErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var problem = context.ProblemDetails;
                var http = context.HttpContext;

                // Framework-generated problems point at the RFC; give them our catalog types instead
                if (problem.Type is null || problem.Type.StartsWith("https://tools.ietf.org/"))
                {
                    problem.Type = problem.Status switch
                    {
                        StatusCodes.Status400BadRequest => ProblemTypes.Validation,
                        StatusCodes.Status401Unauthorized => ProblemTypes.Unauthorized,
                        StatusCodes.Status403Forbidden => ProblemTypes.Forbidden,
                        StatusCodes.Status404NotFound => ProblemTypes.NotFound,
                        _ => ProblemTypes.Unexpected
                    };
                }

                // Validation error keys should match the JSON the caller sent (title, not Title)
                if (problem is HttpValidationProblemDetails validation)
                {
                    var renamed = validation.Errors.ToDictionary(
                        e => JsonNamingPolicy.CamelCase.ConvertName(e.Key), e => e.Value);
                    validation.Errors.Clear();
                    foreach (var (key, messages) in renamed)
                        validation.Errors[key] = messages;
                }

                problem.Instance ??= $"{http.Request.Method} {http.Request.Path}";
                problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;
            };
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddValidation();
        services.AddValidatorsFromAssemblyContaining<Program>();   // FluentValidation, for rules attributes can't express

        return services;
    }

    public static WebApplication UseErrorHandling(this WebApplication app)
    {
        app.UseExceptionHandler();   // unhandled exceptions -> GlobalExceptionHandler
        app.UseStatusCodePages();    // empty error responses (unknown route, etc.) -> Problem Details
        return app;
    }
}
