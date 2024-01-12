# syntax=docker/dockerfile:1.4@sha256:9ba7531bd80fb0a858632727cf7a112fbfd19b17e94c4e84ced81e24ef1a0dbc
FROM docker.io/library/ubuntu:24.04@sha256:38d380071057443ebb6d65566c03a98eaec411e84e04e5e16cff49b1d6fdc9a0
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
