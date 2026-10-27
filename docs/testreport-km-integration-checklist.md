# TestReport KM integration acceptance checklist

This checklist covers the CSIT side of the future Anderson KM connection. Passing the isolated Mock KM tests does not constitute acceptance of the production KM service.

1. Confirm the production HTTPS API base URL and certificate trust chain.
2. Provision CSIT M2M client credentials outside the repository. Confirm token endpoint, client ID, secret file ACL, audience `km.integration`, and scopes for ingestion and document reads.
3. Contract test `POST /api/integrations/csit/v1/ingestion-jobs`, including `EventId`, `DocumentId`, `Version`, project/case/task references, `Idempotency-Key`, and the correlated HTTP 202 response containing `JobId`.
4. Confirm Anderson KM can call the authorized CSIT document content API for the exact committed `DocumentId` and `Version`, and cannot read pending, rejected, or unrelated reports.
5. Register the CSIT callback URL `POST /api/integrations/km/v1/ingestion-status`; validate issuer, audience, client identity, and the existing callback write scope.
6. Verify that every callback correlates `JobId`, `SourceSystem`, `DocumentId`, and `Version`, and that duplicates and out-of-order transitions are handled safely.
7. Exercise timeout, HTTP 429/5xx, token failure, retry schedule, lease expiry, concurrent workers, and service restart recovery without duplicate ingestion.
8. Run successful Processing → Completed and Failed end-to-end acceptance cases and confirm the persisted PostgreSQL outbox state and CSIT UI.
9. Before setting `KmIntegration.Enabled=true`, require approved credentials, reachable endpoints, callback authorization, document-read authorization, production contract results, monitoring ownership, rollback procedure, and a reviewed initial release window.

Keep `KmIntegration.Enabled=false` and `KmDeletion.Enabled=false` until all production acceptance items are signed off.
