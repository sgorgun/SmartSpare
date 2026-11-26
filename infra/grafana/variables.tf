variable "alert_email" {
  description = "Address that receives the alerts. Set TF_VAR_alert_email; never commit it."
  type        = string
  sensitive   = true

  validation {
    condition     = can(regex("^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$", var.alert_email))
    error_message = "alert_email must be an e-mail address."
  }
}

variable "telegram_bot_token" {
  description = "Token of the Telegram bot that posts the alerts (TF_VAR_telegram_bot_token). Empty: e-mail only."
  type        = string
  default     = ""
  sensitive   = true
}

variable "telegram_chat_id" {
  description = "Chat the bot posts to (TF_VAR_telegram_chat_id). Needed when telegram_bot_token is set."
  type        = string
  default     = ""

  validation {
    condition     = var.telegram_chat_id == "" || can(regex("^-?[0-9]+$", var.telegram_chat_id))
    error_message = "telegram_chat_id is the numeric chat id, for example -1001234567890."
  }
}

variable "prometheus_datasource_uid" {
  description = "Uid of the stack's Prometheus (Mimir) data source; the files use \"prometheus\", the local uid."
  type        = string
  default     = "grafanacloud-prom"
}

variable "usage_datasource_uid" {
  description = "Uid of the stack's usage data source (grafanacloud_instance_active_series); the files use \"usage\"."
  type        = string
  default     = "grafanacloud-usage"
}

variable "test_alert" {
  description = "Adds a rule that always fires, to prove delivery end to end (STAGE_4_PLAN 4j acceptance); apply again without it to remove it."
  type        = bool
  default     = false
}
