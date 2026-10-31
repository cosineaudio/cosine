using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Cosine.Tests.Unit;

/// <summary>
/// Guards the set of Subsonic routes the server actually exposes.
/// </summary>
/// <remarks>
/// Sharing was removed outright rather than implemented: it hands out public, unauthenticated
/// stream URLs, which is not something a self-hosted instance wants, and it was never wired to
/// anything - the URLs pointed at a <c>getShare</c> endpoint that existed in neither the spec
/// nor the code, and the web UI never referenced it. The endpoints now 404. Regenerating the
/// controllers from an unedited upstream spec would reintroduce the abstract base class, which
/// is the quiet way this could creep back.
/// </remarks>
public class SubsonicRouteSurfaceTests
{
    /// <summary>
    /// Full route templates reachable through a concrete (registered) controller: a method's
    /// own template, prefixed by its controller's <c>[Route]</c> where there is one.
    /// <para>
    /// Any attribute carrying a template counts, not just <c>[Route]</c> - the generated
    /// Subsonic controllers spell theirs as <c>[HttpGet, Route("rest/...")]</c> while the
    /// hand-written ones use <c>[HttpGet("...")]</c>, and a surface guard that only sees one of
    /// those spellings can be walked straight past.
    /// </para>
    /// </summary>
    private static List<string> RegisteredRoutes()
    {
        var controllers = typeof(Cosine.Api.Controllers.BrowsingController).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        return controllers
            .SelectMany(t =>
            {
                var prefix = t.GetCustomAttributes<RouteAttribute>(inherit: true)
                    .Select(a => a.Template)
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p));

                return t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .SelectMany(m => m.GetCustomAttributes(inherit: true).OfType<IRouteTemplateProvider>())
                    .Select(a => a.Template)
                    .Where(template => !string.IsNullOrEmpty(template))
                    .Select(template => prefix is null ? template! : $"{prefix}/{template}");
            })
            .Distinct()
            .ToList();
    }

    [Fact]
    public void NoSharingRoutesAreExposed()
    {
        var sharing = RegisteredRoutes()
            .Where(r => r.Contains("Share", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(sharing);
    }

    // Without this the test above would pass just as happily on an empty route list.
    [Fact]
    public void TheRestOfTheApiSurfaceIsStillRegistered()
    {
        var routes = RegisteredRoutes();

        Assert.Contains("rest/getAlbum", routes);
        Assert.Contains("rest/stream", routes);
        Assert.Contains("rest/getUser", routes);
        // Templates, not operations: a GET and its POST twin share one, and the Subsonic areas
        // this server doesn't implement (podcasts, jukebox) have no concrete controller at all.
        Assert.True(routes.Count > 50, $"expected the full Subsonic surface, found {routes.Count}");
    }

    /// <summary>
    /// cosine answers library questions OpenSubsonic has no call for (an artist's complete
    /// songs, for one). Those belong under <c>/api</c>: a made-up <c>/rest</c> method name is
    /// allocated from the spec's namespace, so a later official call could take the name and
    /// mean something else while clients probing for it get cosine's shape instead. Every
    /// route reachable through a controller is either a real Subsonic method or off <c>/rest</c>
    /// entirely - this is what keeps the two from blurring one convenient endpoint at a time.
    /// </summary>
    [Fact]
    public void ExtensionEndpointsAreNotOnTheSubsonicSurface()
    {
        var restRoutes = RegisteredRoutes()
            .Where(r => r.StartsWith("rest/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Named for the extension concepts, not the spelling: any of these appearing under
        // /rest means an extension was bolted onto the Subsonic surface.
        var offSpec = restRoutes
            .Where(r => r.Contains("artistSongs", StringComparison.OrdinalIgnoreCase)
                     || r.Contains("allSongs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offSpec);
    }

    [Fact]
    public void TheArtistSongsExtensionIsRegisteredUnderApi()
    {
        var routes = RegisteredRoutes();

        Assert.Contains("api/artists/{id:guid}/songs", routes);
    }

    /// <summary>
    /// The inverse guard of <see cref="ExtensionEndpointsAreNotOnTheSubsonicSurface"/>: no
    /// endpoint off <c>/rest</c> speaks in the generated Subsonic types. The web API has its
    /// own DTOs (<c>Cosine.Api.WebApi.Dtos</c>); a controller that reaches for a convenient
    /// <c>Child</c> or <c>AlbumID3</c> off <c>/rest</c> re-couples the web client to the
    /// Subsonic data model one endpoint at a time - which is exactly how the last coupling
    /// (<c>/api/library</c>) came to exist.
    /// </summary>
    [Fact]
    public void NoEndpointOffRestUsesGeneratedSubsonicTypes()
    {
        var offenders = new List<string>();
        var controllers = typeof(Cosine.Api.Controllers.BrowsingController).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var controller in controllers)
        {
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var templates = method.GetCustomAttributes(inherit: true)
                    .OfType<IRouteTemplateProvider>()
                    .Select(a => a.Template)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToList();
                var prefix = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                    .Select(a => a.Template)
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? string.Empty;

                // Only actions actually routed off /rest are held to this.
                var routed = templates.Count > 0 || prefix.Length > 0;
                var onRest = templates.Any(t => t!.StartsWith("rest/", StringComparison.OrdinalIgnoreCase))
                    || prefix.StartsWith("rest/", StringComparison.OrdinalIgnoreCase);
                if (!routed || onRest)
                    continue;

                var signatureTypes = method.GetParameters().Select(p => p.ParameterType)
                    .Append(method.ReturnType);
                if (signatureTypes.SelectMany(Expand).Any(IsGeneratedSubsonicType))
                    offenders.Add($"{controller.Name}.{method.Name}");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>The type plus its generic arguments, recursively - ActionResult&lt;List&lt;Child&gt;&gt; must not hide one.</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        foreach (var inner in type.GetGenericArguments().SelectMany(Expand))
            yield return inner;
    }

    private static bool IsGeneratedSubsonicType(Type type)
        => type.Namespace?.StartsWith("Cosine.Api.Generated", StringComparison.Ordinal) == true;
}
