# VALIDATION_R5.19.8_ASSIGNED_USER

## Goal
Support domainless workgroups where machine names do not reliably identify the person using the computer.

## Added
- Optional persistent `AssignedUser` field on AgentRecord.
- `PUT /api/v1/admin/agents/{agentId}/assigned-user`.
- Inline editable “Kullanıcı / Sahibi” field in Bilgisayarlar.
- Enter key or Kaydet button persists the value.
- Search matches both machine name and assigned user.
- Agent selectors and Asset 360 display the assigned user when present.

## Compatibility
The new state field is optional and appended with a null default, so existing state schema 11 files remain readable.
Heartbeat updates preserve manually assigned user metadata.
No Agent binary/protocol change is required.
