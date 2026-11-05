$ErrorActionPreference = 'Stop'
$backendRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$envFile = Join-Path $backendRoot '.env'
# Final Phase 1: .NET/NuGet restores the parser; setup no longer downloads or configures Java.
if (-not (Test-Path -LiteralPath $envFile)) {
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $sqlPassword = 'Hsk_' + [Convert]::ToBase64String($bytes).Replace('+', 'x').Replace('/', 'y') + '_1'
    Set-Content -LiteralPath $envFile -Value "HSK_SQL_SA_PASSWORD=$sqlPassword" -Encoding utf8
}
$sqlLine = Get-Content -LiteralPath $envFile | Where-Object { $_ -like 'HSK_SQL_SA_PASSWORD=*' } | Select-Object -First 1
if (-not $sqlLine) { throw 'The local .env file does not contain HSK_SQL_SA_PASSWORD.' }
$settingsPath = Join-Path $backendRoot 'src/HousekeepingParser/local.settings.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    $settings = Get-Content (Join-Path $backendRoot 'src/HousekeepingParser/local.settings.example.json') -Raw | ConvertFrom-Json
    $settings.Values.Database__ConnectionString = $settings.Values.Database__ConnectionString.Replace('CHANGE_ME_LOCAL_ONLY', $sqlLine.Substring('HSK_SQL_SA_PASSWORD='.Length))
    $demoPassword = if ($env:HSK_DEMO_PASSWORD) { $env:HSK_DEMO_PASSWORD } else { 'demo' }
    $settings.Values.Auth__SeedUsers__0__Password = $demoPassword
    $settings.Values.Auth__SeedUsers__1__Password = $demoPassword
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
}
Write-Output 'Local configuration is ready. Existing settings were preserved. Next: docker compose up -d'
