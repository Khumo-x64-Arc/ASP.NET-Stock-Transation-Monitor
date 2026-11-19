FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/StockMonitor.Core/ src/StockMonitor.Core/
COPY src/StockMonitor.Data/ src/StockMonitor.Data/
COPY src/StockMonitor.Etl/ src/StockMonitor.Etl/
RUN dotnet publish src/StockMonitor.Etl/StockMonitor.Etl.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
# schema scripts + source workbook travel with the image so the job is self-contained (Render, CI, k8s Job)
COPY database/ /app/database/
COPY data/ /data/
ENTRYPOINT ["dotnet", "StockMonitor.Etl.dll"]
# default: create/upgrade the schema, then load; rows from 2024 are queued for the live replay
CMD ["--init-db", "--file", "/data/Open Stock Transaction Data 2020-2024.xlsx", "--cutoff", "2024-01-01"]
