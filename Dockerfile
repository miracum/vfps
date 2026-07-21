FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.302-resolute@sha256:45401dde65ffc706a65841120ffdf827805eefe16852d6de1086a876c421de2e AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="/root/.dotnet/tools:${PATH}" \
    ASPNETCORE_ENVIRONMENT="Production" \
    DOTNET_ENVIRONMENT="Production"

COPY .config/ .

RUN dotnet tool restore

COPY src/Directory.Build.props src/
COPY src/Vfps/Vfps.csproj src/Vfps/
COPY src/Vfps/packages.lock.json src/Vfps/

RUN dotnet restore --locked-mode src/Vfps/Vfps.csproj

COPY . .

RUN <<EOF
dotnet build src/Vfps/Vfps.csproj \
    --no-restore \
    --configuration=Release

dotnet publish src/Vfps/Vfps.csproj \
    --no-restore \
    --no-build \
    --configuration=Release \
    -o /build/publish

# dotnet-ef has no --environment flag - it only reads ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT,
# defaulting to "Development" (and thus appsettings.Development.json) when neither is set. That
# file enables Authorization/S3 by default for local `dotnet run`, and evaluating it here - with
# no Postgres/MinIO actually reachable in this build sandbox - previously crashed the whole
# design-time host before EF could discover the DbContext. Bundled migrations run in Production
# anyway, so building them under that same environment is also just the more correct choice.
#
# Two DbContexts now have migrations (PseudonymContext, DataProtectionKeyContext), and
# `migrations bundle` errors out ("More than one DbContext was found") without an explicit
# --context, so this produces one bundle executable per context.
# --runtime/--target-runtime pin this to the same linux-x64 RID as the restore/build/publish
# steps above - without it, dotnet-ef's own internal project evaluation resolves no RID at all,
# which conflicts with the RID-specific packages.lock.json section under RestoreLockedMode.
dotnet ef migrations bundle \
    --project=src/Vfps/Vfps.csproj \
    --startup-project=src/Vfps/Vfps.csproj \
    --context=PseudonymContext \
    --configuration=Release \
    --verbose \
    -o /build/efbundle

dotnet ef migrations bundle \
    --project=src/Vfps/Vfps.csproj \
    --startup-project=src/Vfps/Vfps.csproj \
    --context=DataProtectionKeyContext \
    --configuration=Release \
    --verbose \
    -o /build/efbundle-dataprotection
EOF

FROM build AS build-test
WORKDIR /build/src/Vfps.Tests
RUN dotnet test \
    --configuration=Release \
    --results-directory=./coverage \
    -- --coverage \
    --coverage-output-format cobertura \
    --coverage-output coverage.cobertura.xml \
    --coverage-settings codecoverage.config

FROM scratch AS test
WORKDIR /build/src/Vfps.Tests/coverage
COPY --from=build-test /build/src/Vfps.Tests/coverage .
ENTRYPOINT [ "true" ]

FROM build AS build-stress-test
WORKDIR /build/src/Vfps.StressTests
RUN <<EOF
dotnet build \
    --configuration=Release

dotnet publish \
    --no-restore \
    --no-build \
    --configuration=Release \
    -o /build/publish
EOF

FROM build AS stress-test
WORKDIR /opt/vfps-stress
# https://github.com/hadolint/hadolint/pull/815 isn't yet in mega-linter
# hadolint ignore=DL3022
COPY --from=registry.k8s.io/kubectl:v1.36.2@sha256:b0d792e0d8dfb9bb1b922b78b23137e2a34bb6f9667640353a9d2aadd1fd7761 /bin/kubectl /usr/bin/kubectl

COPY tests/chaos/chaos.yaml /tmp/
COPY --from=build-stress-test /build/publish .
# currently running into <https://github.com/dotnet/runtime/issues/80619>
# when running as non-root.

# hadolint ignore=DL3002
USER 0:0
ENTRYPOINT ["dotnet"]
CMD ["/opt/vfps-stress/Vfps.StressTests.dll", "-reporter", "verbose"]

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0.10-resolute-chiseled@sha256:22467e5e67c226c1acbc8783e15ebecb3b0c0d8176c6c73109a630b1f4d87e33 AS runtime
WORKDIR /opt/vfps
EXPOSE 8080/tcp 8081/tcp 8082/tcp
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS="" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
COPY --from=build /build/publish .
COPY --from=build /build/efbundle .
COPY --from=build /build/efbundle-dataprotection .
CMD ["/opt/vfps/Vfps.dll"]
