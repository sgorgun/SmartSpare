// Shared platform of the environment (STAGE_4_PLAN 8.5, 8.8): managed identities, registry, Key Vault, storage for the
// gateway's key ring, PostgreSQL 17 and the Container Apps environment.

@minLength(5)
param name string
param location string
param postgresAdminLogin string

@secure()
param postgresAdminPassword string

// One user-assigned identity per app and job (ADR 0009, 0016): image pulls, Key Vault references, and later Entra
// logins to PostgreSQL (#16). The migration job has its own.
var apps = ['gateway', 'identity', 'inventory', 'fleet', 'procurement', 'forecasting', 'reporting', 'migrations']
var compactName = replace(name, '-', '')

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for app in apps: {
  name: 'id-${name}-${app}'
  location: location
}]

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  // name has at least 5 characters (minLength); Bicep can't infer the length after replace().
  #disable-next-line BCP334
  name: 'acr${compactName}'
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

// AcrPull for every identity.
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (app, i) in apps: {
  name: guid(registry.id, app, 'AcrPull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity[i].properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${name}'
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
  }
}

// Key Vault Secrets User for every identity: the apps' secrets are Key Vault references (ADR 0009, 8.8).
resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (app, i) in apps: {
  name: guid(vault.id, app, 'KeyVaultSecretsUser')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: identity[i].properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

// The key that wraps the gateway's Data Protection key ring (ADR 0016 section 6).
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
  }
}

resource gatewayCrypto 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataProtectionKey.id, 'gateway', 'KeyVaultCryptoServiceEncryptionUser')
  scope: dataProtectionKey
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
    principalId: identity[0].properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  // name has at least 5 characters (minLength); Bicep can't infer the length after replace().
  #disable-next-line BCP334
  name: 'st${compactName}'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource keyRing 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'dataprotection'
}

// Storage Blob Data Contributor on the key-ring container, for the gateway only.
resource gatewayBlobs 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyRing.id, 'gateway', 'StorageBlobDataContributor')
  scope: keyRing
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: identity[0].properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// PostgreSQL 17, Burstable B1ms, 32 GiB (ADR 0009). 35 user connections; the apps use at most 26 (STAGE_4_PLAN 8.5).
resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-${name}'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '17'
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: {
      storageSizeGB: 32
      autoGrow: 'Disabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Enabled'
    }
  }
}

// Only Azure services reach the server (the Container Apps environment has no fixed outbound address on Consumption).
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Workload profiles environment, Consumption only; no Log Analytics: logs go to Grafana Cloud over OTLP (ADR 0018).
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}'
  location: location
  properties: {
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    appLogsConfiguration: {
      destination: 'none'
    }
  }
}

output environmentId string = environment.id
output registryServer string = registry.properties.loginServer
output keyVaultUri string = vault.properties.vaultUri
output dataProtectionBlobUri string = '${storage.properties.primaryEndpoints.blob}${keyRing.name}/keys.xml'
output dataProtectionKeyId string = dataProtectionKey.properties.keyUri
output postgresHost string = postgres.properties.fullyQualifiedDomainName
output identities array = [for (app, i) in apps: {
  name: app
  id: identity[i].id
  clientId: identity[i].properties.clientId
}]
