# Grafana Cloud as code (STAGE_4_PLAN 6.4, PR 4j): the dashboards and alert rules of infra/observability/, and the
# contact point that delivers the alerts (ADR 0018 §4). The provider reads the stack URL and a service account token
# from GRAFANA_URL and GRAFANA_AUTH; nothing secret is in these files or in a variable default.
terraform {
  required_version = ">= 1.17.0"

  required_providers {
    grafana = {
      source  = "grafana/grafana"
      version = "4.54.0"
    }
  }
}

provider "grafana" {}
