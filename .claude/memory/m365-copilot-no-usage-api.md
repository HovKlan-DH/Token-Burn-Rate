---
name: m365-copilot-no-usage-api
description: "M365 Copilot usage is not readable per-user without a tenant admin role, and exposes no quota at all"
metadata: 
  node_type: memory
  type: reference
  originSessionId: cb08cc4e-fab6-4654-abbd-4f77dd8930a9
  modified: 2026-09-08T06:20:30.826Z
---

Microsoft 365 Copilot (Word/Excel/Outlook/Teams) is a separate product from GitHub
Copilot, drawing on a separate quota pool — prompts in Office do not consume GitHub
Copilot quota. Confirmed by the user from their own work usage.

Its usage cannot be surfaced in a desktop widget:

- Only programmatic source is Microsoft Graph
  `getMicrosoft365CopilotUsageUserDetail` (v1.0 and beta).
- Requires `Reports.Read.All` **plus** a tenant admin role (Reports Reader, AI
  Administrator, Company Administrator, Exchange/SharePoint/Teams admin). It returns
  tenant-wide data for all users, hence the gating. A regular developer will not have it.
- Returns **no quota, entitlement, credit balance or remaining allowance** — only
  per-app last-activity dates, plus prompt counts in `version=v2`.
- Daily-refreshed adoption report (`reportRefreshDate`), not a live counter.
- Enterprise M365 Copilot is typically a flat licensed seat, not a metered pool, so a
  "remaining" figure may not be meaningful at all. AI-credit metering applies to the
  consumer Personal/Family/Premium tiers.

**Why:** Investigated for [[token-burn-rate-widget]]; the user has three AI services in
mind but only two have readable usage.

**How to apply:** Do not attempt an M365 Copilot panel or re-research this. If the user
asks again, the options are manual self-tracking or an admin-gated Graph integration
showing prompt counts (not quota).
