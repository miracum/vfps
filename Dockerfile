# syntax=docker/dockerfile:1.6@sha256:ac85f380a63b13dfcefa89046420e1781752bab202122f8f50032edf31be0021
# kics false positive "Missing User Instruction": <https://docs.kics.io/latest/queries/dockerfile-queries/fd54f200-402c-4333-a5a4-36ef6709af2f/>
# kics-scan ignore-line
FROM mcr.microsoft.com/dotnet/aspnet:8.0.1-jammy-chiseled@sha256:fd65dd3a9fef62b12873e1f3db1d0cf4bffd3984c1656971a2bfcaab14224b67 AS runtime
WORKDIR /opt/vfps
EXPOSE 8080/tcp 8081/tcp 8082/tcp
USER 65534:65534
ENV DOTNET_ENVIRONMENT="Production" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS="" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp

FROM mcr.microsoft.com/dotnet/sdk:8.0.101-jammy@sha256:4f63cff30878af7c8a378e7c28e7bbe3d40838f0ff81e7aa4426d2af3f4d1696 AS build
WORKDIR /build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="/root/.dotnet/tools:${PATH}"

RUN dotnet tool install --global dotnet-ef --version=7.0.0

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
COPY --from=docker.io/bitnami/kubectl:1.29.0@sha256:121fa926a1304f34439082df44b0bdbe6eee583760abd2ae67949a2987322fe9 /opt/bitnami/kubectl/bin/kubectl /usr/bin/kubectl

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
