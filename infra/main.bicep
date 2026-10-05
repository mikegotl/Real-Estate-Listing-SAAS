targetScope = 'resourceGroup'

@description('Short lowercase prefix used to name production resources.')
@minLength(3)
@maxLength(12)
param namePrefix string

@description('Azure region for every resource.')
param location string = resourceGroup().location

@description('PostgreSQL administrator login. Applications use this only until a least-privilege runtime role is introduced.')
param postgresAdministratorLogin string = 'listingstudioadmin'

@secure()
@description('PostgreSQL administrator password. Supply from the protected GitHub environment; never commit it.')
param postgresAdministratorPassword string

@description('Immutable Web image reference, normally the ACR image tagged with the Git commit SHA.')
param webImage string = ''

@description('Immutable Worker image reference, normally the ACR image tagged with the Git commit SHA.')
param workerImage string = ''

@description('Immutable migration image reference, normally the ACR image tagged with the Git commit SHA.')
param migrationImage string = ''

@description('Create or update the Container Apps only after all immutable images have been pushed.')
param deployApplications bool = false

var normalizedPrefix = take(replace(toLower(namePrefix), '-', ''), 12)
var uniqueSuffix = uniqueString(subscription().subscriptionId, resourceGroup().id)
var resourcePrefix = '${toLower(namePrefix)}-${take(uniqueSuffix, 6)}'
var storageName = take('${normalizedPrefix}${uniqueSuffix}', 24)
var registryName = take('${normalizedPrefix}${uniqueSuffix}acr', 50)
var keyVaultName = take('${normalizedPrefix}-${take(uniqueSuffix, 6)}-kv', 24)
var postgresName = take('${resourcePrefix}-pg', 63)
var applicationTags = {
  application: 'listing-studio'
  environment: 'production'
  managedBy: 'bicep'
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${resourcePrefix}-vnet'
  location: location
  tags: applicationTags
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.40.0.0/16'
      ]
    }
  }
}

resource containerAppsSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: virtualNetwork
  name: 'container-apps'
  properties: {
    addressPrefix: '10.40.0.0/23'
    delegations: [
      {
        name: 'Microsoft.App.environments'
        properties: {
          serviceName: 'Microsoft.App/environments'
        }
      }
    ]
    serviceEndpoints: [
      {
        service: 'Microsoft.Storage'
      }
      {
        service: 'Microsoft.KeyVault'
      }
    ]
  }
}

resource postgresSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: virtualNetwork
  name: 'postgresql'
  properties: {
    addressPrefix: '10.40.2.0/24'
    delegations: [
      {
        name: 'Microsoft.DBforPostgreSQL.flexibleServers'
        properties: {
          serviceName: 'Microsoft.DBforPostgreSQL/flexibleServers'
        }
      }
    ]
  }
}

resource postgresPrivateDns 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.postgres.database.azure.com'
  location: 'global'
  tags: applicationTags
}

resource postgresDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: postgresPrivateDns
  name: '${resourcePrefix}-postgres-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresName
  location: location
  tags: applicationTags
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: postgresAdministratorLogin
    administratorLoginPassword: postgresAdministratorPassword
    version: '16'
    availabilityZone: '1'
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      delegatedSubnetResourceId: postgresSubnet.id
      privateDnsZoneArmResourceId: postgresPrivateDns.id
      publicNetworkAccess: 'Disabled'
    }
    storage: {
      storageSizeGB: 32
      autoGrow: 'Enabled'
    }
    authConfig: {
      activeDirectoryAuth: 'Disabled'
      passwordAuth: 'Enabled'
    }
  }
  dependsOn: [
    postgresDnsLink
  ]
}

resource listingStudioDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgresServer
  name: 'listingstudio'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageName
  location: location
  tags: applicationTags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource assetContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobService
  name: 'listingstudio-assets'
  properties: {
    publicAccess: 'None'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${resourcePrefix}-logs'
  location: location
  tags: applicationTags
  properties: {
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${resourcePrefix}-insights'
  location: location
  kind: 'web'
  tags: applicationTags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    DisableLocalAuth: true
    IngestionMode: 'LogAnalytics'
    RetentionInDays: 30
  }
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: applicationTags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    dataEndpointEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${resourcePrefix}-runtime'
  location: location
  tags: applicationTags
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: applicationTags
  properties: {
    enablePurgeProtection: true
    enableRbacAuthorization: true
    enableSoftDelete: true
    publicNetworkAccess: 'Enabled'
    sku: {
      family: 'A'
      name: 'standard'
    }
    softDeleteRetentionInDays: 90
    tenantId: subscription().tenantId
  }
}

resource postgresPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'postgresql-admin-password'
  properties: {
    value: postgresAdministratorPassword
  }
}

var acrPullRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var blobContributorRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var keyVaultSecretsUserRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4633458b-17de-408a-b874-0445c86b69e6')
var monitoringMetricsPublisherRole = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '3913510d-42f4-4e42-8a64-420c390055eb')

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, runtimeIdentity.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRole
  }
}

resource blobAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, runtimeIdentity.id, blobContributorRole)
  scope: storageAccount
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobContributorRole
  }
}

resource keyVaultAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, runtimeIdentity.id, keyVaultSecretsUserRole)
  scope: keyVault
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRole
  }
}

resource monitoringMetricsPublisherAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, runtimeIdentity.id, monitoringMetricsPublisherRole)
  scope: applicationInsights
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: monitoringMetricsPublisherRole
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: '${resourcePrefix}-cae'
  location: location
  tags: applicationTags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: containerAppsSubnet.id
      internal: false
    }
    zoneRedundant: false
  }
}

var databaseEnvironment = [
  {
    name: 'PostgreSQL__Host'
    value: postgresServer.properties.fullyQualifiedDomainName
  }
  {
    name: 'PostgreSQL__Port'
    value: '5432'
  }
  {
    name: 'PostgreSQL__Database'
    value: listingStudioDatabase.name
  }
  {
    name: 'PostgreSQL__Username'
    value: postgresAdministratorLogin
  }
  {
    name: 'PostgreSQL__Password'
    secretRef: 'postgres-password'
  }
]

var sharedEnvironment = concat(databaseEnvironment, [
  {
    name: 'DOTNET_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: applicationInsights.properties.ConnectionString
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: runtimeIdentity.properties.clientId
  }
  {
    name: 'AzureBlobStorage__Provider'
    value: 'Azure'
  }
  {
    name: 'AzureBlobStorage__ServiceUri'
    value: storageAccount.properties.primaryEndpoints.blob
  }
  {
    name: 'AzureBlobStorage__ContainerName'
    value: assetContainer.name
  }
  {
    name: 'FFmpeg__ExecutablePath'
    value: '/usr/bin/ffmpeg'
  }
  {
    name: 'FFmpeg__ProbeExecutablePath'
    value: '/usr/bin/ffprobe'
  }
])

var keyVaultSecrets = [
  {
    name: 'postgres-password'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/${postgresPasswordSecret.name}'
    identity: runtimeIdentity.id
  }
]

var registryConfiguration = [
  {
    server: containerRegistry.properties.loginServer
    identity: runtimeIdentity.id
  }
]

resource webApp 'Microsoft.App/containerApps@2025-01-01' = if (deployApplications) {
  name: '${resourcePrefix}-web'
  location: location
  tags: applicationTags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Multiple'
      ingress: {
        allowInsecure: false
        external: true
        stickySessions: {
          affinity: 'sticky'
        }
        targetPort: 8080
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
        transport: 'http'
      }
      registries: registryConfiguration
      secrets: keyVaultSecrets
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          env: concat(sharedEnvironment, [
            {
              name: 'OTEL_SERVICE_NAME'
              value: 'listingstudio-web'
            }
          ])
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 3
              periodSeconds: 5
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 10
              failureThreshold: 6
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 2
        rules: [
          {
            name: 'http'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
  dependsOn: [
    acrPullAssignment
    blobAssignment
    keyVaultAssignment
    monitoringMetricsPublisherAssignment
  ]
}

resource workerApp 'Microsoft.App/containerApps@2025-01-01' = if (deployApplications) {
  name: '${resourcePrefix}-worker'
  location: location
  tags: applicationTags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registryConfiguration
      secrets: keyVaultSecrets
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage
          env: concat(sharedEnvironment, [
            {
              name: 'OTEL_SERVICE_NAME'
              value: 'listingstudio-worker'
            }
          ])
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 3
              periodSeconds: 5
              failureThreshold: 30
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/health/live'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health/ready'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 10
              failureThreshold: 6
            }
          ]
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    acrPullAssignment
    blobAssignment
    keyVaultAssignment
    monitoringMetricsPublisherAssignment
  ]
}

resource migrationJob 'Microsoft.App/jobs@2025-01-01' = if (deployApplications) {
  name: '${resourcePrefix}-migrations'
  location: location
  tags: applicationTags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentity.id}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1800
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      registries: registryConfiguration
      secrets: keyVaultSecrets
    }
    template: {
      containers: [
        {
          name: 'migrations'
          image: migrationImage
          env: databaseEnvironment
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
    }
  }
  dependsOn: [
    acrPullAssignment
    keyVaultAssignment
  ]
}

output acrName string = containerRegistry.name
output acrLoginServer string = containerRegistry.properties.loginServer
output applicationInsightsName string = applicationInsights.name
output keyVaultName string = keyVault.name
output migrationJobName string = deployApplications ? migrationJob.name : ''
output postgresServerName string = postgresServer.name
output webAppName string = deployApplications ? webApp.name : ''
output webUrl string = deployApplications ? 'https://${webApp!.properties.configuration.ingress.fqdn}' : ''
output workerAppName string = deployApplications ? workerApp.name : ''
