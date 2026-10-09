---
name: debug-focused
description: Debug one SmartVisioSys issue with low Copilot usage.
disable-model-invocation: true
argument-hint: "<bug description and 1-3 relevant files if known>"
---
Debug this issue with minimal context usage.

Process:
1. Inspect only the most likely files first.
2. List hypotheses and evidence.
3. Suggest the smallest diagnostic step or patch.

Do not scan the whole repository unless explicitly requested.
