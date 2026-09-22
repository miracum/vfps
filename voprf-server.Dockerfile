# The -aot variant is the same SDK image with the PublishAot=true prerequisites (clang,
# zlib1g-dev - see https://aka.ms/nativeaot-prerequisites) already installed, so nothing has
# to be added here. The plain (non-aot) tag is what the sibling Dockerfile uses, since that
# target has no PublishAot and doesn't need a native linker.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-resolute-aot@sha256:c3d043bc8720363072224429968f167a664180718faee03c47285c78e4a7db45 AS build
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
    --self-contained \
    --configuration=Release

# --self-contained: PublishAot=true (see the csproj) always produces a self-contained native
# executable, so --no-self-contained (still used by the sibling Dockerfile, whose target has
# no PublishAot) fails here with NETSDK1102.
dotnet publish src/Vfps.Voprf.Server/Vfps.Voprf.Server.csproj \
    --no-restore \
    --no-build \
    --runtime=linux-x64 \
    --self-contained \
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

# Native AOT compiles the managed side straight to machine code, so nothing here needs the
# CLR or the ASP.NET Core runtime libraries - runtime-deps is just the OS plus libc/libssl,
# the same base a plain native binary would need. libsodium ships as a NuGet native asset and
# links only against libc, so it needs nothing added either - see src/Vfps.Voprf/README.md.
# ICU is skipped too: InvariantGlobalization is set in the csproj, and nothing here does
# culture-sensitive text handling.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.12-resolute-chiseled@sha256:93f4087fb76adb7446fd6c9d29be8b974f558bf4553b5ca09d59ad9c65164804 AS runtime
WORKDIR /opt/vfps-voprf
EXPOSE 8081/tcp
# non-root, and nothing here ever writes to disk: the image runs fine with a read-only root
# filesystem. The key is mounted read-only and read once at startup.
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    ASPNETCORE_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS=""
# Named explicitly rather than `COPY --from=build /build/publish .`: PublishAot's default
# Release behaviour strips debug info out of the executable into a companion .dbg with the
# whole BCL's symbols, tens of megabytes and useless without a debugger attached to the
# container - unlike the executable, it buys nothing at runtime. It's still in
# /build/publish for anyone who targets the build stage and wants it for crash triage.
COPY --from=build \
    /build/publish/Vfps.Voprf.Server \
    /build/publish/libsodium.so \
    /build/publish/appsettings.json \
    /build/publish/appsettings.Development.json \
    ./
CMD ["/opt/vfps-voprf/Vfps.Voprf.Server"]
