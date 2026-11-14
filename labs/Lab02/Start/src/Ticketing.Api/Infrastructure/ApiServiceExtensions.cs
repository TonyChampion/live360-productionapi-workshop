using System.Text.Json.Serialization;
using Asp.Versioning;

namespace Ticketing.Api.Infrastructure;

public static class ApiServiceExtensions
{
    /// <summary>
    /// Cross-cutting API plumbing: JSON, versioning, and the OpenAPI document.
    /// </summary>
    public static IServiceCollection AddApiDefaults(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddApiVersioning(o =>
            {
                o.DefaultApiVersion = new ApiVersion(1, 0);
                o.ReportApiVersions = true;                      // api-supported-versions header
                o.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(o =>
            {
                o.GroupNameFormat = "'v'V";                     // document name: v1
                o.SubstituteApiVersionInUrl = true;             // /api/v{version}/... shows as /api/v1/...
            })
            .AddOpenApi();

        return services;
    }
}
