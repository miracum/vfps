FROM docker.io/library/ubuntu:26.04@sha256:b7f48194d4d8b763a478a621cdc81c27be222ba2206ca3ca6bc42b49685f3d9e
SHELL ["/bin/bash", "-eo", "pipefail", "-c"]

ENV GRPCURL_URL=https://github.com/fullstorydev/grpcurl/releases/download/v1.9.4/grpcurl_1.9.4_linux_x86_64.tar.gz \
    GHZ_URL=https://github.com/bojand/ghz/releases/download/v0.121.0/ghz-linux-x86_64.tar.gz

# hadolint ignore=DL3008
RUN <<EOF
apt-get update
apt-get install -y --no-install-recommends curl jq ca-certificates
apt-get clean
rm -rf /var/lib/apt/lists/*

curl -LSs "$GRPCURL_URL" | tar xz
mv ./grpcurl /usr/local/bin/grpcurl
chmod +x /usr/local/bin/grpcurl
grpcurl --version

curl -LSs "$GHZ_URL" | tar xz
mv ./ghz /usr/local/bin/ghz
chmod +x /usr/local/bin/ghz
ghz --version
EOF

# For tools that can't use the server's gRPC reflection. Rooted the way the server compiles them
# (ProtoRoot in Vfps.csproj), so imports resolve with a single import path:
# grpcurl -import-path=/tmp/protos -proto=vfps/api/v1/namespaces.proto describe
COPY src/Vfps/Protos /tmp/protos

USER 65534:65534
