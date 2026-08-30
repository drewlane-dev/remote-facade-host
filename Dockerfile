FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY src/RemoteFacadeHost/RemoteFacadeHost.csproj src/RemoteFacadeHost/
RUN dotnet restore src/RemoteFacadeHost/RemoteFacadeHost.csproj
COPY src/RemoteFacadeHost/ src/RemoteFacadeHost/
RUN dotnet publish src/RemoteFacadeHost/RemoteFacadeHost.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# cifs-utils is only needed when LIB mounting is configured; harmless otherwise.
#
# icu-libs is needed far more often than it looks. The Alpine .NET images run in
# globalization-invariant mode, and a plugin doing anything culture-aware fails
# with "Globalization Invariant Mode is not supported" -- Microsoft.Data.SqlClient
# throws it on the first CONNECTION, long after the assembly loaded fine, so it
# reads as a database problem rather than an image one.
RUN apk add --no-cache cifs-utils icu-libs icu-data-full

# Full globalization, now that ICU is present. Without flipping this the
# package is installed and ignored.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

WORKDIR /app
COPY --from=build /app .

# The entrypoint is a script, not `dotnet` directly, because a plugin's native
# assets have to be on LD_LIBRARY_PATH BEFORE this process starts -- the
# dynamic loader reads that variable once and never again. See entrypoint.sh.
COPY entrypoint.sh /usr/local/bin/remote-facade-host
RUN chmod +x /usr/local/bin/remote-facade-host

EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/remote-facade-host"]
