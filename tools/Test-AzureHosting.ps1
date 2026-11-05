param([Parameter(Mandatory)][string]$ResourceGroup, [Parameter(Mandatory)][string]$AppName)
$ErrorActionPreference = 'Stop'
# Read-only verification: configuration in Git alone does not prove the live app uses the required plan.
if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'Install Azure CLI and sign in before checking live hosting.' }
$siteJson = az functionapp show --resource-group $ResourceGroup --name $AppName --output json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Azure Function App.' }
$site = $siteJson | ConvertFrom-Json
$planJson = az appservice plan show --ids $site.serverFarmId --output json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the hosting plan.' }
$plan = $planJson | ConvertFrom-Json
if ($plan.sku.name -ne 'EP1' -or -not $plan.reserved) { throw 'The final decision requires a Linux Premium EP1 plan.' }
if (-not $site.httpsOnly) { throw 'HTTPS-only access is not enabled.' }
[PSCustomObject]@{ App = $site.name; Sku = $plan.sku.name; Linux = $plan.reserved; HttpsOnly = $site.httpsOnly }
