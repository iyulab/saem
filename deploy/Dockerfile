# syntax=docker/dockerfile:1
#
# saem's hosts, built from this source tree — nothing needs to be installed on the machine but Docker.
# Build from the repository root:   docker build -f deploy/Dockerfile --target <mcp|console|executor|author> .
# The reference compose file (deploy/compose.yaml) builds the targets it needs.

ARG DOTNET_SDK=mcr.microsoft.com/dotnet/sdk:10.0.403
ARG DOTNET_RUNTIME=mcr.microsoft.com/dotnet/aspnet:10.0

# The console's screens. A publish without this step serves the API and no page.
FROM node:22-alpine AS web
WORKDIR /src/src/Saem.Console/web
COPY src/Saem.Console/web/package.json src/Saem.Console/web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Saem.Console/web/ ./
RUN npm run build

FROM ${DOTNET_SDK} AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY --from=web /src/src/Saem.Console/wwwroot src/Saem.Console/wwwroot
RUN dotnet publish src/Saem.Mcp -c Release -o /out/mcp \
 && dotnet publish src/Saem.Console -c Release -o /out/console \
 && dotnet publish src/Saem.Materialization.Cli -c Release -o /out/executor \
 && dotnet publish src/Saem.Authoring.Cli -c Release -o /out/author

# Each host runs as the image's unprivileged user and listens where ASPNETCORE_URLS says (the compose file sets it).
FROM ${DOTNET_RUNTIME} AS runtime-base
WORKDIR /app
# /data holds what a host keeps between runs (the catalog, the draft, the sessions' keys); a named volume mounted there
# starts with this owner.
RUN mkdir /data && chown app:app /data
USER app

FROM runtime-base AS mcp
COPY --from=build /out/mcp ./
ENTRYPOINT ["dotnet", "Saem.Mcp.dll"]

FROM runtime-base AS console
COPY --from=build /out/console ./
ENTRYPOINT ["dotnet", "Saem.Console.dll"]

FROM runtime-base AS executor
COPY --from=build /out/executor ./
ENTRYPOINT ["dotnet", "saem-materialize.dll"]

FROM runtime-base AS author
COPY --from=build /out/author ./
ENTRYPOINT ["dotnet", "saem-author.dll"]

# The install step's one-shot jobs: they talk to the services over HTTP with curl and jq, and nothing else.
FROM alpine:3.22 AS provision
RUN apk add --no-cache curl jq
COPY deploy/provision /provision
USER nobody
ENTRYPOINT ["/bin/sh"]
