using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace StudyScheduler.API.Core.Authentication;

/// <summary>
/// Documents the Telegram init data header as an API-key security scheme so the OpenAPI UI
/// (Scalar) shows an auth input. Paste the full header value, e.g. <c>tma query_id=...&amp;hash=...</c>.
/// </summary>
internal sealed class TelegramSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        var scheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "Authorization",
            Description = "Telegram Mini App init data. Value format: `tma <initData>`.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[TelegramAuthOptions.Scheme] = scheme;

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(TelegramAuthOptions.Scheme, document)] = [],
        });

        return Task.CompletedTask;
    }
}
