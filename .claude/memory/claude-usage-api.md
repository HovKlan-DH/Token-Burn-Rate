---
name: claude-usage-api
description: Claude plan limits come from the OAuth usage endpoint, never from transcript math
metadata:
  type: reference
---

Claude's real plan utilization — the same numbers the claude.ai Usage screen shows — comes
from:

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <claudeAiOauth.accessToken from ~/.claude/.credentials.json>
anthropic-beta: oauth-2025-04-20
```

Response carries a `limits` array of `{kind, group, percent, severity, resets_at,
is_active}` (kinds seen: `session`, `weekly_all`; Max plans add weekly Opus/Sonnet), plus
flat `five_hour` / `seven_day` objects with `utilization` and `resets_at` as a fallback.
`api/oauth/profile` returns account and organization details including `rate_limit_tier`.

**Do not compute these percentages from transcripts.** An earlier version did and was
badly wrong (3% vs 20% session, 70% vs 40% weekly), for two independent reasons:

- Anthropic publishes no token ceiling, so the denominator had to be invented.
- The real limits reset at **fixed times**; a transcript calculation can only do a rolling
  lookback, which keeps counting usage a reset already discarded.
- Limits can be temporarily boosted (e.g. +50% promotions), invisible to local math.

Transcripts remain the only source of absolute token counts and burn rate, which the API
does not expose — use them for that alone.

**Why:** Discovered when the user compared the widget against their Usage screen.

**How to apply:** Read the credentials file, never write it — Claude Code owns it and
refreshes the ~8h token itself; writing risks corrupting a running CLI's session. Report an
expired token instead. Generate bars from the `limits` array rather than hardcoded field
names so new limit kinds appear automatically. Related: [[token-burn-rate-widget]].
