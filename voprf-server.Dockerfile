FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400-resolute@sha256:e9d9e903cc6eb4049f3c07d8a86ffdccdd0511a91c3d459c4c4e68d1dab91adf AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-resolute-chiseled-extra@sha256:0e8d291426c277e5b53bb99f3fa6d95c3b02eff20f7ca1d807a7608250164df3 AS runtime
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
