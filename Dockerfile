# syntax=docker/dockerfile:1

# Build stage. The SDK lives here only; nothing from this stage other than the
# published self-contained binary reaches the final image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Locked restore against the committed packages.lock.json, which carries a
# net10.0/<rid> target for every RuntimeIdentifier declared in the csproj.
# Do not add --force-evaluate or -p:RestoreLockedMode=false here: that would
# resolve package bytes the reviewed lock file never represented and the locked
# test lanes never exercised. Restoring for all declared RIDs (rather than
# passing --runtime, which narrows the set and stops matching the lock file)
# lets the publish below run with --no-restore.
RUN dotnet restore src/Honua.DevOps.Agent/Honua.DevOps.Agent.csproj
RUN dotnet publish src/Honua.DevOps.Agent/Honua.DevOps.Agent.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --no-restore \
    --output /out \
    -p:DebugType=None \
    -p:DebugSymbols=false

# Runtime stage. runtime-deps carries only the native dependencies a
# self-contained .NET binary needs — no SDK and no shared framework. The chiseled
# -extra variant adds the ICU/tzdata that globalization-enabled code requires and
# runs as the non-root `app` user.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /out/Honua.DevOps.Agent /app/honua-devops
# stdin/stdout carry the MCP protocol; keep them attached (docker run -i).
ENTRYPOINT ["/app/honua-devops", "--mcp"]
