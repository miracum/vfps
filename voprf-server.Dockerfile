FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-resolute@sha256:4bd809877fc795924d30c686774a3c2136710f0e923f15224c5fbb70a09cfb2f AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_ENVIRONMENT="Production" \
    DOTNET_ENVIRONMENT="Production"

COPY src/Directory.Build.props src/
COPY src/Vfps.Voprf/Vfps.Voprf.csproj src/Vfps.Voprf/
COPY src/Vfps.Voprf/packages.lock.json src/Vfps.Voprf/
COPY src/Vfps.Voprf.Contracts/Vfps.Voprf.Contracts.csproj src/Vfps.Voprf.Contracts/
COPY src/Vfps.Voprf.Contracts/packages.lock.json src/Vfps.Voprf.Contracts/
COPY src/Vfps.Voprf.Server/Vfps.Voprf.Server.csproj src/Vfps.Voprf.Server/
COPY src/Vfps.Voprf.Server/packages.lock.json src/Vfps.Voprf.Server/

RUN dotnet restore --locked-mode src/Vfps.Voprf.Server/Vfps.Voprf.Server.csproj

COPY . .

RUN <<EOT
dotnet build src/Vfps.Voprf.Server/Vfps.Voprf.Server.csproj \
    --no-restore \
    --runtime=linux-x64 \
    --no-self-contained \
    --configuration=Release

dotnet publish src/Vfps.Voprf.Server/Vfps.Voprf.Server.csproj \
    --no-restore \
    --no-build \
    --runtime=linux-x64 \
    --no-self-contained \
    --configuration=Release \
    -o /build/publish
EOT

FROM build AS build-test
WORKDIR /build/src/Vfps.Voprf.Server.Tests
RUN dotnet test \
    --configuration=Release \
    --results-directory=./coverage \
    -- --coverage \
    --coverage-output-format cobertura \
    --coverage-output coverage.cobertura.xml

FROM scratch AS test
WORKDIR /build/src/Vfps.Voprf.Server.Tests/coverage
COPY --from=build-test /build/src/Vfps.Voprf.Server.Tests/coverage .
ENTRYPOINT [ "true" ]

# libsodium ships as a NuGet native asset and links only against libc, so the chiseled runtime
# image needs nothing added to it - see src/Vfps.Voprf/README.md.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-resolute-chiseled-extra@sha256:5b5936af84ee5564e5b2e3868f9c1830a3c5529c4bd70baeffb67343ccc0bb82 AS runtime
WORKDIR /opt/vfps-voprf
EXPOSE 8081/tcp
# non-root, and nothing here ever writes to disk: the image runs fine with a read-only root
# filesystem. The key is mounted read-only and read once at startup.
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    ASPNETCORE_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS="" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
COPY --from=build /build/publish .
CMD ["/opt/vfps-voprf/Vfps.Voprf.Server.dll"]
