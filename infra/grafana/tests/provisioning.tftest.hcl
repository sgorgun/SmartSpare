# What would be provisioned, planned against a mock provider: no stack and no credentials needed (CI, grafana.yml).
mock_provider "grafana" {}

variables {
  alert_email = "alerts@example.com"
}

run "dashboards_and_rules_of_infra_observability" {
  command = plan

  assert {
    condition     = length(grafana_dashboard.smartspare) == 5
    error_message = "Every dashboard of infra/observability/dashboards is provisioned."
  }

  assert {
    condition     = length(grafana_rule_group.smartspare) == 3 && sum([for g in grafana_rule_group.smartspare : length(g.rule)]) == 8
    error_message = "The 8 rules in 3 groups of ADR 0018 §4."
  }

  assert {
    condition     = alltrue(flatten([for g in grafana_rule_group.smartspare : [for r in g.rule : [for d in r.data : contains(["grafanacloud-prom", "grafanacloud-usage", "__expr__"], d.datasource_uid)]]]))
    error_message = "Every query uses the stack's data sources, never the local uids."
  }

  assert {
    condition     = !strcontains(grafana_dashboard.smartspare["smartspare-free-tier.json"].config_json, "\"uid\": \"prometheus\"") && strcontains(grafana_dashboard.smartspare["smartspare-free-tier.json"].config_json, "grafanacloud-usage")
    error_message = "Dashboards point at the stack's data sources."
  }

  assert {
    condition     = length(grafana_contact_point.smartspare.telegram) == 0 && length(grafana_rule_group.test) == 0
    error_message = "Without a bot token: e-mail only, and no test alert unless asked for."
  }
}

run "telegram_and_test_alert_when_asked_for" {
  command = plan

  variables {
    telegram_bot_token = "123456:mock"
    telegram_chat_id   = "-1001234567890"
    test_alert         = true
  }

  assert {
    condition     = length(grafana_contact_point.smartspare.telegram) == 1 && length(grafana_rule_group.test) == 1
    error_message = "A bot token adds Telegram; test_alert adds the always-firing rule."
  }
}
