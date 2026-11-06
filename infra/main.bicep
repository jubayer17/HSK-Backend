targetScope = 'resourceGroup'

param location string = resourceGroup().location
param appName string
param containerImage string
param keyVaultName string
param databaseSecretName string
param emailSecretName string
param emailSenderAddress string
param registryName string = ''

// Final decision: EP1 (3.5 GB minimum), not a Consumption plan, for memory-heavy PDF processing.
resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${appName}-ep1'
  location: location
  kind: 'elastic'
  sku: { name: 'EP1', tier: 'ElasticPremium', capacity: 1 }
  properties: { reserved: true }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'hsk${uniqueString(resourceGroup().id, appName)}'
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: { minimumTlsVersion: 'TLS1_2', allowBlobPublicAccess: false, supportsHttpsTrafficOnly: true }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${appName}-insights'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web' }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = if (!empty(registryName)) { name: registryName }

resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'functionapp,linux,container'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOCKER|${containerImage}'
      acrUseManagedIdentityCreds: !empty(registryName)
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'Storage__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
        { name: 'Database__ConnectionString', value: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/${databaseSecretName}/)' }
        { name: 'Database__MigrateOnStartup', value: 'true' }
        { name: 'Database__CreateIfMissing', value: 'false' }
        { name: 'Auth__SecureCookie', value: 'true' }
        { name: 'PdfParser__Timeout', value: '00:00:30' }
        { name: 'PdfParser__MaxPages', value: '100' }
        { name: 'Email__Mode', value: 'Azure' }
        { name: 'Email__ConnectionString', value: '@Microsoft.KeyVault(SecretUri=${vault.properties.vaultUri}secrets/${emailSecretName}/)' }
        { name: 'Email__From', value: emailSenderAddress }
        { name: 'WEBSITE_TIME_ZONE', value: 'Europe/Berlin' }
        { name: 'TZ', value: 'Europe/Berlin' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
      ]
    }
  }
}

// Durable Functions uses blobs, queues and tables. Runtime managed identity avoids storage keys in settings.
var storageRoles = [
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner (host locks and PDF containers)
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
]
resource storageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in storageRoles: {
  name: guid(storage.id, app.id, role)
  scope: storage
  properties: { principalId: app.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role) }
}]
resource secretAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, app.id, 'secrets')
  scope: vault
  properties: {
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}
resource registryAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(registryName)) {
  name: guid(registry!.id, app.id, 'pull')
  scope: registry
  properties: {
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

output functionAppName string = app.name
output healthUrl string = 'https://${app.properties.defaultHostName}/api/health'
output hostingSku string = plan.sku.name
