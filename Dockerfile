# syntax=docker/dockerfile:1.4
FROM mcr.microsoft.com/dotnet/nightly/aspnet:7.0-jammy-chiseled@sha256:601b023775e3dc0ba433381257b2b2160e3f59307ff213235f2c864d793d4e95 AS runtime
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
USER 65532:65532

FROM mcr.microsoft.com/dotnet/nightly/sdk:7.0-jammy@sha256:aaeeb2fb035e61d0c62f52aac5059d6be2c3f971e2f364152cdc9734bfb533dd AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

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

FROM build AS unit-test
WORKDIR /build/src/Vfps.Tests
RUN dotnet test \
    --configuration=Release \
    --collect:"XPlat Code Coverage" \
    --results-directory=./coverage \
    -l "console;verbosity=detailed" \
    --settings=runsettings.xml

# TODO: using mcr.microsoft.com/dotnet/nightly/sdk:7.0-jammy as a base image causes some framework version conflicts:
#       "Unable to find package Microsoft.AspNetCore.App.Runtime.linux-x64 with version"
#       should update this to run "ef migrations bundle" in the build layer once .NET 7 is here.
FROM mcr.microsoft.com/dotnet/sdk:7.0.100-rc.1-jammy@sha256:5f968241760ea2469f029d769aec5c5f03ef55c608c63cfd74c7509757a3813a AS build-migrations
WORKDIR /build
ENV PATH="/root/.dotnet/tools:${PATH}"
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# TODO: remove "prerelease" once .NET 7 is officially released
RUN dotnet tool install --global dotnet-ef --prerelease

COPY --from=build /build .

RUN dotnet ef migrations bundle \
    --project=src/Vfps/Vfps.csproj \
    --startup-project=src/Vfps/Vfps.csproj \
    --configuration=Release \
    --runtime=linux-x64 \
    --verbose \
    -o /build/efbundle

FROM runtime AS migrations
WORKDIR /opt/vfps-database-migrations
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp

COPY --from=build-migrations /build/src/Vfps/appsettings.json .
COPY --from=build-migrations /build/efbundle .

ENTRYPOINT ["/opt/vfps-database-migrations/efbundle"]

FROM runtime
WORKDIR /opt/vfps
ENV ASPNETCORE_URLS=http://+:8080,http://+:8081
EXPOSE 8080/tcp 8081/tcp

COPY --from=build /build/publish .

ENTRYPOINT ["dotnet", "/opt/vfps/Vfps.dll"]
