# CSIT × KM accepted-job reanalysis contract proposal

Status: proposal only. CSIT does not expose a reanalysis action until KM and CSIT have an approved, tested end-to-end contract.

## Current boundary

- CSIT's manual retry endpoint requeues only a failed formal test-report delivery **before** KM accepts it (`KmJobId == null`).
- A failed Outbox with a KM Job ID is a persisted KM job. Its `CallbackProcessing` and `CallbackCompleted` transitions reject `Failed`; requeuing and redispatching that Outbox would risk a second job and conflicting callbacks.
- The current CSIT integration client has ingestion and callback contracts, but no accepted-job reanalysis request/response contract.

## Minimum agreed contract

1. **Authority:** CSIT authorizes only its existing administrative write role and records the actor and reason. KM authenticates CSIT with the existing machine-to-machine mechanism and a dedicated reanalysis permission. KM never trusts a browser request directly.
2. **Identity:** Request names the original CSIT Outbox ID, original KM Job ID, source type and entity ID, document ID/version, and expected SHA-256. A new CSIT reanalysis request ID identifies one attempt. The original Outbox remains immutable audit history.
3. **Preconditions:** CSIT rechecks the formal source, archive state, physical file availability, SHA-256, active deletion hold, current analysis revision, original KM Job ID/status, and authorization under a transaction and concurrency guard. KM independently checks that the original job is terminal `Failed` and that the source/version is still eligible.
4. **Idempotency:** CSIT sends a stable idempotency key for a given reanalysis request. KM returns the same attempt/Job identity for repeats and rejects conflicting payloads. A retry must not create duplicate ingestion events or duplicate active indexes for the same source revision.
5. **State and callback:** KM explicitly returns `Accepted` plus an attempt or new Job ID. The attempt has ordered `Accepted → Processing → Completed/Failed` callbacks using the existing authenticated callback channel, carrying the reanalysis request ID and KM Job ID. CSIT stores callback receipts, rejects mismatched or out-of-order callbacks, and does not mutate the old Outbox from `Failed` to success.
6. **Errors and audit:** Both sides distinguish ineligible source/deletion hold, stale SHA or revision, original job not failed, duplicate/conflicting request, transient KM unavailability, and terminal analysis failure. CSIT records actor, reason, source and Job identities, request/response class, timestamps, and callback outcome without tokens, file content, or secrets.

After KM Owner approves and implements this contract, CSIT can add an administration-only API, durable attempt state, UI confirmation and progress, and integration tests against a simulated KM provider. Schema and deployment review would be separate from the current queue cleanup.
