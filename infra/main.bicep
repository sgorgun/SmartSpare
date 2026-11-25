// SmartSpare in Azure (ADR 0009 amended, ADR 0015–0020, STAGE_4_PLAN 8.5–8.8): one resource group with the Container
// Apps environment, the gateway and six services, the migration job, PostgreSQL 17, ACR, Key Vault and the gateway's
// key-ring storage. Validated with what-if in stage 4; first applied in stage 10 (#85). CloudAMQP and Grafana Cloud
// are created by hand; their secrets go to Key Vault.
targetScope = 'subscription'

@description('Short environment name, part of every resource name.')
@allowed(['prod', 'dev'])
param environmentName string = 'prod'

@description('Azure region; ACR in the same region keeps image pulls short (cold start, STAGE_4_PLAN 8.7).')
param location string = 'westeurope'

@description('Image tag of every app and job: the commit SHA from the pipeline.')
param imageTag string

@description('Server admin of PostgreSQL; used once by the migration job to bootstrap logins (ADR 0020).')
param postgresAdminLogin string = 'smartspareadmin'

@secure()
@description('Server admin password, passed from a GitHub secret; Entra logins replace it in stage 10 (#16).')
param postgresAdminPassword string

@description('Addresses that get the budget alerts (NFR07: at most EUR 35 a month).')
param budgetContactEmails array

@description('Monthly budget in the subscription currency.')
param monthlyBudget int = 35

@description('First day of the budget period (yyyy-MM-01).')
param budgetStartDate string = '2027-01-01'

@description('Deploy the broker metrics cron job (STAGE_4_PLAN 6.2); its schedule starts with stage 10 (#85).')
param deployBrokerMetricsJob bool = false

var name = 'smartspare-${environmentName}'

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${name}'
  location: location
  tags: {
    application: 'smartspare'
    environment: environmentName
  }
}

module platform 'modules/platform.bicep' = {
  scope: group
  params: {
    name: name
    location: location
    postgresAdminLogin: postgresAdminLogin
    postgresAdminPassword: postgresAdminPassword
  }
}

module apps 'modules/apps.bicep' = {
  scope: group
  params: {
    location: location
    imageTag: imageTag
    environmentId: platform.outputs.environmentId
    registryServer: platform.outputs.registryServer
    keyVaultUri: platform.outputs.keyVaultUri
    identityList: platform.outputs.identities
    dataProtectionBlobUri: platform.outputs.dataProtectionBlobUri
    dataProtectionKeyId: platform.outputs.dataProtectionKeyId
    deployBrokerMetricsJob: deployBrokerMetricsJob
  }
}

module budget 'modules/budget.bicep' = {
  scope: group
  params: {
    name: name
    amount: monthlyBudget
    startDate: budgetStartDate
    contactEmails: budgetContactEmails
  }
}

output resourceGroup string = group.name
output registryServer string = platform.outputs.registryServer
output gatewayUrl string = apps.outputs.gatewayUrl
