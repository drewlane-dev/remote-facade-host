using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace RemoteFacadeHost;

/// <summary>
/// The MVC pipeline the facade is served on, in one place so Program.cs and
/// the in-process suite cannot drift apart.
///
/// Both settings below exist to hold the wire exactly where minimal APIs left
/// it. Measured against the pre-migration image, endpoint by endpoint --
/// nothing here is inferred from documentation.
/// </summary>
public static class FacadeWiring
{
    public static IServiceCollection AddFacade(
        this IServiceCollection services, InstanceHolder holder, ServedPlugin plugin)
    {
        services.AddSingleton(holder);
        services.AddSingleton(plugin);

        services
            .AddControllers()
            // MVC discovers controllers from the ENTRY assembly's dependency
            // context. In the container that is this assembly, so this call is
            // redundant there; in a test process the entry assembly is the test
            // assembly and FacadeController would not be found at all. Naming it
            // explicitly is what lets the in-process suite exercise this very
            // method rather than a hand-built copy of it.
            //
            // Adding a part that is ALREADY present does not double-register
            // the routes: ControllerFeatureProvider.PopulateFeature skips a type
            // it has already collected. If that ever stopped being true every
            // endpoint would fail with AmbiguousMatchException, which is what
            // test/run.sh would see first.
            .AddApplicationPart(typeof(FacadeController).Assembly)
            // [ApiController] otherwise treats InvokeRequest's non-nullable
            // members as required and answers 400 before the action runs. Two
            // measured cases depend on it NOT doing that: a body with no
            // "method" must reach the envelope as
            // {"ok":false,"error":"no method '' taking 0 argument(s)"}, and a
            // body with no "args" must succeed, because Invoker reads
            // request.Args?.Length ?? 0. Both are 200s on the pre-migration
            // image; automatic validation would turn both into 400s.
            //
            // Suppressing it also means a body that does not bind at all
            // arrives as a null argument instead of an automatic 400 -- see
            // FacadeController.Invoke, which restores that 400 itself.
            .ConfigureApiBehaviorOptions(options =>
            {
                options.SuppressModelStateInvalidFilter = true;

                // [ApiController] otherwise rewrites every client-error status
                // into an RFC 9110 ProblemDetails body. The pre-migration image
                // answered 400 and 415 with NO body, and this suite compares
                // bodies byte-for-byte -- a ProblemDetails document also
                // carries a per-request traceId, which nothing can compare
                // against a fixed expectation.
                //
                // The status codes are what callers act on and they are
                // unchanged either way; this only decides whether a body rides
                // along with them.
                options.SuppressMapClientErrors = true;
            });

        return services;
    }

    public static WebApplication MapFacade(this WebApplication app)
    {
        app.MapControllers();
        return app;
    }
}
