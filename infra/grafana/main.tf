locals {
  observability = "${path.module}/../observability"
  dashboards    = "${local.observability}/dashboards"
  rule_file     = jsondecode(file("${local.observability}/grafana/provisioning/alerting/rules.json"))

  # The files carry the local data source uids; the stack's own uids replace them.
  datasource_uids = {
    prometheus = var.prometheus_datasource_uid
    usage      = var.usage_datasource_uid
  }

  # Group intervals in the file are durations ("1m"); the provider wants seconds.
  interval_seconds = { "1m" = 60, "5m" = 300, "10m" = 600 }
}

resource "grafana_folder" "smartspare" {
  uid   = "smartspare"
  title = "SmartSpare"
}

# The dashboards of infra/observability/dashboards, the same files the local Grafana loads.
resource "grafana_dashboard" "smartspare" {
  for_each = fileset(local.dashboards, "*.json")

  folder    = grafana_folder.smartspare.uid
  overwrite = true
  config_json = replace(
    replace(file("${local.dashboards}/${each.value}"), "\"uid\": \"prometheus\"", "\"uid\": \"${var.prometheus_datasource_uid}\""),
    "\"uid\": \"usage\"",
    "\"uid\": \"${var.usage_datasource_uid}\"",
  )
}

# The 8 rules in 3 groups of ADR 0018 §4, from the local provisioning file.
resource "grafana_rule_group" "smartspare" {
  for_each = { for group in local.rule_file.groups : group.name => group }

  name             = each.value.name
  folder_uid       = grafana_folder.smartspare.uid
  interval_seconds = local.interval_seconds[each.value.interval]

  dynamic "rule" {
    for_each = each.value.rules

    content {
      uid            = rule.value.uid
      name           = rule.value.title
      condition      = rule.value.condition
      for            = rule.value.for
      no_data_state  = rule.value.noDataState
      exec_err_state = rule.value.execErrState
      labels         = rule.value.labels
      annotations    = rule.value.annotations

      dynamic "data" {
        for_each = rule.value.data

        content {
          ref_id         = data.value.refId
          datasource_uid = lookup(local.datasource_uids, data.value.datasourceUid, data.value.datasourceUid)
          model          = jsonencode(data.value.model)

          relative_time_range {
            from = try(data.value.relativeTimeRange.from, 0)
            to   = try(data.value.relativeTimeRange.to, 0)
          }
        }
      }
    }
  }
}

# One contact point with every channel: e-mail always, Telegram when a bot token is given (ADR 0018 §4).
resource "grafana_contact_point" "smartspare" {
  name = "SmartSpare"

  email {
    addresses    = [var.alert_email]
    single_email = true
  }

  dynamic "telegram" {
    for_each = var.telegram_bot_token == "" ? [] : [1]

    content {
      token   = var.telegram_bot_token
      chat_id = var.telegram_chat_id
    }
  }
}

# The stack's notification policy tree. The stack serves only SmartSpare, so its root routes to the contact point.
resource "grafana_notification_policy" "smartspare" {
  contact_point   = grafana_contact_point.smartspare.name
  group_by        = ["grafana_folder", "alertname"]
  group_wait      = "30s"
  group_interval  = "5m"
  repeat_interval = "4h"
}

# The acceptance test of 4j: a rule that fires at once and travels the same policy and contact point as the real rules.
resource "grafana_rule_group" "test" {
  count = var.test_alert ? 1 : 0

  name             = "smartspare-test"
  folder_uid       = grafana_folder.smartspare.uid
  interval_seconds = 60

  rule {
    uid            = "ss-test-alert"
    name           = "SmartSpare test alert"
    condition      = "C"
    for            = "0s"
    no_data_state  = "Alerting"
    exec_err_state = "Alerting"
    labels         = { severity = "info", system = "smartspare" }
    annotations    = { summary = "Test of alert delivery (STAGE_4_PLAN 4j). Remove with test_alert = false." }

    data {
      ref_id         = "A"
      datasource_uid = var.prometheus_datasource_uid
      model          = jsonencode({ refId = "A", expr = "vector(1)", instant = true })

      relative_time_range {
        from = 60
        to   = 0
      }
    }

    data {
      ref_id         = "C"
      datasource_uid = "__expr__"
      model = jsonencode({
        refId      = "C"
        type       = "threshold"
        expression = "A"
        conditions = [{ evaluator = { type = "gt", params = [0] } }]
      })

      relative_time_range {
        from = 0
        to   = 0
      }
    }
  }
}
