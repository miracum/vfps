FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.302-resolute@sha256:c43f711c3ba4e621fe8570660f16abb31bbffda372fd2690eb917a85162a4281 AS build
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

FROM build AS stress-test
ARG TARGETARCH
# renovate: datasource=github-releases depName=bojand/ghz
ARG GHZ_VERSION=0.121.0
WORKDIR /opt/vfps-stress
# https://github.com/hadolint/hadolint/pull/815 isn't yet in mega-linter
# hadolint ignore=DL3022
COPY --from=registry.k8s.io/kubectl:v1.36.3@sha256:6e4fce3c83651edb91b74bc67701c5cd263dd8aa3cd4254b1798d6425a5ab789 /bin/kubectl /usr/bin/kubectl
# hadolint ignore=DL3022
COPY --from=ghcr.io/jqlang/jq:1.8.1@sha256:4f34c6d23f4b1372ac789752cc955dc67c2ae177eb1b5860b75cdc5091ce6f91 /jq /usr/bin/jq

# ghz (https://ghz.sh) replaced NBomber as the gRPC load generator - it's a single static
# binary rather than a .NET test project, so it's downloaded and checksum-verified here
# instead of built like the rest of this Dockerfile.
RUN <<EOF
set -eu
case "${TARGETARCH}" in
  amd64) GHZ_ARCH="x86_64"; GHZ_SHA256="9ae3b93f2c46dac9136e29e81b4a1de8d4e56f092a6fe46884a25c9c83cb2324" ;;
  arm64) GHZ_ARCH="arm64"; GHZ_SHA256="02a40abcfc10b98eab5b693511abe044f5a3410d77bcc86c9a3a0eeb615eb77a" ;;
  *) echo "unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;;
esac
curl -fsSL -o /tmp/ghz.tar.gz "https://github.com/bojand/ghz/releases/download/v${GHZ_VERSION}/ghz-linux-${GHZ_ARCH}.tar.gz"
echo "${GHZ_SHA256}  /tmp/ghz.tar.gz" | sha256sum -c -
tar -xzf /tmp/ghz.tar.gz -C /usr/bin ghz
rm /tmp/ghz.tar.gz
EOF

COPY tests/chaos/chaos.yaml /tmp/
COPY tests/stress/run.sh .
COPY src/Vfps/Protos/ ./Protos/
# currently running into <https://github.com/dotnet/runtime/issues/80619>
# when running as non-root.

# hadolint ignore=DL3002
USER 0:0
ENTRYPOINT ["/opt/vfps-stress/run.sh"]

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0.10-resolute-chiseled-extra@sha256:4762b78e42e22a325e4a6492a5ac5dd55449ba744088842a1a1ea239d16e1027 AS runtime
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
