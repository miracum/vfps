# syntax=docker/dockerfile:1.4
FROM mcr.microsoft.com/dotnet/nightly/sdk:7.0-jammy@sha256:aaeeb2fb035e61d0c62f52aac5059d6be2c3f971e2f364152cdc9734bfb533dd AS build
WORKDIR "/build"

COPY src/Vfps/Vfps.csproj src/Vfps/Vfps.csproj

RUN dotnet restore src/Vfps/Vfps.csproj

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
EOF

# TODO: using mcr.microsoft.com/dotnet/nightly/sdk:7.0-jammy as a base image causes same framework version conflicts.
#       should update this to run "ef migrations bundle" in the build layer once .NET 7 is here.
FROM mcr.microsoft.com/dotnet/sdk:7.0.100-rc.1-jammy@sha256:5f968241760ea2469f029d769aec5c5f03ef55c608c63cfd74c7509757a3813a AS build-migrations
WORKDIR "/build"
ENV PATH="/root/.dotnet/tools:${PATH}"

# TODO: remove "prerelease" once .NET 7 is officially released
RUN dotnet tool install --global dotnet-ef --prerelease

COPY --from=build /build .

RUN dotnet ef migrations bundle \
    --project=src/Vfps/Vfps.csproj \
    --startup-project=src/Vfps/Vfps.csproj \
    --configuration=Release \
    --self-contained \
    --runtime=linux-x64 \
    --verbose \
    -o /build/efbundle

# ideally, we should use the same base image as vfps itself to better leverage layer caching when deploying
FROM mcr.microsoft.com/dotnet/nightly/runtime:7.0-jammy-chiseled@sha256:9b56398c837741a0ed21e3a6feef2f99fe8bf61e8c35536f7e71577a067d6378 AS migrations
WORKDIR /opt/vfps-database-migrations
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
USER 65532:65532

COPY --from=build-migrations /build/src/Vfps/appsettings.json .
COPY --from=build-migrations /build/efbundle .

ENTRYPOINT ["/opt/vfps-database-migrations/efbundle"]

FROM mcr.microsoft.com/dotnet/nightly/aspnet:7.0-jammy-chiseled@sha256:019d6e9b6e923d6909a917a25a71989f58efe7bba09e4a8e4af58fab5b45dd2c
WORKDIR "/opt/vfps"
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 8080/tcp
USER 65532:65532

COPY --from=build /build/publish .

ENTRYPOINT ["dotnet", "/opt/vfps/Vfps.dll"]
