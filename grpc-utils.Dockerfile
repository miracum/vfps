FROM docker.io/library/ubuntu:24.04@sha256:6015f66923d7afbc53558d7ccffd325d43b4e249f41a6e93eef074c9505d2233
SHELL ["/bin/bash", "-eo", "pipefail", "-c"]

ENV GRPCURL_URL=https://github.com/fullstorydev/grpcurl/releases/download/v1.8.8/grpcurl_1.8.8_linux_x86_64.tar.gz \
    GHZ_URL=https://github.com/bojand/ghz/releases/download/v0.117.0/ghz-linux-x86_64.tar.gz

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

COPY src/Vfps/Protos /tmp/protos/Protos

# grpcurl -import-path=/tmp/protos/ -proto=Protos/vfps/api/v1/namespaces.proto describe

USER 65534:65534
