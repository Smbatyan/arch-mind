# syntax=docker/dockerfile:1

# Stage 1: build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY . .

RUN dotnet restore ArchMind.Api/ArchMind.Api.csproj
RUN dotnet publish ArchMind.Api/ArchMind.Api.csproj -c Release -o /out

# Stage 2: runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Runtime dependencies:
# - git: cloning repositories for ingestion
# - python3 / venv / pip: hosts the Graphify CLI (invoked as a subprocess by the .NET app)
# - build-essential, pkg-config: required to compile tree-sitter grammars during Graphify install
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        python3 \
        python3-pip \
        python3-venv \
        build-essential \
        pkg-config \
    && rm -rf /var/lib/apt/lists/*

# Install Graphify (https://github.com/safishamsi/graphify) into an isolated venv,
# then expose its entrypoint on PATH so the backend can invoke `graphify` directly.
# anthropic is installed separately because graphify's claude backend requires it
# but it is not declared as a hard dependency in graphify's setup.
RUN python3 -m venv /opt/graphify-venv \
    && /opt/graphify-venv/bin/pip install --no-cache-dir git+https://github.com/safishamsi/graphify.git \
    && /opt/graphify-venv/bin/pip install --no-cache-dir anthropic
RUN ln -s /opt/graphify-venv/bin/graphify /usr/local/bin/graphify

# Smoke-test: fail the image build immediately if the CLI is missing or broken.
RUN graphify --help > /dev/null && echo "graphify installed OK"

COPY --from=build /out /app

EXPOSE 5000

ENTRYPOINT ["dotnet", "ArchMind.Api.dll"]
