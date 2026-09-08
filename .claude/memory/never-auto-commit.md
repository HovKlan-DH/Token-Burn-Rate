---
name: never-auto-commit
description: Never run git commit unless the user explicitly asks; they commit their own work
metadata:
  type: feedback
---

Never create git commits on your own initiative. Make the file changes and stop there,
leaving them in the working tree. The user runs `git commit` themselves, and will ask
directly on the occasions they want you to do it.

This covers `git commit` in every form, including amending. Staging with `git add` is
likewise not something to do unprompted.

**Why:** The user wants control over their own commit history — what gets grouped into a
commit, when, and under whose authorship is their call, not something to be decided for
them. Committing unasked takes that decision away and leaves them undoing work.

**How to apply:** After finishing a change, report what was modified and leave it uncommitted.
Do not offer a commit as the natural next step or treat "the work is done" as implying it
should be committed. If a commit genuinely seems warranted, ask first and accept no as the
answer. When the user does ask, follow the repo's attribution conventions as normal.
Related: [[token-burn-rate-widget]].
