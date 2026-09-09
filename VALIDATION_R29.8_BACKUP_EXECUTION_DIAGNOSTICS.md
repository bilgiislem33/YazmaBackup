# R29.8 Backup Execution Diagnostics

## Problem

Newly queued backup commands were rendered as failed before an Agent had completed
them. Because no result existed yet, the detail panel also showed no useful error.

## Changes

- `backup-history` now returns `succeeded: null` while `CompletedAtUtc` is empty.
- Agent error text is exposed only after command completion.
- Operations history refreshes every five seconds and refreshes the open detail.
- The detail panel shows the effective NAS target, including the owner folder.
- Pending, failed and successful executions are visually distinct.

## Acceptance

1. Start a backup and immediately open Operations.
2. Confirm its state is `Bekliyor`, not `Başarısız`.
3. Keep the detail open and confirm it updates without a page refresh.
4. Confirm `Gerçek NAS hedef klasörü` ends with the assigned owner name.
5. For a failure, confirm the Agent error is visible in the red detail panel.
