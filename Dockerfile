FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.201-noble@sha256:478b9038d187e5b5c29bfa8173ded5d29e864b5ad06102a12106380ee01e2e49 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="/root/.dotnet/tools:${PATH}"

RUN dotnet tool install --global dotnet-ef --version=10.0.5

COPY src/Directory.Build.props src/
COPY src/Vfps/Vfps.csproj src/Vfps/

RUN dotnet restore --runtime=linux-x64 src/Vfps/Vfps.csproj

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
# no Redis/Postgres/MinIO actually reachable in this build sandbox - previously crashed the whole
# design-time host before EF could discover the DbContext. Bundled migrations run in Production
# anyway, so building them under that same environment is also just the more correct choice.
ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production dotnet ef migrations bundle \
    --project=src/Vfps/Vfps.csproj \
    --startup-project=src/Vfps/Vfps.csproj \
    --configuration=Release \
    --verbose \
    -o /build/efbundle
EOF

FROM build AS build-test
WORKDIR /build/src/Vfps.Tests
RUN dotnet test \
    --configuration=Release \
    --collect:"XPlat Code Coverage" \
    --results-directory=./coverage \
    -l "console;verbosity=detailed" \
    --settings=runsettings.xml

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
COPY --from=registry.k8s.io/kubectl:v1.33.1 /bin/kubectl /usr/bin/kubectl

COPY tests/chaos/chaos.yaml /tmp/
COPY --from=build-stress-test /build/publish .
# currently running into <https://github.com/dotnet/runtime/issues/80619>
# when running as non-root.

# hadolint ignore=DL3002
USER 0:0
ENTRYPOINT ["dotnet"]
CMD ["test", "/opt/vfps-stress/Vfps.StressTests.dll", "-l", "console;verbosity=detailed"]

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0.5-noble-chiseled@sha256:1191b4891ae8b1a8184b2de52b2c6332dfb27c30b58d282632044357db63761d AS runtime
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
CMD ["/opt/vfps/Vfps.dll"]
