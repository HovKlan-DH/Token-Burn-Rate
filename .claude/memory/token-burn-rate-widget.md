---
name: token-burn-rate-widget
description: "Token-Burn-Rate runs on two machines (home personal, work org-assigned) and must adapt to both"
metadata: 
  node_type: memory
  type: project
  originSessionId: cb08cc4e-fab6-4654-abbd-4f77dd8930a9
  modified: 2026-09-08T06:20:42.074Z
---

Token-Burn-Rate is an Avalonia always-on-top widget showing live Claude Code and GitHub
Copilot usage, published as one self-contained executable per OS.

The user runs it on **two machines** and it must work on both from the same binary:

- **Home** — GitHub Copilot free individual plan (`free_limited_copilot`), no orgs.
  Premium-interactions entitlement is 0, so that bar renders dimmed as "not included in
  plan".
- **Work** — GitHub Copilot seat assigned by an organisation (business/enterprise).
  Populates `organization_login_list` and a real premium-interactions entitlement, so the
  third bar becomes live. Enterprise reports unlimited completions/chat with entitlement
  0, rendered as ∞.

The work machine also has M365 Copilot on a separate pool — deliberately not shown, see
[[m365-copilot-no-usage-api]].

Claude bars come from Anthropic's own usage endpoint, not from transcript math — see
[[claude-usage-api]].

**Why:** The home machine's free personal plan made GitHub Copilot and "Copilot" look like
one product; they are not. Never generalise the account shape from whichever machine is
being used to develop on.

**How to apply:** Detect plan/org from the endpoint at runtime and hide or dim what is
unavailable — no per-machine config file. When testing Copilot changes at home, exercise
business/enterprise payloads through the parser rather than assuming the local response is
representative.
