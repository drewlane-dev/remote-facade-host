FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY src/RemoteFacadeHost/RemoteFacadeHost.csproj src/RemoteFacadeHost/
RUN dotnet restore src/RemoteFacadeHost/RemoteFacadeHost.csproj
COPY src/RemoteFacadeHost/ src/RemoteFacadeHost/
RUN dotnet publish src/RemoteFacadeHost/RemoteFacadeHost.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# Only needed when LIB mounting is configured; harmless otherwise and small.
RUN apk add --no-cache cifs-utils

WORKDIR /app
COPY --from=build /app .

# The entrypoint is a script, not `dotnet` directly, because a plugin's native
# assets have to be on LD_LIBRARY_PATH BEFORE this process starts -- the
# dynamic loader reads that variable once and never again. See entrypoint.sh.
COPY entrypoint.sh /usr/local/bin/remote-facade-host
RUN chmod +x /usr/local/bin/remote-facade-host

EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/remote-facade-host"]
