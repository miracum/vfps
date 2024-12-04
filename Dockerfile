# kics false positive "Missing User Instruction": <https://docs.kics.io/latest/queries/dockerfile-queries/fd54f200-402c-4333-a5a4-36ef6709af2f/>
# kics-scan ignore-line
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/aspnet:9.0.0-noble-chiseled@sha256:d24db805712b6bc67f4f18a50d3659fc9f8014dde1b36494c98f0159f6d1542f AS runtime
WORKDIR /opt/vfps
EXPOSE 8080/tcp 8081/tcp 8082/tcp
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS="" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0.100-noble@sha256:3bdd7f7fd595373d049c724f3a05ec8a8d9e27da05ba9cbe3ca6e0f3cc001e50 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="/root/.dotnet/tools:${PATH}"

RUN dotnet tool install --global dotnet-ef --version=9.0.0

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

dotnet ef migrations bundle \
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
COPY --from=docker.io/bitnami/kubectl:1.30.3@sha256:fc6e48522aa4417275d6fa92bd820f426bbf27454c80b327c449ef6e32467bd3 /opt/bitnami/kubectl/bin/kubectl /usr/bin/kubectl

COPY tests/chaos/chaos.yaml /tmp/
COPY --from=build-stress-test /build/publish .
# currently running into <https://github.com/dotnet/runtime/issues/80619>
# when running as non-root.

# hadolint ignore=DL3002
USER 0:0
ENTRYPOINT ["dotnet"]
CMD ["test", "/opt/vfps-stress/Vfps.StressTests.dll", "-l", "console;verbosity=detailed"]

FROM runtime
COPY --chown=65534:65534 --from=build /build/publish .
COPY --chown=65534:65534 --from=build /build/efbundle .
CMD ["/opt/vfps/Vfps.dll"]
