<#
  Creates a site database (if it doesn't exist) and brings it up to date with SQL\Schema.sql.
  Safe to re-run: never drops tables or deletes data.

  Examples:
    .\deploy\Setup-Database.ps1 -Database MediaManager                       # Windows login
    .\deploy\Setup-Database.ps1 -Database PrivateMedia -Credential (Get-Credential)   # SQL login (e.g. sa)
#>
param(
    [Parameter(Mandatory)][string]$Database,
    [string]$Server = 'localhost',
    [pscredential]$Credential
)

$ErrorActionPreference = 'Stop'
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "Database name may only contain letters, digits and underscores." }

$schemaFile = Join-Path (Split-Path $PSScriptRoot -Parent) 'SQL\Schema.sql'

function New-Connection([string]$db) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b['Data Source'] = $Server
    $b['Initial Catalog'] = $db
    if ($Credential) {
        $b['User ID'] = $Credential.UserName
        $b['Password'] = $Credential.GetNetworkCredential().Password
    } else {
        $b['Integrated Security'] = $true
    }
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    $c.Open()
    return $c
}

# 1. Create the database if needed
$master = New-Connection 'master'
try {
    $cmd = $master.CreateCommand()
    $cmd.CommandText = "IF DB_ID(N'$Database') IS NULL BEGIN CREATE DATABASE [$Database]; SELECT 1 END ELSE SELECT 0"
    if ($cmd.ExecuteScalar() -eq 1) { Write-Host "Created database $Database" -ForegroundColor Green }
    else { Write-Host "Database $Database already exists" }
} finally { $master.Close() }

# 2. Apply the schema (split into batches on GO lines, like SSMS/sqlcmd do)
$batches = [regex]::Split([IO.File]::ReadAllText($schemaFile), '^\s*GO\s*$', 'Multiline') |
    Where-Object { ($_ -replace '(?m)^\s*--.*$', '' -replace '(?s)/\*.*?\*/', '').Trim() }

$conn = New-Connection $Database
try {
    foreach ($batch in $batches) {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $batch
        [void]$cmd.ExecuteNonQuery()
    }
} finally { $conn.Close() }

Write-Host "Applied $($batches.Count) batches from SQL\Schema.sql to $Database. Schema is up to date." -ForegroundColor Green
