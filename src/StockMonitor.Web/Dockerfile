FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/StockMonitor.Core/ src/StockMonitor.Core/
COPY src/StockMonitor.Data/ src/StockMonitor.Data/
COPY src/StockMonitor.Web/ src/StockMonitor.Web/
RUN dotnet publish src/StockMonitor.Web/StockMonitor.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "StockMonitor.Web.dll"]
