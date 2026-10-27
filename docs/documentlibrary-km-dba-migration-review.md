# DocumentLibraryFile → KM v1.2 DBA Migration 審核包

## 1. Migration

- 名稱：`20261027061524_AddDocumentLibraryKmIntegrationV1`
- 基準 Migration：`20261026135657_AddVerificationCaseLocalCleanup`
- EF Core 產物：
  - `Migrations/20261027061524_AddDocumentLibraryKmIntegrationV1.cs`
  - `Migrations/20261027061524_AddDocumentLibraryKmIntegrationV1.Designer.cs`
  - `Migrations/AppDbContextModelSnapshot.cs`
- DBA SQL：[`documentlibrary-km-migration.sql`](documentlibrary-km-migration.sql)
- DBA SQL SHA256：`E82A5B962D4CD584D55BF4585D8CCD82F85C72A50A48DD26A4BF56BF1DBD5E8C`
- SQL 是以 `dotnet ef migrations script 20261026135657_AddVerificationCaseLocalCleanup 20261027061524_AddDocumentLibraryKmIntegrationV1` 產生，包含單一 transaction 與 Migration history 寫入。

## 2. 涉及資料表

| 資料表 | 動作 | 用途 |
| --- | --- | --- |
| `document_processing_jobs` | 新增欄位、Check Constraint、Index | KM 狀態、完整 Metadata、NAS 歸檔 Lease／Retry／結果 |
| `document_processing_callbacks` | 新增資料表 | Callback ID 冪等收據與 payload 雜湊稽核 |
| `km_integration_outbox` | 僅擴充既有 Check Constraint | 允許正式事件 `DocumentLibraryFileUploaded`，沿用既有 Outbox |
| `__EFMigrationsHistory` | 新增一筆 | EF Migration 版本記錄 |

沒有刪除、重建或回填既有業務資料，也沒有建立第二套同步工作表。

## 3. 新增欄位與型別

`document_processing_jobs`：

| 欄位 | PostgreSQL 型別 | Null／Default | 說明 |
| --- | --- | --- | --- |
| `km_started_at` | `timestamptz` | NULL | KM 開始時間 |
| `km_completed_at` | `timestamptz` | NULL | KM 完成時間 |
| `km_failed_at` | `timestamptz` | NULL | KM 失敗時間 |
| `km_error_code` | `varchar(100)` | NULL | 契約錯誤碼 |
| `km_error_message` | `varchar(2000)` | NULL | 契約錯誤訊息 |
| `km_error_retryable` | `boolean` | NULL | KM 回報是否可重試 |
| `metadata_json` | `jsonb` | NULL | v1.2 Completed 完整 Metadata |
| `archive_status` | `varchar(30)` | NOT NULL, `NotStarted` | NAS 狀態 |
| `archive_attempt_count` | `integer` | NOT NULL, `0` | NAS 嘗試次數 |
| `archive_next_retry_at` | `timestamptz` | NULL | 下次歸檔時間 |
| `archive_lease_token` | `uuid` | NULL | 歸檔 Lease |
| `archive_lease_expires_at` | `timestamptz` | NULL | Lease 到期時間 |
| `archive_last_attempt_at` | `timestamptz` | NULL | 最後歸檔嘗試 |
| `archive_last_error` | `varchar(2000)` | NULL | 歸檔錯誤 |
| `archive_sha256` | `varchar(64)` | NULL | 正式 NAS 檔案複驗值 |
| `archived_at` | `timestamptz` | NULL | 歸檔完成時間 |

`document_processing_callbacks`：

| 欄位 | PostgreSQL 型別 | Null | 說明 |
| --- | --- | --- | --- |
| `callback_id` | `uuid` | NOT NULL | PK；契約 Callback ID |
| `document_processing_job_id` | `uuid` | NOT NULL | 對應 CSIT Job ID |
| `status` | `varchar(30)` | NOT NULL | Processing／Completed／Failed |
| `payload_sha256` | `varchar(64)` | NOT NULL | 相同 Callback ID 內容一致性 |
| `received_at` | `timestamptz` | NOT NULL | CSIT 收件時間 |

## 4. Constraint、Index 與 Foreign Key

- `ck_document_processing_jobs_status`：既有集合加入 `Accepted`；保留 Pending／Processing／Completed／Failed。
- `ck_document_processing_jobs_archive_status`：只允許 NotStarted／Pending／Archiving／Completed／Failed。
- `ck_document_processing_jobs_archive_result`：Completed 必須同時有 `archived_at` 與 `archive_sha256`。
- `ck_document_processing_jobs_metadata`：新 KM Completed 必須有 Metadata；特別保留舊版 Completed + NotStarted + 無 `km_completed_at` 的相容路徑。
- `ck_document_processing_callbacks_status`：只允許正式三種 Callback 狀態。
- `ck_km_outbox_supported_event`：在既有 TestReport 與附件事件之外加入 `DocumentLibraryFileUploaded`；強制 `source_entity_id = document_id`、內部版本 1、案件欄位皆 NULL。
- Callback PK `callback_id` 提供全域冪等；同 ID 不同 payload 由應用層以 SHA256 拒絕。
- FK `fk_document_callbacks_job` → `document_processing_jobs(id)`，`ON DELETE CASCADE`。
- Index `ix_document_callbacks_job_received(document_processing_job_id, received_at)`。
- Index `ix_document_jobs_archive_claim(archive_status, archive_lease_expires_at)`。
- Index `ix_document_jobs_archive_retry(status, archive_status, archive_next_retry_at)`。
- 既有唯一索引維持不變：Job 對 DocumentFile 一對一、Outbox `event_id` 唯一、`(source_system, document_id, version)` 唯一、非 NULL `km_job_id` 唯一。

## 5. 舊資料與版本相容性

- 新增欄位除兩個有常數預設的歸檔欄位外皆可 NULL；不要求既有資料回填。
- PostgreSQL 11+ 對常數 default 的 ADD COLUMN 採快速預設；本次 PostgreSQL 16.13 實測沒有資料表重寫。
- 現存 `document_processing_jobs.status='Completed'` 且沒有 Metadata 的資料仍合法，條件是它尚未進入新 KM／NAS 流程。
- 舊版程式可忽略新增欄位／資料表；Migration 後仍可在整合功能關閉時運作。
- TestReport 的 DTO、API、事件與約束分支未變；附件事件僅保留原有行為。
- 文件專區對外 v1 契約沒有加入 `documentVersion`；Outbox 的 `version=1` 僅為既有資料模型內部唯一鍵。

## 6. SQL 與預期鎖定風險

完整 SQL 見 [`docs/documentlibrary-km-migration.sql`](documentlibrary-km-migration.sql)。SQL 只包含 ALTER／CREATE／INDEX／Migration history INSERT，沒有 DELETE、DROP TABLE 或業務資料 UPDATE。

風險：

- `ALTER TABLE ... ADD/DROP CONSTRAINT` 與 `ADD COLUMN` 需要短暫 `ACCESS EXCLUSIVE` lock。
- 三個一般 `CREATE INDEX` 在 transaction 內執行，期間會消耗 I/O 並持有阻擋 schema 變更的 lock；不是 `CONCURRENTLY`。
- `ck_km_outbox_supported_event` 與新 Job Check 會掃描既有列驗證；資料量越大，lock 時間越長。
- Callback 表為新空表，PK／FK／Index 風險低。
- 建議 DBA 先查表大小、長交易、鎖等待，並在低流量維護窗執行；設定合適的 `lock_timeout`／`statement_timeout`，不要在逾時後繞過約束。

## 7. DEV Migration 前備份要求

人工核准前先完成並驗證：

1. DEV PostgreSQL 可還原的 snapshot 或一致性備份。
2. 至少保留 `document_processing_jobs`、`km_integration_outbox`、`document_files`、`__EFMigrationsHistory` 的 schema + data 匯出。
3. 記錄 Migration 前 row counts、目前 Migration ID、DB Runtime 與備份位置／校驗值。
4. 確認 `KmIntegration.Enabled=false`、`KmDeletion.Enabled=false`，且沒有背景派送。

## 8. 回復與失敗處理

- Up 在單一 transaction 中；SQL 或約束驗證失敗時應 rollback 全部 DDL 與 history INSERT。DBA 應先保存完整錯誤與 lock 診斷，再 forward-fix。
- 在尚未產生任何 DocumentLibrary KM 新資料前，可由 DBA 評估 EF Down；Down 會刪除 Callback 表與所有本 Migration 新增欄位，因此仍須先備份。
- 一旦存在 `DocumentLibraryFileUploaded`、Callback、Metadata 或歸檔結果，Down **不可視為無損回復**：舊 Outbox Check 不接受新事件，而且欄位／Callback 資料會遺失。此時只能維持功能旗標關閉後 forward-fix，或還原 Migration 前整庫備份。
- 不允許用刪除新事件、關閉 constraint 或直接改 DEV 資料的方式規避 DBA Gate。

## 9. 隔離 PostgreSQL 實測結果

- Instance：PostgreSQL 16.13，僅綁定 `127.0.0.1:55432`。
- 最終專屬 DB：`csit_documentlibrary_km_e2e_20261027_final3`；從既有隔離 schema/history 基線還原後，由 EF 依序 Up 至本 Migration。
- Migration Up、Model 約束、舊 Completed 無 Metadata 相容性：PASS。
- 獨立 `csit_documentlibrary_km_e2e_20261027_sqlreview` DB 先升到前一版，再以 `psql -v ON_ERROR_STOP=1 -f docs/documentlibrary-km-migration.sql` 直接套用：transaction COMMIT，PASS。
- Mock KM 202、KM Job ID、M2M 下載、401、Processing／Completed／Failed、完整 JSONB Metadata、Callback 冪等／衝突（含兩個並行相同 Callback）：PASS。
- NAS 複製、size/SHA256 複驗、原子 move、DB commit 後清理、已存在正確 NAS target 的中斷恢復：PASS。
- 503／timeout／Retry、Lease 到期、兩 Worker `SKIP LOCKED`、重啟恢復、多文件防重：PASS。
- 最終稽核計數：5 個 DocumentLibrary Outbox、5 個 Callback 收據；2 個新文件 KM+NAS Completed；1 個 KM Failed；2 個 KM Accepted；1 筆 legacy Completed/NotStarted 保留。
- 一個最初純空 DB 暴露既有歷史 Migration 依賴舊 schema 基線；未修改或刪除該 DB。後續皆使用新建、還原基線的專屬 DB，沒有重用或覆寫前次資料。
- 所有隔離 DB 與 data directory 保留供稽核；測試結束後 Instance 正常停止。

## 10. DBA 核准後的 DEV 順序與人工核准點

1. **人工核准點（本次停止點）**：DBA 審查本文件、SQL、備份／維護窗／rollback 限制，明確核准執行 DEV Migration。
2. 維持兩個功能旗標為 false，先備份並在 DEV 由 DBA 執行／監督 Migration。
3. 驗證 history、欄位、constraint、index、row counts 與既有 TestReport smoke。
4. 合併／推送程式至指定部署分支，再依正式 `Release-DevReady.ps1` 與 DEV Ready Watcher 流程部署；不得用舊 run-site 腳本。
5. 驗證 Runtime SHA、PID、DLL SHA256、三支 API 在未授權時的安全行為，且兩旗標仍為 false。
6. 取得真正 M2M 憑證、NAS 權限及 Anderson KM 測試窗後，另行人工核准才可開啟 `KmIntegration.Enabled`；`KmDeletion.Enabled` 本階段保持 false。

本開發分支不執行 DEV Migration、不 Push 部署分支、不啟動 DEV Ready Watcher。
