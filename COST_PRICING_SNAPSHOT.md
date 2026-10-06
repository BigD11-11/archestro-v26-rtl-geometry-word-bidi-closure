# AI Usage and Cost policy — V1.1.0

The app stores accounting metadata only. It records provider/model, operation and UTC time, request ID, elapsed time, meeting/report IDs, input/cache-hit/cache-miss/output/reasoning/total token counts, immutable price snapshot ID, and calculated USD/SAR. It does not store prompts, transcript text, or response bodies in the cost ledger. Local provider API cost is always zero. Cached report opens do not call a provider and do not append ledger rows.

## Seed snapshots

- DeepSeek `deepseek-flash`, snapshot `deepseek-2026-10-06-v1`: USD per 1M tokens, cache hit off-peak/peak `$0.003 / $0.006`, cache miss `$0.15 / $0.30`, output `$0.60 / $1.20`. Peak is weekdays 01:00–04:00 and 06:00–10:00 UTC, excluding Chinese public holidays. Source: [DeepSeek Models & Pricing](https://api-docs.deepseek.com/quick_start/pricing/).
- FX `usd-sar-2026-10-06-v1`: 3.75 SAR per USD. Source: [Saudi Central Bank exchange-rate policy](https://www.sama.gov.sa/en-US/MediaCenter/News/Pages/news-557.aspx).

DeepSeek documents `prompt_tokens`, cache-hit/cache-miss input, `completion_tokens`, `total_tokens`, response `id`, and optional `reasoning_tokens`; these provider-returned fields are the primary source for cloud usage ([Chat Completions API](https://api-docs.deepseek.com/api/create-chat-completion/)). Rates are timestamped snapshots. Later rate imports append a new snapshot and do not recalculate historical ledger rows.
