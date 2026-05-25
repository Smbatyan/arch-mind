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

# git is needed at runtime for repository cloning
RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /out /app

EXPOSE 5000

ENTRYPOINT ["dotnet", "ArchMind.Api.dll"]
