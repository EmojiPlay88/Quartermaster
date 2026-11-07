# Quartermaster relay. Build context: the repository root (see docker-compose.yml next to this file).
#   docker build -f src/Quartermaster.Relay/Dockerfile -t quartermaster-relay .
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -f src/Quartermaster.Relay/Dockerfile .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Quartermaster.Net/Quartermaster.Net.csproj src/Quartermaster.Net/
COPY src/Quartermaster.Relay/Quartermaster.Relay.csproj src/Quartermaster.Relay/
RUN dotnet restore src/Quartermaster.Relay/Quartermaster.Relay.csproj
COPY src/Quartermaster.Net/ src/Quartermaster.Net/
COPY src/Quartermaster.Relay/ src/Quartermaster.Relay/
# Framework-dependent, architecture-neutral output: the same files run on amd64 and arm64.
RUN dotnet publish src/Quartermaster.Relay/Quartermaster.Relay.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# /data holds the generated certificate and the turn mailbox; owned by the non-root user.
RUN mkdir -p /data && chown app:app /data
ENV ASPNETCORE_HTTP_PORTS= \
    Relay__Port=47820 \
    Relay__DataDir=/data
EXPOSE 47820
VOLUME /data
USER app
ENTRYPOINT ["dotnet","Quartermaster.Relay.dll"]
