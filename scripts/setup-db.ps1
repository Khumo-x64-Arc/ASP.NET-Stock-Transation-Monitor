# Applies database/*.sql in order. Requires sqlcmd (https://aka.ms/sqlcmd).
param(
  [string]$Server = "localhost,1433",
  [string]$User = "sa",
  [string]$Password = "YourStrong!Passw0rd",
  [switch]$Trusted   # use Windows auth instead of SQL auth
)
$root = Split-Path -Parent $PSScriptRoot
Get-ChildItem "$root/database/*.sql" | Sort-Object Name | ForEach-Object {
  Write-Host "== $($_.Name)"
  if ($Trusted) { sqlcmd -S $Server -E -C -b -i $_.FullName }
  else          { sqlcmd -S $Server -U $User -P $Password -C -b -i $_.FullName }
  if ($LASTEXITCODE -ne 0) { throw "Failed on $($_.Name)" }
}
Write-Host "Database ready."
