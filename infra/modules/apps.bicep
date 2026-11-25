// The gateway, the six module services and the jobs (ADR 0015, ADR 0020, STAGE_4_PLAN 8.5): 0.25 vCPU / 0.5 GiB each,
// scale to zero, at most one replica, single revision mode. Secrets are Key Vault references resolved with each app's
// own identity; the values are put into Key Vault by hand (stage 10, #85).
//
// Key Vault secrets:
//   db-<service>, db-migrator         connection strings (one login per service, ADR 0015 §5)
//   messaging, rabbitmq-management    CloudAMQP's AMQPS URL and management URL (ADR 0017, KEDA http mode)
//   internal-token-signing-key        PEM of the gateway's ES256 key; internal-token-jwks its public JWKS (ADR 0016)
//   otlp-endpoint, otlp-headers       Grafana Cloud's OTLP gateway and its Authorization header (ADR 0018)

param location string
param imageTag string
param environmentId string
param registryServer string
param keyVaultUri string
@description('The identities of platform.bicep, one per app and job: { name, id, clientId }.')
param identityList array
param dataProtectionBlobUri string
param dataProtectionKeyId string
param deployBrokerMetricsJob bool

var identities = toObject(identityList, entry => entry.name)

// Services whose consumers wake on their queues (KEDA rabbitmq scaler in http mode, STAGE_4_PLAN 5.6).
var consumers = ['inventory']
var services = ['identity', 'inventory', 'fleet', 'procurement', 'forecasting', 'reporting']

var resources = {
  cpu: json('0.25')
  memory: '0.5Gi'
}

func secret(vaultUri string, name string, identityId string) object => {
  name: name
  keyVaultUrl: '${vaultUri}secrets/${name}'
  identity: identityId
}

func telemetry(app string, tag string) array => [
  { name: 'OTEL_SERVICE_NAME', value: app }
  { name: 'OTEL_RESOURCE_ATTRIBUTES', value: 'deployment.environment.name=prod,service.version=${tag}' }
  { name: 'OTEL_EXPORTER_OTLP_PROTOCOL', value: 'http/protobuf' }
  { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', secretRef: 'otlp-endpoint' }
  { name: 'OTEL_EXPORTER_OTLP_HEADERS', secretRef: 'otlp-headers' }
]

// Probes on the ServiceDefaults endpoints; the start-up probe tolerates a cold start (STAGE_4_PLAN 8.7).
var probes = [
  {
    type: 'Startup'
    httpGet: { path: '/health/live', port: 8080 }
    periodSeconds: 2
    failureThreshold: 30
  }
  {
    type: 'Liveness'
    httpGet: { path: '/health/live', port: 8080 }
    periodSeconds: 30
  }
  {
    type: 'Readiness'
    httpGet: { path: '/health/ready', port: 8080 }
    periodSeconds: 10
  }
]

resource gateway 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'gateway'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities.gateway.id}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [{ server: registryServer, identity: identities.gateway.id }]
      secrets: [
        secret(keyVaultUri, 'internal-token-signing-key', identities.gateway.id)
        secret(keyVaultUri, 'otlp-endpoint', identities.gateway.id)
        secret(keyVaultUri, 'otlp-headers', identities.gateway.id)
      ]
    }
    template: {
      containers: [
        {
          name: 'gateway'
          image: '${registryServer}/smartspare/gateway:${imageTag}'
          resources: resources
          probes: probes
          env: concat(telemetry('gateway', imageTag), [
            { name: 'AZURE_CLIENT_ID', value: identities.gateway.clientId }
            { name: 'InternalTokens__SigningKey', secretRef: 'internal-token-signing-key' }
            { name: 'DataProtection__BlobUri', value: dataProtectionBlobUri }
            { name: 'DataProtection__KeyVaultKeyId', value: dataProtectionKeyId }
            // Only the ingress reaches the gateway; trust its rightmost X-Forwarded-For (STAGE_4_PLAN 2.5, #48).
            { name: 'ForwardedHeaders__TrustContainerAppsIngress', value: 'true' }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

resource service 'Microsoft.App/containerApps@2024-03-01' = [for name in services: {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities[name].id}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // Internal only: reachable from the gateway (and other services) by app name, never from the internet.
      ingress: {
        external: false
        targetPort: 8080
        transport: 'auto'
      }
      registries: [{ server: registryServer, identity: identities[name].id }]
      secrets: concat([
        secret(keyVaultUri, 'db-${name}', identities[name].id)
        secret(keyVaultUri, 'messaging', identities[name].id)
        secret(keyVaultUri, 'internal-token-jwks', identities[name].id)
        secret(keyVaultUri, 'otlp-endpoint', identities[name].id)
        secret(keyVaultUri, 'otlp-headers', identities[name].id)
      ], contains(consumers, name) ? [secret(keyVaultUri, 'rabbitmq-management', identities[name].id)] : [])
    }
    template: {
      containers: [
        {
          name: name
          image: '${registryServer}/smartspare/${name}:${imageTag}'
          resources: resources
          probes: probes
          env: concat(telemetry(name, imageTag), [
            { name: 'AZURE_CLIENT_ID', value: identities[name].clientId }
            { name: 'ConnectionStrings__smartspare', secretRef: 'db-${name}' }
            { name: 'ConnectionStrings__messaging', secretRef: 'messaging' }
            { name: 'InternalTokens__Jwks', secretRef: 'internal-token-jwks' }
          ])
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: concat([
          {
            name: 'http'
            http: { metadata: { concurrentRequests: '20' } }
          }
        ], contains(consumers, name) ? [
          {
            // Wakes the consumer when one of its queues has messages, through the management API: no AMQP connection
            // per poll (STAGE_4_PLAN 5.6, verified on the free plan in BROKER.md; checked behind Container Apps in #85).
            name: 'queues'
            custom: {
              type: 'rabbitmq'
              metadata: {
                protocol: 'http'
                mode: 'QueueLength'
                value: '1'
                activationValue: '0'
                useRegex: 'true'
                queueName: '^${name}\\..*'
              }
              auth: [{ secretRef: 'rabbitmq-management', triggerParameter: 'host' }]
            }
          }
        ] : [])
      }
    }
  }
}]

// ADR 0020: one manual job; each pipeline starts it with its commit's image and `migrate --module <name>`
// (tools/run-migration-job.sh). It connects as smartspare_migrator.
resource migrations 'Microsoft.App/jobs@2024-03-01' = {
  name: 'migrations'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities.migrations.id}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [{ server: registryServer, identity: identities.migrations.id }]
      secrets: [
        secret(keyVaultUri, 'db-migrator', identities.migrations.id)
        secret(keyVaultUri, 'otlp-endpoint', identities.migrations.id)
        secret(keyVaultUri, 'otlp-headers', identities.migrations.id)
      ]
    }
    template: {
      containers: [
        {
          name: 'migrations'
          image: '${registryServer}/smartspare/migrations:${imageTag}'
          args: ['migrate']
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat(telemetry('migrations', imageTag), [
            { name: 'Migrations__ConnectionString', secretRef: 'db-migrator' }
          ])
        }
      ]
    }
  }
}

// STAGE_4_PLAN 6.2: every 15 minutes (UTC), in the Reporting image; the job itself comes with 4j and its schedule with #85.
resource brokerMetrics 'Microsoft.App/jobs@2024-03-01' = if (deployBrokerMetricsJob) {
  name: 'broker-metrics'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities.reporting.id}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 300
      replicaRetryLimit: 0
      scheduleTriggerConfig: {
        cronExpression: '*/15 * * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: [{ server: registryServer, identity: identities.reporting.id }]
      secrets: [
        secret(keyVaultUri, 'db-reporting', identities.reporting.id)
        secret(keyVaultUri, 'messaging', identities.reporting.id)
        secret(keyVaultUri, 'rabbitmq-management', identities.reporting.id)
        secret(keyVaultUri, 'internal-token-jwks', identities.reporting.id)
        secret(keyVaultUri, 'otlp-endpoint', identities.reporting.id)
        secret(keyVaultUri, 'otlp-headers', identities.reporting.id)
      ]
    }
    template: {
      containers: [
        {
          name: 'broker-metrics'
          image: '${registryServer}/smartspare/reporting:${imageTag}'
          args: ['--job', 'reporting.broker-metrics']
          resources: resources
          env: concat(telemetry('broker-metrics', imageTag), [
            { name: 'ConnectionStrings__smartspare', secretRef: 'db-reporting' }
            { name: 'ConnectionStrings__messaging', secretRef: 'messaging' }
            { name: 'RabbitMq__ManagementUrl', secretRef: 'rabbitmq-management' }
            { name: 'InternalTokens__Jwks', secretRef: 'internal-token-jwks' }
          ])
        }
      ]
    }
  }
}

output gatewayUrl string = 'https://${gateway.properties.configuration.ingress.fqdn}'
