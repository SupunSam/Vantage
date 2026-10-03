# API: build with the .NET 10 SDK, run on the ASP.NET 10 runtime. Build context is the repo root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/src/Vantage.Domain/Vantage.Domain.csproj src/Vantage.Domain/
COPY backend/src/Vantage.Infrastructure/Vantage.Infrastructure.csproj src/Vantage.Infrastructure/
COPY backend/src/Vantage.Api/Vantage.Api.csproj src/Vantage.Api/
RUN dotnet restore src/Vantage.Api/Vantage.Api.csproj
COPY backend/src/ src/
RUN dotnet publish src/Vantage.Api/Vantage.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# Company root certificates (for networks that inspect HTTPS): any *.crt in deploy/certs is trusted.
COPY deploy/certs/ /usr/local/share/ca-certificates/company/
RUN update-ca-certificates
# /data holds uploaded files and the encryption keys for stored secrets (mounted as a volume).
RUN mkdir -p /data/keys /data/files && chown -R $APP_UID /data
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Vantage.Api.dll"]
