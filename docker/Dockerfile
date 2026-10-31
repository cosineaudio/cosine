# Stage 1: Build frontend
FROM node:22-alpine AS frontend
WORKDIR /src
COPY src/Cosine.Web/package*.json ./
RUN npm ci
COPY src/Cosine.Web/ ./
RUN npm run build

# Stage 2: Build backend
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS backend
WORKDIR /src
COPY Cosine.slnx ./
COPY src/Cosine.Audio/Cosine.Audio.csproj src/Cosine.Audio/
COPY src/Cosine.Core/Cosine.Core.csproj src/Cosine.Core/
COPY src/Cosine.Infrastructure/Cosine.Infrastructure.csproj src/Cosine.Infrastructure/
COPY src/Cosine.Api/Cosine.Api.csproj src/Cosine.Api/
COPY src/Cosine.Playback.Abstractions/Cosine.Playback.Abstractions.csproj src/Cosine.Playback.Abstractions/
COPY src/Cosine.Playback.Upnp/Cosine.Playback.Upnp.csproj src/Cosine.Playback.Upnp/
RUN dotnet restore src/Cosine.Api/Cosine.Api.csproj
COPY src/Cosine.Audio/ src/Cosine.Audio/
COPY src/Cosine.Core/ src/Cosine.Core/
COPY src/Cosine.Infrastructure/ src/Cosine.Infrastructure/
COPY src/Cosine.Api/ src/Cosine.Api/
COPY src/Cosine.Playback.Abstractions/ src/Cosine.Playback.Abstractions/
COPY src/Cosine.Playback.Upnp/ src/Cosine.Playback.Upnp/
COPY --from=frontend /src/dist src/Cosine.Api/wwwroot/
RUN dotnet publish src/Cosine.Api/Cosine.Api.csproj -c Release -o /app --no-restore -p:SkipFrontendBuild=true

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# su-exec lets the entrypoint drop from root to the requested PUID/PGID after chowning
# the writable mounts.
RUN apk add --no-cache ffmpeg icu-libs krb5-libs su-exec
WORKDIR /app
COPY --from=backend /app ./
COPY LICENSE THIRD_PARTY_NOTICES.md /usr/share/doc/cosine/
COPY tools/openapi/specs/LICENSE /usr/share/doc/cosine/opensubsonic-LICENSE
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
# HOME must be writable by the runtime user for ASP.NET Data Protection keys.
ENV HOME=/app
# Override to match the owner of the mounted media/cache volumes (see entrypoint.sh).
ENV PUID=1000
ENV PGID=1000
EXPOSE 8080
# Starts as root, chowns the writable paths, then su-execs to PUID:PGID.
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
