// Parameters of the production environment (ADR 0009). The image tag, the PostgreSQL admin password and the budget's
// e-mail address come from the workflow (commit SHA, GitHub secret, repository variable), never from this file.
using 'main.bicep'

param environmentName = 'prod'
param location = 'westeurope'
param imageTag = readEnvironmentVariable('IMAGE_TAG', 'bootstrap')
param postgresAdminPassword = readEnvironmentVariable('POSTGRES_ADMIN_PASSWORD', '')
param budgetContactEmails = [readEnvironmentVariable('BUDGET_EMAIL', '')]
param monthlyBudget = 35
param budgetStartDate = '2027-01-01'
param deployBrokerMetricsJob = false
