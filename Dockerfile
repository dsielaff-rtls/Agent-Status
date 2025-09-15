# Dockerfile with CATO certificate support
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Add CATO certificate first thing
RUN apt-get update && apt-get install -y ca-certificates curl

# Download and install Cato Networks certificate
RUN curl -o /usr/local/share/ca-certificates/CatoNetworksTrustedRootCA.crt https://clientdownload.catonetworks.com/public/certificates/CatoNetworksTrustedRootCA.pem
RUN update-ca-certificates

# Verify certificate installation
RUN ls -la /usr/local/share/ca-certificates/
RUN openssl x509 -in /usr/local/share/ca-certificates/CatoNetworksTrustedRootCA.crt -text -noout | head -10

# Set environment variables for SSL and .NET to use system certificates
ENV SSL_CERT_DIR=/etc/ssl/certs
ENV SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt
ENV DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0
ENV REQUESTS_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt
ENV CURL_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt

WORKDIR /src
COPY ["Agent Status/Agent Status.csproj", "Agent Status/"]
RUN dotnet restore "Agent Status/Agent Status.csproj"
COPY . .
WORKDIR "/src/Agent Status"
RUN dotnet build "Agent Status.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Agent Status.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Add certificate to runtime image as well
RUN apt-get update && apt-get install -y ca-certificates curl
RUN curl -o /usr/local/share/ca-certificates/CatoNetworksTrustedRootCA.crt https://clientdownload.catonetworks.com/public/certificates/CatoNetworksTrustedRootCA.pem
RUN update-ca-certificates

COPY --from=publish /app/publish .
ENV RUNNING_IN_DOCKER=true
ENTRYPOINT ["dotnet", "Agent Status.dll"]
