# Deploying to Render with Docker

Render runs Docker images, but its only managed database is PostgreSQL. SQL Server therefore has to come from one
of two places:

| | Option A: everything on Render (this repo's `render.yaml`) | Option B: Render web + Azure SQL free tier |
|---|---|---|
| SQL Server | Express in a **private service** with a persistent disk | Azure SQL Database (serverless, free offer) |
| Approx. cost | SQL 2 GB instance + 10 GB disk + Starter web + a few cents of job time | Starter web + job time. The database is free up to its monthly limit |
| Caveat | SQL Server needs at least 2 GB RAM, so the database alone is the Standard plan | The live feed queries the DB every few seconds, so the database never auto-pauses. 100k vCore-seconds is roughly 28 h of 1 vCore, so turn the feed off between demos |

## Architecture on Render

```
                Internet
                   │ HTTPS (TLS terminated by Render)
          ┌────────▼─────────┐
          │ stockmonitor-web │  web service, Docker, binds $PORT, SignalR over WebSockets
          └────────┬─────────┘
                   │ private network, TCP 1433
          ┌────────▼─────────┐        ┌───────────────────┐
          │ stockmonitor-sql │◄───────┤ stockmonitor-etl  │ cron job, triggered manually
          │ SQL Server 2022  │        │ --init-db + load  │ (schema + Excel load)
          │ disk /var/opt/mssql       └───────────────────┘
          └──────────────────┘
```

What the repo already handles for Render:
- **Port:** the web app binds `0.0.0.0:$PORT` when Render sets `PORT` (`Program.cs`).
- **Health check:** `/healthz` is a liveness check that doesn't touch the DB, so a cold database doesn't fail the deploy. `/health` still checks SQL.
- **No sqlcmd needed:** the ETL image contains `database/*.sql` and the workbook, and `--init-db` applies the scripts
  over SqlClient. It detects Azure SQL and skips the `CREATE DATABASE` / `USE` statements there.
- **SQL Server image:** `deploy/sqlserver/Dockerfile` uses the **Express** edition (free for production), caps memory at
  1.5 GB, and runs as root so it can write to Render's disk.

## Option A: step by step

**1. Push the project to GitHub or GitLab.** Push the whole folder, including `data/` and `render.yaml`.
```bash
git init && git add . && git commit -m "StockMonitor"
git remote add origin https://github.com/<you>/stockmonitor.git && git push -u origin main
```

**2. Create the Blueprint.** In the Render Dashboard, go to **New → Blueprint**, pick the repo and choose a region
(all services must be in the same region to share the private network). Render reads `render.yaml` and asks for the
`sync: false` values:
- `MSSQL_SA_PASSWORD` (on *stockmonitor-sql*): at least 8 characters with upper case, lower case, a digit and a symbol, e.g. `Sm!` plus a long random string.
- `STOCKMONITOR_ConnectionStrings__StockMonitor` (env group *stockmonitor-db*): enter a placeholder for now and set it in step 3.

Click **Apply**. The SQL service and the web service start building. The first SQL Server boot takes about a minute.

**3. Set the connection string.** Open **stockmonitor-sql → Connect → Internal** and copy the host (something like
`stockmonitor-sql-abcd`). Then go to **Env Groups → stockmonitor-db** and set:
```
Server=stockmonitor-sql-abcd,1433;Database=StockMonitor;User Id=sa;Password=<the SA password>;TrustServerCertificate=True;Encrypt=True
```
Save it, and if Render doesn't offer to redeploy, use **Manual Deploy** on the web service so it picks up the new value.

**4. Load the data.** Open **stockmonitor-etl → Trigger Run**. The logs should show:
```
Initialising database 'StockMonitor' on SQL Server
  01_create_database.sql: 5 batches … 06_views.sql: 9 batches
Database ready.
[1/7] Extracting 9 sheets …
…
[7/7] Reconciling control totals
Batch 1 SUCCEEDED.
```
The run is idempotent. Running it again skips the already-loaded file, so it is safe to trigger again.

**5. Open the app** at `https://stockmonitor-web.onrender.com` (the exact URL is on the service page). Within a few
seconds the dashboard connection pill turns **Live**, the 2024 transactions start streaming in, and prices begin ticking.

**6. Optional checks**
- `https://<web>/health` should return `Healthy`, which confirms the web app can reach SQL Server.
- To push your own transaction, see the `curl` example in the README and point it at the Render URL.

### Updating
- **Code change:** `git push`. Render rebuilds the images, and the web app restarts with zero downtime.
- **Schema change:** edit `database/*.sql` (keep the scripts idempotent), push, then click **Trigger Run** on the ETL job.
  `--init-db` runs on every job run.
- **Demo reset:** in **stockmonitor-etl → Settings → Docker Command**, temporarily set
  `dotnet StockMonitor.Etl.dll --init-db --reset --file "/data/Open Stock Transaction Data 2020-2024.xlsx" --cutoff 2024-01-01`,
  trigger a run, then clear the override. After that, restart the web service.

### Backups
Render snapshots persistent disks daily. For point-in-time safety you can also run a SQL backup to the same disk,
for example from the SQL service's **Shell**:
```bash
/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "BACKUP DATABASE StockMonitor TO DISK='/var/opt/mssql/backup/sm.bak' WITH INIT"
```

## Option B: Azure SQL Database (free offer) + Render web

1. In the Azure portal, create a **SQL Database** and click **Apply offer** on the free banner. Choose *Auto-pause until next
   month* so you can never be billed. Name it `StockMonitor`. On the server's **Networking** page, allow public access and add
   **Allow Azure services…** plus Render's outbound IPs (listed on each Render service under **Connect → Outbound**).
2. Delete the `stockmonitor-sql` block from `render.yaml`, or delete that service after the Blueprint is created.
3. Set the env group connection string to the ADO.NET string from Azure (**Connection strings** blade), e.g.
   `Server=tcp:<server>.database.windows.net,1433;Database=StockMonitor;User ID=<admin>;Password=<pwd>;Encrypt=True;TrustServerCertificate=False`
4. **Trigger Run** on the ETL job. `--init-db` detects Azure SQL and creates only the schemas, tables, procedures and views.
5. To make the free compute last, set these env vars on the web service when you are not demoing:
   `STOCKMONITOR_Monitor__Prices__Enabled=false` and `STOCKMONITOR_Monitor__Replay__Enabled=false`.
   With them off, the database can auto-pause once nobody has the dashboard open.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| SQL service log: `requires a machine with at least 2000 megabytes of memory` | The plan is too small. Use `standard` (2 GB) or `pro` |
| SQL service log: `Permission denied` on `/var/opt/mssql` | The image must run as root (already set in `deploy/sqlserver/Dockerfile`). Check that the disk mount path is exactly `/var/opt/mssql` |
| SQL service stuck "deploying" | Private services are detected by their open port. SQL Server opens 1433 after about 30–60 s, so wait for `SQL Server is now ready for client connections` in the logs |
| ETL: `A network-related or instance-specific error` | The connection string host is wrong (use the *Internal* address), the services are in different regions, or SQL is still starting |
| ETL: `Login failed for user 'sa'` | The password in the env group doesn't match `MSSQL_SA_PASSWORD`. If you changed it after the first boot, the old one still applies, because the disk keeps the original |
| Web deploys but pages show empty data | The ETL hasn't run yet. Trigger it, then refresh |
| Dashboard pill stays "Reconnecting" | A browser extension or proxy is blocking WebSockets. SignalR falls back to long polling automatically, so check the browser console |
| Live feed stops after a while | The web service is on the `free` plan and went to sleep. Use `starter` or above |
| Blueprint rejects `plan:` | Use the new compute-plan IDs (`1c-2g` for standard, `0.5c-512mb` for starter) |
