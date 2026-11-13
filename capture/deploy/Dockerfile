# odx-orchestrator for a Linux server, built from source; compose.yaml builds it with capture/ as the context.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY dotnet/Directory.Build.props dotnet/
COPY dotnet/src/Odx.Core dotnet/src/Odx.Core
COPY dotnet/src/Odx.Protocol dotnet/src/Odx.Protocol
COPY dotnet/src/Apps/Odx.Orchestrator dotnet/src/Apps/Odx.Orchestrator
COPY config/odx-capture.json config/
ARG VERSION=0.3.6
RUN dotnet publish dotnet/src/Apps/Odx.Orchestrator -c Release -o /app -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY profiles games
# The state (a bind mount) goes to /data; the image's non-root "app" user (uid 1654) must own it.
RUN mkdir -p /data && chown app /data
USER app
EXPOSE 5180 12300/udp
ENTRYPOINT ["dotnet"]
