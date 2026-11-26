output "dashboards" {
  description = "Uid of every provisioned dashboard."
  value       = sort([for dashboard in grafana_dashboard.smartspare : dashboard.uid])
}

output "rule_groups" {
  description = "Rule groups and their number of rules."
  value       = { for name, group in grafana_rule_group.smartspare : name => length(group.rule) }
}

output "telegram" {
  description = "Whether the contact point includes Telegram."
  value       = nonsensitive(var.telegram_bot_token != "")
}
