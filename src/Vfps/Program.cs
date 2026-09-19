using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Amazon.Runtime;
using Amazon.S3;
using BlazorBlueprint.Components;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi.Models;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Vfps;
using Vfps.AppServices;
using Vfps.Authorization;
using Vfps.Components;
using Vfps.Config;
using Vfps.CsvProcessing;
using Vfps.Data;
using Vfps.Fhir;
using Vfps.Metrics;
using Vfps.PseudonymGenerators;
using Vfps.Services;
using Vfps.Tracing;
using Vfps.Voprf.Client;

// `migrate` applies pending EF Core migrations and exits. This is what the container image ships
// instead of the two `dotnet ef migrations bundle` executables it used to carry - see
// DatabaseMigrator for why. Checked before CreateBuilder so a migration run never binds Kestrel,
// reaches an OIDC discovery endpoint or S3, or starts Hangfire's job server.
if (args is ["migrate", ..])
{
    return await DatabaseMigrator.RunAsync(args[1..]);
}

var builder = WebApplication.CreateBuilder(args);

// Read up front rather than where the authentication handlers are wired below, because three
// separate parts of this file need it and one of them - the Swagger document - is configured
// before that point: what the API accepts as a credential is part of its published contract.
var authConfig = new AuthorizationConfig();
builder.Configuration.GetSection("Authorization").Bind(authConfig);

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorBlueprintComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

builder.Services.AddLocalization();

var supportedCultures = new[] { "en", "de" };
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options
        .SetDefaultCulture(supportedCultures[0])
        .AddSupportedCultures(supportedCultures)
        .AddSupportedUICultures(supportedCultures);
});

builder.Services.AddGrpc().AddJsonTranscoding();
builder.Services.AddGrpcSwagger();
builder.Services.AddGrpcHealthChecks();
builder.Services.AddGrpcReflection();
builder.Services.AddHealthChecks().AddDbContextCheck<PseudonymContext>();

// How long the host waits for in-flight work to finish after SIGTERM before tearing everything
// down. Kept just under Kubernetes' own default terminationGracePeriodSeconds of 30s so the
// process gets to finish draining rather than being SIGKILLed exactly as it would have: with both
// at 30s there is no margin at all. Raising this is only useful alongside a matching increase to
// terminationGracePeriodSeconds (and, for gRPC and Blazor clients to actually be routed away
// before the drain starts, a preStop hook) - see the deployment notes in the README.
builder.Services.Configure<HostOptions>(hostOptions =>
{
    // Hosted services otherwise stop one after another in reverse registration order, and
    // GenericWebHostService (Kestrel) is registered last by WebApplicationBuilder - so Kestrel's
    // connection drain runs first and can spend the entire ShutdownTimeout above on its own. A
    // Blazor circuit's WebSocket is a long-running request, so a single open browser tab is
    // enough for that to happen. Everything queued behind the drain then gets an already-expired
    // token: Hangfire's BackgroundJobServerHostedService.StopAsync rethrows it as an unhandled
    // OperationCanceledException, killing an otherwise-clean shutdown with a non-zero exit code
    // (its server itself has long since stopped - it hooks ApplicationStopping directly). None of
    // these services depend on another having stopped first, so stopping them concurrently costs
    // nothing and gives each one the full budget instead of Kestrel's leftovers.
    hostOptions.ServicesStopConcurrently = true;

    hostOptions.ShutdownTimeout = builder.Configuration.GetValue(
        "ShutdownTimeout",
        TimeSpan.FromSeconds(25)
    );
});

// A dedicated metrics port (separate from the app's public HTTP/gRPC listeners) keeps /metrics
// off the internet-facing endpoints - only an in-cluster scraper needs to reach it. Kept as a
// second, code-configured Kestrel listener alongside the appsettings.json-configured Http/
// HttpGrpc endpoints, rather than folding it into those. The separation is enforced in both
// directions by the "metrics port" guard middleware below - /metrics answers only on this port,
// and this port answers nothing but /metrics. Port 0 (used by
// appsettings.Test.json) makes Kestrel bind an ephemeral OS-assigned port, matching the prior
// prometheus-net MetricServer's own Port=0 behavior for parallel test runs.
var metricsPort = builder.Configuration.GetValue<ushort>("MetricsPort", 8082);
builder.WebHost.ConfigureKestrel(kestrelOptions => kestrelOptions.ListenAnyIP(metricsPort));

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc(
        "v1",
        new OpenApiInfo
        {
            Title = "VFPS FHIR and gRPC JSON-transcoded API",
            Version = "v1",
            Description = "A very fast and resource-efficient pseudonym service.",
            License = new OpenApiLicense
            {
                Name = "Apache-2.0",
#pragma warning disable S1075 // URIs should not be hardcoded
                Url = new Uri("https://www.apache.org/licenses/LICENSE-2.0")
#pragma warning restore S1075 // URIs should not be hardcoded
            },
        }
    );

    var filePath = Path.Combine(AppContext.BaseDirectory, "Vfps.xml");
    c.IncludeXmlComments(filePath);
    c.IncludeGrpcXmlComments(filePath, includeControllerXmlComments: true);
    c.UseInlineDefinitionsForEnums();

    // Vfps.Protos and Hl7.Fhir.Model both define a type named "Meta", which otherwise
    // collide under Swashbuckle's default (short-name-only) schemaId generation.
    c.CustomSchemaIds(type => type.FullName?.Replace('+', '.'));

    // Only described when it is actually required: with authorization disabled the API takes no
    // credential at all, and an Authorize button on a deployment that ignores what you type into
    // it is worse than none. With it enabled, the endpoints below are bearer-only - a browser
    // session does not authenticate an API call - so without this, "Try it out" in the bundled
    // Swagger UI has no way to send a token and can only ever answer 401.
    if (authConfig.IsEnabled)
    {
        const string bearerScheme = "Bearer";

        c.AddSecurityDefinition(
            bearerScheme,
            new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description =
                    "An OIDC access token from the configured authority, whose audience must match "
                    + "Authorization__Audience. Paste the token itself - Swagger UI adds the "
                    + "\"Bearer \" prefix. Machine clients normally obtain one through an OAuth2 "
                    + "client_credentials grant against a confidential client of their own.",
            }
        );

        c.AddSecurityRequirement(
            new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = bearerScheme,
                        },
                    },
                    Array.Empty<string>()
                },
            }
        );
    }
});

// A connection failure mid-outage (e.g. a Postgres upgrade/failover, or a rolling restart) would
// otherwise throw immediately out of every DB call on the context - including from inside a
// running CsvPseudonymizationJobRunner job, which has no other way to ride out a transient
// outage. Npgsql's own NpgsqlException.IsTransient correctly classifies a connection-refused/
// timeout failure (the SocketException case) as retriable, so this is safe to apply broadly, not
// just to the CSV job path. 15 retries at up to 5min each, exponential backoff, covers several
// minutes of outage (the real-world trigger for this: a multi-minute Postgres upgrade left every
// DB call failing - see the incident this was added for) while still eventually giving up rather
// than retrying forever. Safe with this codebase's raw-SQL (FromSqlRaw/FromSqlInterpolated)
// usage - none of it opens an explicit Database.BeginTransaction, which is the one thing this
// feature can't wrap.
//
// Applied to every context that talks to this database, not just PseudonymContext:
// DataProtectionKeyContext went without it originally, which meant the exact same Postgres blip
// the API rode out cleanly would still throw on any auth-cookie or antiforgery-token operation,
// taking the Blazor UI down on its own.
void ConfigureNpgsqlResilience(NpgsqlDbContextOptionsBuilder npgsqlOptions) =>
    npgsqlOptions.EnableRetryOnFailure(
        maxRetryCount: 15,
        maxRetryDelay: TimeSpan.FromMinutes(5),
        errorCodesToAdd: null
    );

// Backing-store selection itself lives in DatabaseMigrator so the `migrate` subcommand resolves
// the exact same connection string this does; the resilience configuration below is the part
// only the long-running app wants.
void ConfigurePseudonymContext(IServiceProvider isp, DbContextOptionsBuilder options) =>
    DatabaseMigrator.ConfigurePseudonymContext(
        isp.GetRequiredService<IConfiguration>(),
        options,
        ConfigureNpgsqlResilience
    );

// Who owns the schema: this process, or something that ran before it. Development and an
// explicit ForceRunDatabaseMigrations mean the app migrates itself on startup (see the
// Database.Migrate() calls at the very end); everywhere else a separate `migrate` run - the Helm
// chart's migrations Job - has already done it, and each pod here just waits for that Job. The
// same answer settles Hangfire's PrepareSchemaIfNecessary below, so the two schemas this app
// depends on always have exactly one owner between them. Computed here, off the builder, because
// the Hangfire registration needs it long before the WebApplication exists.
var shouldRunDatabaseMigrations =
    builder.Environment.IsDevelopment()
    || builder.Configuration.GetValue<bool>("ForceRunDatabaseMigrations");

// A factory, not a plain AddDbContext - PseudonymAppService's "trusted" methods
// (CreateTrustedAsync/ReverseLookupTrustedAsync) use IDbContextFactory<PseudonymContext>
// directly to get their own independent context per call, since the CSV job runner calls them
// many times concurrently within a single Hangfire job's DI scope, and DbContext instances
// aren't safe for concurrent use. Everything else in the app still injects a plain, scoped
// PseudonymContext as before - the AddScoped registration below just sources that one instance
// per scope from the same factory, rather than registering AddDbContext separately (which would
// conflict on DbContextOptions<PseudonymContext>'s lifetime). Not pooled: PseudonymContext's
// OnConfiguring (snake_case naming, exception processing) mutates options post-construction,
// which DbContext pooling explicitly disallows ("'OnConfiguring' cannot be used to modify
// DbContextOptions when DbContext pooling is enabled").
builder.Services.AddDbContextFactory<PseudonymContext>(ConfigurePseudonymContext);
builder.Services.AddScoped<PseudonymContext>(isp =>
    isp.GetRequiredService<IDbContextFactory<PseudonymContext>>().CreateDbContext()
);

// The VOPRF pseudonym generator, when a key-holding server is configured for this deployment.
// Off by default, matching this codebase's optional-feature idiom: with no Voprf:Address the
// generator is simply absent and PSEUDONYM_GENERATION_METHOD_VOPRF is not an available method,
// which NamespaceAppService reports at namespace-creation time rather than at first use.
var voprfConfig = new VoprfClientOptions();
builder.Configuration.GetSection(VoprfClientOptions.SectionName).Bind(voprfConfig);

if (!string.IsNullOrWhiteSpace(voprfConfig.Address))
{
    // Validates the options as it registers them - a missing pinned public key or an
    // unparseable address fails startup rather than the first pseudonym.
    builder.Services.AddVoprfPseudonymizer(options =>
    {
        options.Address = voprfConfig.Address;
        options.PublicKey = voprfConfig.PublicKey;
        options.ExpectedKeyId = voprfConfig.ExpectedKeyId;
        options.AllowUnpinnedPublicKey = voprfConfig.AllowUnpinnedPublicKey;
        options.MaxBatchSize = voprfConfig.MaxBatchSize;
        options.IncludeKeyIdInPseudonym = voprfConfig.IncludeKeyIdInPseudonym;
        options.Format = voprfConfig.Format;
        options.Length = voprfConfig.Length;
        options.Normalization = voprfConfig.Normalization;
    });

    builder.Services.AddSingleton<IValueDependentPseudonymGenerator, VoprfPseudonymGenerator>();
}

builder.Services.AddSingleton<PseudonymizationMethodsLookup>(serviceProvider =>
    new(serviceProvider.GetService<IValueDependentPseudonymGenerator>())
);

var cacheConfig = new CacheConfig();
builder.Configuration.GetSection("Pseudonymization:Caching").Bind(cacheConfig);

var isNamespaceCachingEnabled = builder.Configuration.GetValue(
    "Pseudonymization:Caching:Namespaces:IsEnabled",
    false
);
if (isNamespaceCachingEnabled)
{
    builder.Services.AddSingleton<IMemoryCache>(_ => new MemoryCache(
        new MemoryCacheOptions { TrackStatistics = true, SizeLimit = cacheConfig.SizeLimit }
    ));
    builder.Services.AddSingleton(_ => cacheConfig);
    builder.Services.AddScoped<INamespaceRepository, CachingNamespaceRepository>();
}
else
{
    builder.Services.AddScoped<INamespaceRepository, NamespaceRepository>();
}

var isPseudonymCachingEnabled = builder.Configuration.GetValue(
    "Pseudonymization:Caching:Pseudonyms:IsEnabled",
    false
);
if (isPseudonymCachingEnabled)
{
    builder.Services.TryAddSingleton<IMemoryCache>(_ => new MemoryCache(
        new MemoryCacheOptions { TrackStatistics = true, SizeLimit = cacheConfig.SizeLimit }
    ));
    builder.Services.TryAddSingleton(_ => cacheConfig);
    builder.Services.AddScoped<IPseudonymRepository, CachingPseudonymRepository>();
}
else
{
    builder.Services.AddScoped<IPseudonymRepository, PseudonymRepository>();
}

// add a service to regularly query the cache statistics
if (isNamespaceCachingEnabled || isPseudonymCachingEnabled)
{
    builder.Services.AddHostedService<MemoryCacheMetricsBackgroundService>();
}

builder.Services.AddScoped<INamespaceAppService, NamespaceAppService>();
builder.Services.AddScoped<IPseudonymAppService, PseudonymAppService>();

builder.Services.AddHostedService<InitNamespacesBackgroundService>();

// The per-namespace pseudonym count is too expensive to recompute on every replica, so one
// replica computes it and the rest read the result out of the pseudonym_counts table - see
// PseudonymCountMetrics. The background service that reads runs everywhere and unconditionally;
// which replica pays for the recompute is settled by Hangfire's recurring job scheduler below.
builder.Services.AddScoped<IPseudonymCountRepository, PseudonymCountRepository>();
builder.Services.AddScoped<PseudonymCountMetrics>();
builder.Services.AddHostedService<PseudonymCountMetricsBackgroundService>();

// Authorization: off by default (see Config/AuthorizationConfig.cs) - matches the existing
// Tracing/Pseudonymization:Caching:*:IsEnabled idiom in this codebase. Configuration carries the
// OIDC wiring and the bootstrap admin roles only; per-namespace access lives in the database as
// NamespaceAccessGrant rows managed from the admin UI.
builder.Services.Configure<AuthorizationConfig>(builder.Configuration.GetSection("Authorization"));

// A singleton, like the permission checker that reads it: the grant set is process-wide state,
// not per-request, and caching it there is what keeps a permission check off the database on the
// pseudonym-create hot path.
builder.Services.AddSingleton<INamespaceAccessGrantCache, NamespaceAccessGrantCache>();
builder.Services.AddSingleton<INamespacePermissionChecker, NamespacePermissionChecker>();
builder.Services.AddScoped<INamespaceAccessGrantRepository, NamespaceAccessGrantRepository>();
builder.Services.AddScoped<INamespaceAccessGrantAppService, NamespaceAccessGrantAppService>();

// vfps-issued access tokens - personal ones, which act as the user who created them, and
// service-account ones, which act as a principal an admin granted access to. Registered
// unconditionally, like the grant services above: the app services enforce
// Authorization:AccessTokens:IsEnabled themselves, so the UI can inject them and explain why the
// feature is off rather than failing to resolve a dependency. The authentication scheme that
// consumes them, on the other hand, only exists when the feature is on - see below.
builder.Services.AddSingleton<IAccessTokenCache, AccessTokenCache>();
builder.Services.AddSingleton<IAccessTokenUsageTracker, AccessTokenUsageTracker>();
builder.Services.AddHostedService<AccessTokenUsageFlushBackgroundService>();
builder.Services.AddScoped<IAccessTokenRepository, AccessTokenRepository>();
builder.Services.AddScoped<IServiceAccountRepository, ServiceAccountRepository>();
builder.Services.AddScoped<IAccessTokenAppService, AccessTokenAppService>();
builder.Services.AddScoped<IServiceAccountAppService, ServiceAccountAppService>();

if (authConfig.IsEnabled)
{
    // Which of the two bearer handlers a request belongs to. Both credentials travel in the same
    // Authorization header - one header is what keeps every existing client (grpc-dotnet call
    // credentials, the FHIR endpoint, Swagger UI's Authorize button) working unchanged with
    // either - so the token's own prefix is what tells them apart: a JWT is three dot-separated
    // base64url segments and can never start with "vfps_".
    //
    // Anything not recognisably a vfps token goes to the JWT handler, including a request with no
    // Authorization header at all: its challenge is the 401 the API contract promises.
    string SelectBearerScheme(HttpContext context) =>
        authConfig.AccessTokens.IsEnabled
        && context
            .Request.Headers.Authorization.ToString()
            .AsSpan()
            .TrimStart()
            .StartsWith($"Bearer {AccessTokenSecret.Prefix}", StringComparison.OrdinalIgnoreCase)
            ? AccessTokenDefaults.AuthenticationScheme
            : JwtBearerDefaults.AuthenticationScheme;

    builder
        .Services.AddAuthentication(options =>
        {
            // Route browser requests (Blazor UI) to the cookie scheme and everything else
            // (gRPC/REST callers presenting a bearer token) to whichever bearer handler owns the
            // credential presented.
            options.DefaultScheme = "smart";
            options.DefaultChallengeScheme = "smart";
        })
        .AddPolicyScheme(
            "smart",
            "Cookie or Bearer",
            options =>
            {
                options.ForwardDefaultSelector = context =>
                    context
                        .Request.Headers.Authorization.ToString()
                        .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? SelectBearerScheme(context)
                        : CookieAuthenticationDefaults.AuthenticationScheme;
            }
        )
        // What the API policy below is pinned to. A policy scheme rather than listing both bearer
        // schemes on the policy itself, because ASP.NET Core's policy evaluator runs *every*
        // scheme a policy names and merges the successes: a vfps token would then also be handed
        // to the JWT handler, which fetches the authority's discovery document before it even
        // looks at the token. Routing to exactly one handler is what keeps a vfps-issued token
        // working while the identity provider is unreachable - a good part of why one is issued.
        .AddPolicyScheme(
            ApiBearerScheme,
            "Bearer (identity provider or vfps-issued)",
            options => options.ForwardDefaultSelector = SelectBearerScheme
        )
        .AddCookie(options =>
        {
            // Blazor's [Authorize]-on-a-page metadata is enforced by ASP.NET Core's own
            // authorization middleware before Blazor's AuthorizeRouteView/NotAuthorized
            // template ever runs, so an unauthenticated request to any gated page is
            // challenged here first. Without this, it defaults to the nonexistent
            // "/Account/Login" and dead-ends instead of reaching the real OIDC challenge
            // endpoint below. Written without the "/ui" prefix - like every other server-side
            // route template in this file, it's added back via PathBase for the redirect the
            // browser actually receives.
            options.LoginPath = "/authentication/login";
            options.ReturnUrlParameter = "returnUrl";

            // The forbidden counterpart of LoginPath, and the same trap: AccessDeniedPath
            // defaults to a nonexistent "/Account/AccessDenied", so an authenticated non-admin
            // who types in /hangfire (the HangfireDashboard policy below admits admins only) is
            // bounced to a 404 instead of being told no. There is no access-denied page in this
            // app to point it at - "signed in, but not an admin" is rendered in-page everywhere
            // else, see AccessControl.razor - so answer the request honestly rather than
            // redirecting somewhere that doesn't exist.
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect(options =>
        {
            options.Authority = authConfig.Authority;
            options.ClientId = authConfig.ClientId;
            options.ClientSecret = authConfig.ClientSecret;
            options.ResponseType = "code";
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Add("roles");
            // Only relax to plain-HTTP metadata/issuer for local development (e.g. a local
            // Keycloak without TLS) - production authorities must always be HTTPS.
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            // Without this, the .NET JWT stack silently rewrites well-known-sounding claim
            // types (e.g. a "roles" claim becomes the long ClaimTypes.Role URI), which would
            // otherwise silently break AuthorizationConfig.RoleClaimType matching exactly what
            // the IdP was configured to emit.
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.RoleClaimType = authConfig.RoleClaimType;
            options.PushedAuthorizationBehavior = authConfig.UsePushedAuthorizationRequests
                ? PushedAuthorizationBehavior.UseIfAvailable
                : PushedAuthorizationBehavior.Disable;

            // Records when this sign-in happened, because nothing else does. The principal that
            // reaches the auth cookie carries no timestamp: this handler's default ClaimActions
            // delete iat/nbf/exp, and auth_time is optional in OIDC and not emitted by the realm
            // this is tested against. SessionRevalidatingAuthenticationStateProvider needs one to
            // bound how long an open Blazor circuit may keep serving the principal.
            options.Events.OnTokenValidated = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    identity.AddClaim(SessionLifetime.StampFor(DateTimeOffset.UtcNow));
                }

                return Task.CompletedTask;
            };
        })
        .AddJwtBearer(options =>
        {
            options.Authority = authConfig.Authority;
            options.Audience = authConfig.Audience;
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.RoleClaimType = authConfig.RoleClaimType;
        });

    // Only registered when the feature is on, so that a deployment which forbids static
    // credentials has no handler that could accept one: with the scheme absent, the selector
    // above never routes to it and a "vfps_..." token is handed to the JWT handler, which
    // refuses it as the malformed JWT it is.
    if (authConfig.AccessTokens.IsEnabled)
    {
        builder
            .Services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AccessTokenAuthenticationHandler>(
                AccessTokenDefaults.AuthenticationScheme,
                displayName: null,
                configureOptions: _ => { }
            );
    }

    // Replaces Blazor's default ServerAuthenticationStateProvider, which hands a circuit the
    // principal it was created with and then never revisits it. Registered only inside this
    // block: with authorization off there is no identity to revalidate in the first place.
    builder.Services.AddScoped<
        AuthenticationStateProvider,
        SessionRevalidatingAuthenticationStateProvider
    >();

    // The two endpoint-level policies this app has: one gating the API, one gating the Hangfire
    // dashboard. Both live here rather than next to what they protect, because a policy has to be
    // registered on the service collection before the container exists.
    builder.Services.AddAuthorization(options =>
    {
        // The authentication gate for the gRPC and JSON-transcoded REST API, applied to the
        // service endpoints further down. Every RPC already resolves the caller's namespace
        // permissions in the app-service layer (see PseudonymAppService), and an anonymous
        // principal holds no grants, so a tokenless call is refused there too - but it is refused
        // as PermissionDenied, after the request has been parsed and dispatched, and only for as
        // long as every present and future entry point remembers to ask. This makes it a
        // pipeline-level Unauthenticated that no service method can forget.
        //
        // Pinned to the bearer scheme rather than left on the "smart" default above, which is what
        // makes the refusal a usable one: with no Authorization header that selector forwards to
        // the cookie scheme, whose challenge is a 302 to the login page. A browser redirect is not
        // an answer a gRPC client can interpret - grpc-dotnet reports it as "Bad gRPC response.
        // HTTP status code: 302" - whereas the bearer challenge is a 401, which grpc-dotnet maps
        // to StatusCode.Unauthenticated. It also means a browser session no longer authenticates
        // an API call at all: with authorization enabled, Swagger UI's "Try it out" needs a bearer
        // token like any other client, rather than riding on the admin's login cookie.
        //
        // Pinned to ApiBearerScheme rather than to JwtBearer itself, so that a vfps-issued access
        // token is an equally valid API credential: that policy scheme forwards to whichever of
        // the two bearer handlers owns the token presented, and to JWT bearer - hence the 401 -
        // when there is none.
        options.AddPolicy(
            ApiAuthorizationPolicy,
            policy => policy.AddAuthenticationSchemes(ApiBearerScheme).RequireAuthenticatedUser()
        );

        // Gates the Hangfire dashboard (mapped further down, wherever Hangfire itself is enabled)
        // behind an admin role rather than merely a login. The dashboard lists every job's
        // arguments and parameters - for a CSV job that means the uploaded file's own name, the S3
        // object keys it reads and writes, and any failure detail - so "is signed in" is too low a
        // bar: an account holding no namespace grants at all would otherwise read all of it.
        //
        // RequireAuthenticatedUser() stays alongside the admin check because the two do different
        // jobs here: it is what turns an *unauthenticated* request into a real challenge/redirect
        // into the OIDC login flow via ASP.NET Core's own authorization middleware, instead of the
        // bare "Unauthorized" page Hangfire's own IDashboardAuthorizationFilter mechanism would
        // render. An authenticated non-admin then gets a 403 from that same middleware.
        //
        // The admin test goes through INamespacePermissionChecker rather than a RequireRole over
        // the configured admin role names, so it stays the one check used everywhere else in the
        // app - the one that already knows about Authorization:RoleClaimType. It is resolved per
        // request from the endpoint's own HttpContext (which is what
        // AuthorizationHandlerContext.Resource is for an endpoint-routed request), since this
        // policy has to be registered here, before the container that singleton lives in exists.
        // Anything other than an HttpContext resource fails the assertion rather than skipping it -
        // the dashboard is not a thing to fail open.
        options.AddPolicy(
            "HangfireDashboard",
            policy =>
                policy
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context =>
                        context.Resource is HttpContext httpContext
                        && httpContext
                            .RequestServices.GetRequiredService<INamespacePermissionChecker>()
                            .IsAdmin(context.User)
                    )
        );
    });
}

// Blazor Server keeps one process per replica but many circuits/cookies across replicas - the
// Data Protection key ring that encrypts auth cookies/antiforgery tokens needs to be shared so a
// request landing on a different replica than the one that issued the cookie can still decrypt it
// (relevant even with ingress session affinity, e.g. right after a scaling event). This must not
// be scoped to Authorization:IsEnabled - app.UseAntiforgery() below is unconditional (Blazor
// Server's own circuit handshake relies on it regardless of whether OIDC auth is on), so even an
// auth-disabled deployment needs a shared key ring across replicas, not just a single-process
// fallback. Persisted to Postgres (the same database vfps already depends on) rather than a
// separate Redis instance - sticky sessions themselves are an ingress-level concern, documented in
// the README, not implemented here.
builder.Services.Configure<DataProtectionConfig>(
    builder.Configuration.GetSection("DataProtection")
);
var dataProtectionConfig = new DataProtectionConfig();
builder.Configuration.GetSection("DataProtection").Bind(dataProtectionConfig);

var dataProtectionConnectionString = builder.Configuration.GetConnectionString("PostgreSQL");
if (!string.IsNullOrEmpty(dataProtectionConnectionString))
{
    builder.Services.AddDbContext<DataProtectionKeyContext>(options =>
        options.UseNpgsql(dataProtectionConnectionString, ConfigureNpgsqlResilience)
    );

    var dataProtectionBuilder = builder
        .Services.AddDataProtection()
        // Pinned rather than left to its default, which is derived from the content root path:
        // the application name is the isolation boundary for every protected payload, so an image
        // that ever changes where the app is unpacked would silently invalidate every cookie
        // minted before the move. A constant makes that impossible. Changing this value is itself
        // a one-time forced re-login, which is why it's a constant and not a setting.
        .SetApplicationName("vfps")
        .PersistKeysToDbContext<DataProtectionKeyContext>();

    // Throws rather than degrading to an unencrypted ring - see DataProtectionCertificateLoader.
    var keyProtectionCertificates = DataProtectionCertificateLoader.Load(
        dataProtectionConfig.Certificates
    );

    if (keyProtectionCertificates.Count > 0)
    {
        dataProtectionBuilder
            .ProtectKeysWithCertificate(keyProtectionCertificates[0])
            // Without this, decryption resolves certificates by thumbprint out of the platform
            // certificate store - which a certificate loaded from a mounted file is not in, so
            // every restart would fail to read back the ring it just wrote. Passing them
            // explicitly is also what lets more than one be accepted, which is the whole rotation
            // story in DataProtectionConfig.Certificates.
            .UnprotectKeysWithAnyCertificate([.. keyProtectionCertificates]);
    }
}

// CSV pseudonymization jobs: off by default, matching this codebase's optional-feature idiom.
// Input/output files live in S3-compatible object storage - see Config/S3Config.cs.
builder.Services.Configure<S3Config>(builder.Configuration.GetSection("S3"));
var s3Config = new S3Config();
builder.Configuration.GetSection("S3").Bind(s3Config);

builder.Services.Configure<CsvProcessingConfig>(builder.Configuration.GetSection("CsvProcessing"));
var csvProcessingConfig = new CsvProcessingConfig();
builder.Configuration.GetSection("CsvProcessing").Bind(csvProcessingConfig);

if (s3Config.IsEnabled)
{
    builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
        s3Config.AccessKey,
        s3Config.SecretKey,
        new AmazonS3Config
        {
            ServiceURL = s3Config.ServiceUrl,
            ForcePathStyle = s3Config.ForcePathStyle,
            // Not just cosmetic even against a non-AWS endpoint: the region is part of the
            // SigV4 credential scope embedded in every signed request/presigned URL, independent
            // of ServiceURL. Previously never set here at all, so every deployment silently
            // signed as "us-east-1" (the SDK's own fallback) regardless of this setting.
            //
            // Must be AuthenticationRegion (a plain string), not RegionEndpoint: the two are
            // mutually exclusive on AmazonS3Config, and whichever is assigned *last* wins -
            // setting RegionEndpoint here previously clobbered ServiceURL back to null, silently
            // redirecting every request to the real AWS endpoint for that region instead of this
            // configured S3-compatible one. That surfaced as "The AWS Access Key Id you provided
            // does not exist in our records" - a real error, just from the wrong server, since AWS
            // itself has no record of a MinIO/Ceph-issued key.
            AuthenticationRegion = s3Config.Region,
            // AWSSDK doesn't infer the scheme from ServiceURL for presigned URLs - without this,
            // a plain-HTTP endpoint (e.g. local MinIO) still gets signed as "https://", which the
            // browser then fails to load against a server not actually listening for TLS there.
            UseHttp = s3Config.ServiceUrl.StartsWith("http://", StringComparison.Ordinal),
            // AWSSDK v4's default (WHEN_SUPPORTED) calculates a newer flexible checksum instead
            // of the classic Content-MD5 header for some operations, including
            // PutBucketLifecycleConfiguration - MinIO (and likely other S3-compatible stores)
            // rejects that request with "Missing required header for this request: Content-Md5".
            // WHEN_REQUIRED falls back to the legacy MD5 behavior those operations still expect.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        }
    ));

    builder.Services.AddScoped<IPseudonymizationJobRepository, PseudonymizationJobRepository>();
    builder.Services.AddScoped<IPseudonymizationJobAppService, PseudonymizationJobAppService>();
    builder.Services.AddScoped<ICsvPseudonymizationJobRunner, CsvPseudonymizationJobRunner>();

    // One processor per job direction, behind the runner that dispatches to them. Scoped like the
    // runner itself, so each Hangfire job execution gets its own set within its own DI scope.
    builder.Services.AddScoped<CsvJobOutputUploader>();
    builder.Services.AddScoped<ICsvColumnTransformer, CsvColumnTransformer>();
    builder.Services.AddScoped<ICsvNamespaceImporter, CsvNamespaceImporter>();
    builder.Services.AddScoped<ICsvNamespaceExporter, CsvNamespaceExporter>();
    builder.Services.AddHostedService<S3BucketConfigurationBackgroundService>();
    builder.Services.AddHostedService<StalledPseudonymizationJobWatchdogService>();

    // Hangfire itself is registered further down for every deployment, not just this one - but CSV
    // jobs are the only feature that *cannot* work without it, so the missing-connection-string
    // failure is raised here, where it can name the setting that pulled it in.
    if (string.IsNullOrEmpty(builder.Configuration.GetConnectionString("PostgreSQL")))
    {
        throw new InvalidOperationException(
            "S3:IsEnabled requires ConnectionStrings:PostgreSQL to also be set - Hangfire reuses "
                + "the same database, no separate storage is provisioned for it."
        );
    }
}

// Hangfire is registered whenever a PostgreSQL connection string is available, independent of any
// individual feature that happens to use it: CSV processing (optional, see S3:IsEnabled) is no
// longer its only consumer now that the pseudonym-count metric is a recurring job too, and gating
// shared infrastructure on one of its consumers is what previously made "is the dashboard mapped?"
// depend on an unrelated setting. The connection string is the honest gate - it's the one thing
// Hangfire genuinely cannot start without - and it's absent only where there's no PostgreSQL at
// all, i.e. the integration tests, which swap in SQLite.
var hangfireConnectionString = builder.Configuration.GetConnectionString("PostgreSQL");
var isHangfireEnabled = !string.IsNullOrEmpty(hangfireConnectionString);

if (isHangfireEnabled)
{
    // Hangfire's client and storage are registered on every instance, whether or not it processes
    // jobs: enqueueing a job (IBackgroundJobClient) and rendering the dashboard both read from
    // storage and need no local server.
    //
    // Hangfire's Postgres storage handles distributed locking itself, so every replica that does
    // run a server can pick up jobs with no extra coordination work needed - consistent with
    // "no new service" for this feature.
    builder.Services.AddHangfire(config =>
        config
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(hangfireConnectionString),
                new PostgreSqlStorageOptions
                {
                    SchemaName = DatabaseMigrator.HangfireSchemaName,

                    // Off wherever the migrations Job owns the schema, which is every deployment
                    // that isn't development or ForceRunDatabaseMigrations. Left at the package
                    // default of true, this installer runs in every replica's
                    // PostgreSqlStorage constructor, and Install.sql serializes nothing: replicas
                    // starting together run the same DDL concurrently and hit
                    // `XX000: could not find tuple for constraint NNNNN`. The chart's
                    // wait-for-migrations-job init container makes that worse rather than better,
                    // since it releases every pod at the same instant. See
                    // DatabaseMigrator.InstallHangfireSchema, which is what runs it instead.
                    PrepareSchemaIfNecessary = shouldRunDatabaseMigrations,

                    // Hangfire.PostgreSql tracks "who is working on this job" via a fetchedat
                    // timestamp on the queue row, not a held lock - its dequeue query picks up any
                    // row whose fetchedat is older than InvisibilityTimeout (left at the package's
                    // own 30-minute default here), regardless of whether the worker that fetched
                    // it is still healthily running. With sliding off (also the package's
                    // default), fetchedat is stamped once and never renewed, so a CSV job that
                    // legitimately runs past 30 minutes gets its queue row handed to a second
                    // worker while the first is still processing it - not a crash, not a real
                    // server shutdown, just Hangfire deciding the first attempt looks abandoned.
                    // The two then race forever: each fresh attempt re-reads the whole file from
                    // byte zero (nothing about progress survives across a RunAsync call), so on a
                    // large enough import every attempt again takes longer than 30 minutes and
                    // gets reassigned again before it can finish.
                    //
                    // Sliding fixes this at the source: while true, a background heartbeat
                    // re-stamps fetchedat every InvisibilityTimeout/5 (6 minutes here) for as long
                    // as the fetch is genuinely still held, so a healthy job of any length is
                    // never mistaken for abandoned - a worker that actually crashed (and so stops
                    // heartbeating) is still reclaimed after the same 30 minutes as before. See
                    // CsvPseudonymizationJobRunner.RunAsync's JobAbortedException handling for
                    // what happens on the (now rare) occasions a fetch is genuinely lost instead.
                    UseSlidingInvisibilityTimeout = true,
                }
            )
            // CsvPseudonymizationJobRunner already handles its own failures (marks the job
            // Failed with a sanitized message, logs the full exception server-side) - Hangfire's
            // default of 10 automatic retries would silently re-run the whole job (tying up a
            // worker slot each time) even though most failures here are deterministic (a bad
            // column name, malformed input) and will just fail again identically. The metrics
            // recompute relies on this too: its retry is simply the next scheduled tick.
            .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    );

    // Which queues this instance serves is what preserves CsvProcessing:ProcessJobs now that every
    // instance runs a server. The metrics queue is served everywhere - the recurring job is
    // enqueued once per interval regardless of how many servers listen, so "every replica can pick
    // it up" costs nothing and means a deployment of nothing but ProcessJobs=false pods still gets
    // its counts recomputed. The default queue, where CSV jobs land, stays opt-in.
    var servedQueues = new List<string> { HangfireQueues.Metrics };
    if (s3Config.IsEnabled && csvProcessingConfig.ProcessJobs)
    {
        // First in the list is first served: a long CSV job shouldn't wait behind a metrics tick.
        servedQueues.Insert(0, HangfireQueues.Default);
    }

    builder.Services.AddHangfireServer(hangfireOptions =>
    {
        hangfireOptions.Queues = [.. servedQueues];

        // Every one of these is pinned rather than left at a Hangfire default, because the
        // defaults are tuned for short jobs on a single server and this app runs long CSV
        // jobs across a horizontally-scaled Deployment sharing one database. See
        // CsvProcessingConfig for the reasoning behind each value.
        hangfireOptions.WorkerCount = Math.Max(1, csvProcessingConfig.WorkerCount);
        hangfireOptions.ShutdownTimeout = csvProcessingConfig.JobServerShutdownTimeout;

        // Derived, not separately configurable: a heartbeat slower than the timeout it's
        // checked against would have healthy servers declare each other dead and
        // double-process jobs, so these three only make sense set together. The floor guards
        // the same invariant against a recovery delay configured so low that the derived
        // heartbeat can't keep up with it.
        var recoveryDelay =
            csvProcessingConfig.OrphanedJobRecoveryDelay < TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : csvProcessingConfig.OrphanedJobRecoveryDelay;

        hangfireOptions.ServerTimeout = recoveryDelay;
        hangfireOptions.ServerCheckInterval = recoveryDelay / 4;
        hangfireOptions.HeartbeatInterval = recoveryDelay / 8;
    });
}

builder.Services.AddControllers(options =>
{
    options.InputFormatters.Insert(0, new FhirInputFormatter());
    options.OutputFormatters.Insert(0, new FhirOutputFormatter());
});

// Metrics are always exported (a pull-based /metrics endpoint costs nothing when nobody scrapes
// it), matching this codebase's other unconditional infrastructure. Tracing stays opt-in below:
// pushing spans to a collector nobody deployed would be per-request overhead for nothing.
builder.AddMetrics();

// Tracing
var isTracingEnabled = builder.Configuration.GetValue("Tracing:IsEnabled", false);
if (isTracingEnabled)
{
    builder.AddTracing();
}

var app = builder.Build();

// The bundled Helm chart's ingress (Traefik) terminates TLS and forwards plain HTTP to this pod,
// so without this, Kestrel - and therefore the OIDC handler building redirect_uri - only ever
// sees "http" as the request scheme, producing an http:// redirect_uri that the IdP correctly
// rejects since the client is registered for https://. Must run before anything that reads the
// request scheme/host (UsePathBase, authentication, and any HTTPS redirection). KnownNetworks/
// KnownProxies are cleared because the actual proxy is a Kubernetes ingress pod, not the loopback
// address ASP.NET Core trusts by default - only cluster-internal traffic can reach this pod
// directly, so trusting the header unconditionally here doesn't extend that trust to the public
// internet.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Keeps /metrics off the app's public-facing ports, and everything else off the metrics port.
// The second half is the one with teeth: ASP.NET Core routing is indifferent to which Kestrel
// listener accepted a connection, so without this the admin UI, the Hangfire dashboard, the
// REST/FHIR API and the gRPC services are all served on the metrics port too - and a deployment
// that scopes that port more loosely than the API ports (the bundled chart's NetworkPolicy does)
// would be publishing the whole pseudonymization API through it. See MetricsPortGuard for the
// decision itself, kept there as a pure function so it can be unit-tested.
//
// Placed ahead of UsePathBase/routing so a rejected request never reaches an endpoint at all.
app.Use(
    async (context, next) =>
    {
        if (
            MetricsPortGuard.ShouldReject(
                context.Request.Path,
                context.Connection.LocalPort,
                metricsPort
            )
        )
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
);

app.UseRequestLocalization();

if (s3Config.IsEnabled && !csvProcessingConfig.ProcessJobs)
{
    // Deliberately noisy: "jobs sit in Queued forever" is an confusing symptom to diagnose, and
    // this line is what distinguishes an instance that is not meant to process jobs (the normal
    // setup for API pods alongside a dedicated worker Deployment) from a deployment where nothing
    // processes them at all.
    app.Logger.LogInformation(
        "CsvProcessing:ProcessJobs is false - this instance accepts CSV jobs but does not process "
            + "them. At least one instance must have it enabled, or jobs will queue forever."
    );
}

if (!authConfig.IsEnabled)
{
    app.Logger.LogWarning(
        "Authorization is disabled (Authorization:IsEnabled=false). The API and admin UI are "
            + "reachable without authentication and every namespace is fully accessible. This is "
            + "not recommended for deployments handling real data."
    );
}

if (
    !string.IsNullOrEmpty(dataProtectionConnectionString)
    && dataProtectionConfig.Certificates.Count == 0
)
{
    app.Logger.LogWarning(
        "The Data Protection key ring is persisted to PostgreSQL unencrypted "
            + "(DataProtection:Certificates is empty). The keys that protect this deployment's "
            + "auth cookies and antiforgery tokens are stored as plaintext in the same database "
            + "as the pseudonyms, so anyone who can read that database can forge a session for "
            + "any user, including an admin. Configure a certificate to encrypt the key ring at "
            + "rest - see the Data Protection section of the README."
    );
}

app.UsePathBase("/ui");
app.UseRouting();

// Configure the HTTP request pipeline.
var pseudonymEndpoints = app.MapGrpcService<PseudonymService>();
var namespaceEndpoints = app.MapGrpcService<NamespaceService>();

if (authConfig.IsEnabled)
{
    // Applied to the gRPC services rather than globally, and only when authorization is on at
    // all: with it off the policy is never registered, and requiring a policy by a name nothing
    // added throws per request rather than at startup. JSON transcoding hangs the RESTful routes
    // off these same endpoint builders, so the /v1/... surface is covered by the same call - see
    // the policy's own comment for why it is bearer-only and what that gate is for.
    pseudonymEndpoints.RequireAuthorization(ApiAuthorizationPolicy);
    namespaceEndpoints.RequireAuthorization(ApiAuthorizationPolicy);
}

// Left anonymous on purpose, like the /healthz endpoints below: a kubelet probe and a gRPC
// health-checking load balancer carry no token, and the response says only whether the process is
// serving.
app.MapGrpcHealthChecksService();
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "VFPS API v1"));

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

if (authConfig.IsEnabled)
{
    // Standard ASP.NET Core Blazor Web App + OIDC login/logout pattern: Blazor components
    // can't issue the redirect-based OIDC challenge themselves, so a plain link to these
    // endpoints (not Blazor-routed navigation) triggers the actual sign-in/sign-out flow.
    app.MapGet(
            "/authentication/login",
            (string? returnUrl) =>
                Results.Challenge(
                    // Never the raw query value: this endpoint is anonymous, and RedirectUri is
                    // what ASP.NET Core's remote-authentication handler redirects to once sign-in
                    // completes - verbatim, with no local-URL check of its own. See ReturnUrl,
                    // which also keeps the empty-value case covered: an empty (not just null)
                    // returnUrl is what NavigationManager.ToBaseRelativePath produces for a page
                    // outside the "/ui" base (e.g. a bare "/" request), and an empty RedirectUri
                    // never completes OIDC sign-in, causing an infinite login/redirect loop.
                    new AuthenticationProperties
                    {
                        RedirectUri = ReturnUrl.Resolve(returnUrl),
                    },
                    [OpenIdConnectDefaults.AuthenticationScheme]
                )
        )
        .AllowAnonymous();

    app.MapPost(
        "/authentication/logout",
        async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = "/ui" },
                [OpenIdConnectDefaults.AuthenticationScheme]
            );
        }
    );
}

// Backs MainLayout's language switcher - Blazor components can't set the request culture cookie
// themselves, so a plain link to this endpoint (not Blazor-routed navigation) sets it and redirects
// back to the page the user was on.
app.MapGet(
    "/culture/set",
    (string culture, string? redirectUri, HttpContext ctx) =>
    {
        var resolvedCulture = supportedCultures.Contains(culture) ? culture : supportedCultures[0];

        ctx.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(resolvedCulture)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                IsEssential = true,
                HttpOnly = true,
                // Not hardcoded true: this app also runs over plain HTTP in local dev, where a
                // hardcoded Secure flag would make the browser silently drop the cookie.
                Secure = ctx.Request.IsHttps,
            }
        );

        // LocalRedirect already refuses to send the browser off-site, but it does so by throwing
        // - which would turn a doctored link into a 500 rather than a language switch. Resolving
        // first keeps the same guarantee and answers the way every other bad input here does.
        return Results.LocalRedirect(ReturnUrl.Resolve(redirectUri));
    }
);

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapHealthChecks("/healthz");
app.MapHealthChecks(
    "/readyz",
    new HealthCheckOptions
    {
        // there's currently no readiness probes depending on external state,
        // but in case we ever add one, this prepares the code for it.
        // see https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-7.0#separate-readiness-and-liveness-probes
        Predicate = healthCheck => healthCheck.Tags.Contains("ready"),
    }
);

app.MapHealthChecks("/livez", new HealthCheckOptions { Predicate = _ => false });

app.MapPrometheusScrapingEndpoint(MetricsPortGuard.MetricsPath);

if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

if (isHangfireEnabled)
{
    // Gated on the same condition as the AddHangfire/AddHangfireServer block above rather than on
    // any one feature - mapping the dashboard without those services registered throws "Unable to
    // find the required services" on every request.
    var permissionChecker = app.Services.GetRequiredService<INamespacePermissionChecker>();
    var dashboardOptions = new DashboardOptions
    {
        // Same admin-role check used everywhere else in the app (falls back to "everyone is
        // admin" when Authorization:IsEnabled=false, matching the rest of the app's
        // off-by-default, fully-open behavior) - rather than a hardcoded "admin" role name that
        // doesn't match Authorization:AdminRoles/RoleClaimType.
        //
        // Belt and braces now that the HangfireDashboard policy above admits admins only: on the
        // authorized path this can no longer evaluate to anything but false. Kept so that
        // loosening that policy can never silently hand a non-admin write access to the
        // dashboard's job controls as well as sight of it.
        IsReadOnlyFunc = dashboardContext =>
            !permissionChecker.IsAdmin(dashboardContext.GetHttpContext().User),
    };

    if (authConfig.IsEnabled)
    {
        app.MapHangfireDashboardWithAuthorizationPolicy(
            "HangfireDashboard",
            "/hangfire",
            dashboardOptions
        );
    }
    else
    {
        app.MapHangfireDashboardWithNoAuthorizationFilters("/hangfire", dashboardOptions);
    }

    // The per-namespace pseudonym count, recomputed on a schedule for the whole deployment rather
    // than by each replica for itself - see PseudonymCountMetrics for why it's a shared snapshot at
    // all. Hangfire owning the schedule is the entire point: a recurring job is dispatched to one
    // server per tick, so "exactly one replica pays for this query" needs no lease, lock or
    // leader election of the app's own.
    //
    // AddOrUpdate is idempotent and keyed by the job id, so every replica running this at startup
    // converges on one recurring job rather than creating N of them - and an id that changes would
    // orphan the old entry in the dashboard, so it's a constant, not something derived.
    app.Services.GetRequiredService<IRecurringJobManager>()
        .AddOrUpdate<PseudonymCountMetrics>(
            "pseudonym-count-metrics",
            HangfireQueues.Metrics,
            metrics => metrics.RecomputeAsync(CancellationToken.None),
            // Hangfire substitutes a real token tied to job cancellation for CancellationToken.None
            // above; the literal is just how the expression tree names the parameter.
            Cron.MinuteInterval(PseudonymCountRecomputeIntervalMinutes)
        );
}

var controllerEndpoints = app.MapControllers();

if (authConfig.IsEnabled)
{
    // FhirController is the same API under a different content type - $create-pseudonym reaches the
    // same PseudonymAppService.CreateAsync the gRPC PseudonymService.Create does - so it gets the
    // same gate rather than being left as the one unauthenticated way in.
    //
    // Applied to MapControllers rather than as an [Authorize] attribute on the controller for the
    // same reason the gRPC services are gated here: the policy exists only when authorization is
    // enabled, and an attribute naming a policy nothing registered throws per request instead of
    // at startup. That it also covers any controller added later is the intended default for a
    // service whose whole HTTP surface is the API - a deliberately public one opts out with
    // [AllowAnonymous], which the authorization middleware honours over this.
    //
    // One wrinkle worth knowing before reading a trace: the 401 comes from the pipeline, so it
    // carries no body. Every *authenticated* refusal this controller makes is still a FHIR
    // OperationOutcome, but "no token at all" is answered before MVC is reached.
    controllerEndpoints.RequireAuthorization(ApiAuthorizationPolicy);
}

if (shouldRunDatabaseMigrations)
{
    // only ran in a development setup or when forced.
    // See https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying?tabs=dotnet-core-cli>
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<PseudonymContext>();
    db.Database.Migrate();

    // Only registered when a PostgreSQL connection string is set - see the
    // AddDbContext<DataProtectionKeyContext> call above.
    var dataProtectionDb = scope.ServiceProvider.GetService<DataProtectionKeyContext>();
    dataProtectionDb?.Database.Migrate();
}

app.Run();

// Explicit because the `migrate` path above returns an exit code, which makes this entry point
// int-returning.
return 0;

public partial class Program
{
    /// <summary>
    /// How often the per-namespace pseudonym count is recomputed, in minutes. Matches the interval
    /// this metric used before it became a Hangfire recurring job. Infrequent on purpose: it's a
    /// full-scan-class GROUP BY over the largest table in the schema, and every replica reads the
    /// stored result on a far shorter timer regardless (see PseudonymCountMetricsBackgroundService),
    /// so a longer interval here costs freshness but never consistency.
    /// </summary>
    internal const int PseudonymCountRecomputeIntervalMinutes = 5;

    /// <summary>
    /// Name of the authorization policy gating the gRPC and JSON-transcoded REST API. Registered,
    /// and applied to those endpoints, only when Authorization:IsEnabled is true.
    /// </summary>
    internal const string ApiAuthorizationPolicy = "Api";

    /// <summary>
    /// Name of the policy scheme that picks the bearer handler a presented credential belongs to
    /// - the identity provider's JWTs, or vfps's own access tokens. Registered, like
    /// <see cref="ApiAuthorizationPolicy"/> itself, only when Authorization:IsEnabled is true.
    /// </summary>
    internal const string ApiBearerScheme = "ApiBearer";

    internal static readonly ActivitySource ActivitySource = new("Vfps");

    internal static readonly Meter Meter = new("Vfps");

    protected Program() { }
}
