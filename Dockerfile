# syntax=docker/dockerfile:1.4
FROM mcr.microsoft.com/dotnet/nightly/aspnet:7.0.0-rc.1-jammy-chiseled@sha256:4011e4c1b5781ac8d48a322ee0b80f1742b8b5d1b50b8287e6c38ecaecd5575b AS runtime
WORKDIR /opt/vfps
EXPOSE 8080/tcp 8081/tcp
USER 65532:65532
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS="" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp

FROM mcr.microsoft.com/dotnet/sdk:7.0.100-rc.1-jammy@sha256:5f968241760ea2469f029d769aec5c5f03ef55c608c63cfd74c7509757a3813a AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="/root/.dotnet/tools:${PATH}"

# TODO: remove "prerelease" once .NET 7 is officially released
RUN dotnet tool install --global dotnet-ef --prerelease

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

dotnet ef migrations bundle \
    --project=src/Vfps/Vfps.csproj \
    --startup-project=src/Vfps/Vfps.csproj \
    --configuration=Release \
    --runtime=linux-x64 \
    --verbose \
    -o /build/efbundle
EOF

FROM build AS unit-test
WORKDIR /build/src/Vfps.Tests
RUN dotnet test \
    --configuration=Release \
    --collect:"XPlat Code Coverage" \
    --results-directory=./coverage \
    -l "console;verbosity=detailed" \
    --settings=runsettings.xml

FROM runtime
COPY --chown=65532:65532 --from=build /build/publish .
COPY --chown=65532:65532 --from=build /build/efbundle .
ENTRYPOINT ["dotnet", "/opt/vfps/Vfps.dll"]
