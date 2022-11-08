# syntax=docker/dockerfile:1.4
FROM mcr.microsoft.com/dotnet/nightly/aspnet:7.0-jammy-chiseled@sha256:c1350bc839492415f55f4dc6549533e4bad68ecdaf9976497a235eee18f89993 AS runtime
WORKDIR /opt/vfps
EXPOSE 8080/tcp 8081/tcp
USER 65532:65532
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS="" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp

FROM mcr.microsoft.com/dotnet/sdk:7.0-jammy@sha256:4099e5d6966436aa7cc37e9d2d5d0ab4b1e09abe9982d138a6a37f4ca696ce27 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="/root/.dotnet/tools:${PATH}"

RUN dotnet tool install --global dotnet-ef

COPY src/Vfps/Vfps.csproj src/Vfps/Vfps.csproj

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

dotnet ef migrations bundle \
    --project=src/Vfps/Vfps.csproj \
    --startup-project=src/Vfps/Vfps.csproj \
    --configuration=Release \
    --target-runtime=linux-x64 \
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
