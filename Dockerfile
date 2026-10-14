FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402-resolute@sha256:6924c5c6564690089a67509f5a6ae85d7afa28d7f0f6466ec1b8a5439764ca92 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="/root/.dotnet/tools:${PATH}" \
    ASPNETCORE_ENVIRONMENT="Production" \
    DOTNET_ENVIRONMENT="Production"

COPY src/Directory.Build.props src/
COPY src/Vfps.Voprf/Vfps.Voprf.csproj src/Vfps.Voprf/
COPY src/Vfps.Voprf/packages.lock.json src/Vfps.Voprf/
COPY src/Vfps.Voprf.Contracts/Vfps.Voprf.Contracts.csproj src/Vfps.Voprf.Contracts/
COPY src/Vfps.Voprf.Contracts/packages.lock.json src/Vfps.Voprf.Contracts/
COPY src/Vfps.Voprf.Client/Vfps.Voprf.Client.csproj src/Vfps.Voprf.Client/
COPY src/Vfps.Voprf.Client/packages.lock.json src/Vfps.Voprf.Client/
COPY src/Vfps/Vfps.csproj src/Vfps/
COPY src/Vfps/packages.lock.json src/Vfps/

RUN dotnet restore --locked-mode src/Vfps/Vfps.csproj

COPY . .

RUN <<EOF
dotnet build src/Vfps/Vfps.csproj \
    --no-restore \
    --runtime=linux-x64 \
    --no-self-contained \
    --configuration=Release

dotnet publish src/Vfps/Vfps.csproj \
    --no-restore \
    --no-build \
    --runtime=linux-x64 \
    --no-self-contained \
    --configuration=Release \
    -o /build/publish
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
# kubectl and a baked-in copy of the chaos manifests used to live here, for the Argo Workflows
# templates that applied and removed chaos from inside the cluster. tests/chaos/ha/run.sh now drives
# chaos from outside it with the runner's own kubeconfig, so this image only has to run tests.
COPY --from=build-stress-test /build/publish .
# currently running into <https://github.com/dotnet/runtime/issues/80619>
# when running as non-root.

# hadolint ignore=DL3002
USER 0:0
ENTRYPOINT ["dotnet"]
CMD ["/opt/vfps-stress/Vfps.StressTests.dll", "-reporter", "verbose"]

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0.13-resolute-chiseled-extra@sha256:e662e2bbca99a18b0922af83d794a212495e737e15ee8b56ffde9fcd24b0cba5 AS runtime
WORKDIR /opt/vfps
EXPOSE 8080/tcp 8081/tcp 8082/tcp
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS="" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
COPY --from=build /build/publish .
CMD ["/opt/vfps/Vfps.dll"]
