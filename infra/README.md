# Azure EP1 deployment

`main.bicep` defines a Linux EP1 Function App, Storage, Application Insights and managed-identity permissions.
The template is deployment configuration, not evidence of a live Azure deployment.

Prerequisites: an Azure subscription/resource group, an existing Azure SQL database, an RBAC-enabled
Key Vault in the same resource group, and an Azure Communication Services resource linked to a verified email domain.
Store the SQL connection string and ACS connection string as separate Key Vault secrets. Never commit their values.
Create the initial app users through the existing secure seed configuration or provision them before enabling access.

Build/push the Docker image to your registry. For a private Azure Container Registry in the same resource group,
pass its name as `registryName`; the template grants the app AcrPull. Configure SQL firewall/network access separately.

```powershell
az deployment group what-if --resource-group YOUR_GROUP --template-file infra/main.bicep --parameters appName=YOUR_APP containerImage=YOUR_IMAGE keyVaultName=YOUR_VAULT databaseSecretName=YOUR_SQL_SECRET emailSecretName=YOUR_ACS_SECRET emailSenderAddress=YOUR_VERIFIED_SENDER registryName=YOUR_ACR
```

Review the result before running `az deployment group create` with the same arguments. EP1 incurs Azure charges.
After deployment run `tools/Test-AzureHosting.ps1 -ResourceGroup YOUR_GROUP -AppName YOUR_APP`.
Then verify backend health, a real PDF run and delivery to an intended test mailbox. SDK acceptance is not proof of inbox arrival.

No Azure CLI/account was available during this update, so cloud deployment and live email remain unverified.
