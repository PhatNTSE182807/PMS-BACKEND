using Microsoft.OpenApi.Models;
using PMS.API.Controllers;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PMS.API.Filters;

/// <summary>
/// Lists the Swagger groups in a fixed order with a description; without it Swagger UI
/// orders them by whichever endpoint happens to come first.
/// </summary>
public class SwaggerTagsDocumentFilter : IDocumentFilter
{
    private static readonly (string Name, string Description)[] OrderedTags =
    [
        (SwaggerTags.Authentication, "Register and sign in (password or one-time email code)."),
        (SwaggerTags.Profile, "The signed-in user's profile, vehicles and password."),
        (SwaggerTags.Management, "Look up user accounts."),
    ];

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var used = swaggerDoc.Paths.Values
            .SelectMany(path => path.Operations.Values)
            .SelectMany(operation => operation.Tags.Select(tag => tag.Name))
            .ToHashSet();

        var known = OrderedTags.Select(t => t.Name).ToHashSet();

        swaggerDoc.Tags = OrderedTags
            .Where(t => used.Contains(t.Name))
            .Select(t => new OpenApiTag { Name = t.Name, Description = t.Description })
            // Groups from other controllers keep their default place after the user groups
            .Concat(used.Where(name => !known.Contains(name)).Order().Select(name => new OpenApiTag { Name = name }))
            .ToList();
    }
}
