using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Vfps.Voprf.Server.Config;
using Vfps.Voprf.Server.Keys;
using Vfps.Voprf.Server.Security;
using Vfps.Voprf.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Curated configuration object rather than binding straight onto framework options: what this
// server exposes as settings is a deliberate surface, and the individual handlers below are
// wired from it explicitly.
builder.Services.Configure<VoprfServerConfig>(
    builder.Configuration.GetSection(VoprfServerConfig.SectionName)
);
var config = new VoprfServerConfig();
builder.Configuration.GetSection(VoprfServerConfig.SectionName).Bind(config);

// Before anything is constructed, and before Kestrel binds a port. A configuration that would
// serve the key to anyone must not get as far as listening while somebody reads the log.
var configurationErrors = HardeningGuard.Validate(config, builder.Environment.IsDevelopment());
if (configurationErrors.Count > 0)
{
    foreach (var error in configurationErrors)
    {
        Console.Error.WriteLine($"configuration error: {error}");
    }

    return 1;
}

var hardened = config.Hardening.IsEnabled;

// Authorization policy gating BlindEvaluate.
const string EvaluatePolicy = "voprf:evaluate";

builder.Services.AddSingleton<IVoprfKeyProvider, VoprfKeyProvider>();

builder.Services.AddGrpc(grpc =>
{
    grpc.EnableDetailedErrors = !hardened || config.Hardening.EnableDetailedErrors;
    grpc.MaxReceiveMessageSize = config.Hardening.MaxReceiveMessageSizeBytes;

    // Nothing here streams, and a response is a few kilobytes at most.
    grpc.MaxSendMessageSize = config.Hardening.MaxReceiveMessageSizeBytes;
});
builder.Services.AddGrpcHealthChecks();
builder.Services.AddHealthChecks();

if (!hardened || config.Hardening.EnableReflection)
{
    builder.Services.AddGrpcReflection();
}

// ---- authentication ---------------------------------------------------------------------

var requireAuthentication = hardened && config.Hardening.RequireAuthentication;

if (requireAuthentication)
{
    switch (config.Authentication.Mode)
    {
        case AuthenticationMode.ClientCertificate:
            ConfigureClientCertificateAuthentication(
                builder,
                config.Authentication.ClientCertificate
            );
            break;

        case AuthenticationMode.Jwt:
            ConfigureJwtAuthentication(builder, config.Authentication.Jwt);
            break;

        default:
            throw new InvalidOperationException(
                $"Unknown authentication mode '{config.Authentication.Mode}'."
            );
    }

    builder
        .Services.AddAuthorizationBuilder()
        .AddPolicy(EvaluatePolicy, BuildPolicy(config.Authentication));
}
else
{
    builder.Services.AddAuthorization();
}

// ---- rate limiting ----------------------------------------------------------------------

if (hardened && config.Hardening.RateLimit.IsEnabled)
{
    var limits = config.Hardening.RateLimit;

    builder.Services.AddRateLimiter(rateLimiter =>
    {
        rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Partitioned by caller identity so that one client exhausting its budget does not
        // deny the service to the others. Unauthenticated callers - only possible with
        // hardening off, where the limiter is not installed at all - would share the remote
        // address partition.
        rateLimiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var partition =
                context.User.Identity?.Name
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter(
                partition,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitsPerWindow,
                    Window = limits.Window,
                    QueueLimit = limits.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }
            );
        });
    });
}

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Vfps.Voprf.Server");

if (!hardened)
{
    logger.LogWarning(
        "Hardening is DISABLED: TLS is not required, callers are not authenticated, and gRPC "
            + "reflection and detailed errors are on. This is a development configuration and the "
            + "server will refuse to start with it outside the Development environment."
    );
}

// Resolved now rather than on the first call. A singleton is otherwise constructed lazily, so
// an unreadable or malformed key would leave the process listening and reporting healthy until
// somebody used it - and then failing with an error the caller cannot be told the reason for.
try
{
    _ = app.Services.GetRequiredService<IVoprfKeyProvider>();
}
catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
{
    logger.LogCritical(exception, "Could not load the VOPRF key");
    return 1;
}

logger.LogInformation(
    "VOPRF server posture: hardening={Hardened}, tls={Tls}, authentication={Authentication}, "
        + "rateLimit={RateLimit}, reflection={Reflection}, maxBatch={MaxBatch}",
    hardened,
    hardened && config.Hardening.RequireTls ? "required" : "not required",
    requireAuthentication ? config.Authentication.Mode.ToString() : "none",
    hardened && config.Hardening.RateLimit.IsEnabled
        ? $"{config.Hardening.RateLimit.PermitsPerWindow}/{config.Hardening.RateLimit.Window}"
        : "off",
    !hardened || config.Hardening.EnableReflection ? "on" : "off",
    config.MaxBatchSize
);

// ---- pipeline ---------------------------------------------------------------------------

if (hardened && config.Hardening.RequireTls)
{
    // Checked per request rather than only at binding time: a deployment may put this behind a
    // proxy that terminates TLS, and what matters is the scheme of the hop that actually
    // arrived. Health checks are exempt so a plaintext liveness probe on the pod network can
    // still work.
    app.Use(
        async (context, next) =>
        {
            if (!context.Request.IsHttps && !IsHealthCheck(context))
            {
                logger.LogWarning(
                    "Rejected a plaintext request from {Peer}",
                    context.Connection.RemoteIpAddress
                );
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            await next(context);
        }
    );
}

app.UseRouting();

if (hardened && config.Hardening.RateLimit.IsEnabled)
{
    app.UseRateLimiter();
}

// Only when a scheme was actually registered: the authentication middleware resolves services
// that AddAuthentication puts in the container, and with authentication switched off for
// development nothing has registered them.
if (requireAuthentication)
{
    app.UseAuthentication();
}

app.UseAuthorization();

var evaluation = app.MapGrpcService<VoprfEvaluationService>();
if (requireAuthentication)
{
    // Named on the endpoint rather than left to a fallback policy: the protection is then
    // visible where the endpoint is declared, and it cannot be silently lost by anything else
    // that happens to attach authorization metadata.
    evaluation.RequireAuthorization(EvaluatePolicy);
}

// The health service is what a load balancer and Kubernetes probe, so it must answer without
// credentials - it reveals only that the process is up.
app.MapGrpcHealthChecksService().AllowAnonymous();
app.MapHealthChecks("/healthz").AllowAnonymous();

if (!hardened || config.Hardening.EnableReflection)
{
    app.MapGrpcReflectionService();
}

app.MapGet(
        "/",
        () =>
            Results.Text(
                "vfps VOPRF evaluation server. gRPC only; see Protos/vfps/voprf/v1/voprf.proto."
            )
    )
    .AllowAnonymous();

await app.RunAsync();
return 0;

static bool IsHealthCheck(HttpContext context) =>
    context.Request.Path.StartsWithSegments("/healthz")
    || context.Request.Path.StartsWithSegments("/grpc.health.v1.Health");

static AuthorizationPolicy BuildPolicy(AuthenticationConfig authentication)
{
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();

    if (
        authentication.Mode == AuthenticationMode.Jwt
        && !string.IsNullOrWhiteSpace(authentication.Jwt.RequiredScope)
    )
    {
        var required = authentication.Jwt.RequiredScope;

        // "scope" arrives as one space-delimited claim value, so a plain claim match would only
        // work for a token carrying exactly this one scope.
        policy.RequireAssertion(context =>
            context
                .User.FindAll("scope")
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(required, StringComparer.Ordinal)
        );
    }

    return policy.Build();
}

static void ConfigureClientCertificateAuthentication(
    WebApplicationBuilder builder,
    ClientCertificateConfig certificateConfig
)
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.CheckCertificateRevocation = certificateConfig.CheckRevocation;
        });
    });

    builder
        .Services.AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
        .AddCertificate(options =>
        {
            options.AllowedCertificateTypes = CertificateTypes.Chained;
            options.RevocationMode = certificateConfig.CheckRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck;

            options.Events = new CertificateAuthenticationEvents
            {
                OnCertificateValidated = context =>
                {
                    var allowed = certificateConfig.AllowedThumbprints;
                    if (allowed.Count > 0)
                    {
                        // Chaining to a trusted root only says the root issued it. Where the
                        // root issues certificates for anything else at all, that is not the
                        // same as "may use this key".
                        var thumbprint = context.ClientCertificate.GetCertHashString(
                            System.Security.Cryptography.HashAlgorithmName.SHA256
                        );

                        if (!allowed.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
                        {
                            context.Fail("Client certificate is not in the allow list.");
                            return Task.CompletedTask;
                        }
                    }

                    context.Principal = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            [
                                new Claim(
                                    ClaimTypes.Name,
                                    context.ClientCertificate.Subject,
                                    ClaimValueTypes.String,
                                    context.Options.ClaimsIssuer
                                ),
                            ],
                            context.Scheme.Name
                        )
                    );
                    context.Success();
                    return Task.CompletedTask;
                },
            };
        });
}

static void ConfigureJwtAuthentication(WebApplicationBuilder builder, JwtConfig jwt)
{
    builder
        .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = jwt.Authority;
            options.Audience = jwt.Audience;
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "preferred_username";

            // An empty Audience would otherwise disable audience validation entirely, which
            // makes any token from the issuer - including ones minted for other applications -
            // good enough to use this key.
            options.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(
                jwt.Audience
            );
        });
}
