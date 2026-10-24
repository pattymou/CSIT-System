# Verification attachment KM contract proposal

Status: CSIT-side preparation only. The existing `csit-km-integration-v1` contract remains unchanged and does not accept this event.

## Proposed event

- `eventType`: `VerificationApplicationAttachmentArchived`
- `sourceSystem`: `CSIT`
- `sourceEntityType`: `VerificationApplicationFile`
- `sourceEntityId`: verification application attachment ID
- `documentId`: the completed `DocumentLibraryFile.Id`
- `version`: `1` (the archived attachment is immutable)
- `eventId`: durable CSIT outbox event ID and idempotency key
- `document`: original file name, content type, and byte size
- `committedAt`: time the formal file and database link were committed
- `applicationId` and `applicationNo`: parent verification application correlation

Unlike a Test Report, a verification application attachment has no Project / Case / Task identity. Those fields must not be populated with unrelated IDs.

## KM decisions required before outbound delivery is enabled

1. Confirm whether this is a new v1-compatible event variant or a versioned ingestion endpoint.
2. Confirm the exact request schema, including whether `sourceEntityType`, `sourceEntityId`, `applicationId`, and `applicationNo` are required.
3. Confirm that Project / Case / Task are absent or nullable for this document type.
4. Confirm that `eventId` is the idempotency key and how duplicate submissions are acknowledged.
5. Confirm the accepted response and callback correlation fields. CSIT requires `jobId + sourceSystem + documentId + version` to match before changing state.
6. Confirm use of the existing authenticated content endpoint and scopes for the formal document.
7. Confirm retryable HTTP/error codes and terminal failure semantics.

Until these points are agreed and contract-tested, CSIT persists the event as `Pending` but the worker only dispatches the existing `TestReportCommitted` event. `KmIntegration.Enabled` is not changed by this work.
