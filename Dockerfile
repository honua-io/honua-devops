# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/Honua.DevOps.Agent/Honua.DevOps.Agent.csproj \
    --runtime linux-x64 \
    --force-evaluate \
    -p:RestoreLockedMode=false \
    -p:PublishSingleFile=true
RUN dotnet publish src/Honua.DevOps.Agent/Honua.DevOps.Agent.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --no-restore \
    --output /out \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /out/Honua.DevOps.Agent /app/honua-devops
ENTRYPOINT ["/app/honua-devops", "--mcp"]
