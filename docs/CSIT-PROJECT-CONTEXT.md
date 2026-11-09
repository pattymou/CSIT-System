# CSIT 專案長期交接文件

本文件是 CSIT 專案的長期交接入口，供新的 Codex 帳號、AI Agent 與維護者在開始工作前建立一致背景。內容以目前 repository、既有技術文件、GitHub 證據與 Owner 已定案的架構決議為準。

文件建立日期：2026-11-09（Asia/Taipei）
目前交接基準 branch：`feature/km-stage-integration`
目前交接基準 commit：`e610ed7d564e0490003c417bef1bd146e1c4edfa`

## 新 Agent 開始工作的閱讀順序

1. 先讀根目錄 `AGENTS.md`，遵守 DEV Ready、Migration、Production 與資料安全規範。
2. 再讀本文件，確認 Owner 決議、責任分界、已驗證證據與待確認項目。
3. 依任務範圍閱讀：
   - KM Mock 驗收：[`docs/km-mock-e2e.md`](km-mock-e2e.md)
   - 隔離負載測試：[`docs/csit-multi-user-load-test.md`](csit-multi-user-load-test.md)
   - DocumentLibrary KM v1.2 DBA review：[`docs/documentlibrary-km-dba-migration-review.md`](documentlibrary-km-dba-migration-review.md)
   - KM analysis v1.4：[`docs/km-analysis-v14-dba-migration-review.md`](km-analysis-v14-dba-migration-review.md)
   - KM v1.4.1 Task B/C/D：[`docs/km-v14-task-b-dba-migration-review.md`](km-v14-task-b-dba-migration-review.md)、[`docs/km-v141-task-c-dba-migration-review.md`](km-v141-task-c-dba-migration-review.md)、[`docs/km-v141-task-d-dba-migration-review.md`](km-v141-task-d-dba-migration-review.md)
   - Test Report 真實 KM 驗收清單：[`docs/testreport-km-integration-checklist.md`](testreport-km-integration-checklist.md)
4. 再看與任務直接相關的 Controller、Service、Entity、`Data/AppDbContext.cs` 與 migration review；不要只依賴文件摘要推論行為。
5. 查 GitHub Issue、PR、commit、Actions artifact 與 DEV state/log；沒有證據的項目一律標示「待確認」。

## 1. Owner 已定案的核心架構

以下是 Owner 與 ChatGPT 的長期規劃決議，不是本文件作者可自行重訂的設計建議：

- CSIT 不重寫，維持 .NET、Blazor、EF Core 與 PostgreSQL。
- 正式資料結構維持 `Module → ModuleRecord → Case → Task → File`。目前實體對應包括 `ModuleEntity`、`ModuleRecord`、`ModuleRecordCase`、`ModuleRecordTask` 與 `ModuleCaseFile`；驗證申請另有 `VerificationApplication`，核准後建立對應 `ModuleRecord`。
- CSIT 擁有驗證申請、Test Report、設備、預約、正式文件、NAS、審核狀態與對外 API Contract。
- Shared Identity 是正式認證與權限整合的基礎。隔離負載測試使用明確標記的 Legacy/loopback 模式，不代表正式 Shared Identity 驗收。
- DEV 網站與 DEV PostgreSQL 位於同一台實體主機；CSIT Owner 本人負責系統、資料庫、架構與部署決策，不預設另有 IT 團隊。

### CSIT 與 Anderson KM 的責任分界

CSIT 負責業務流程、正式資料、API Contract、Outbox、授權文件下載、Callback 接收、部署與整合驗收。Anderson KM 負責 KM 端 API、Parser、Chunk、Embedding、RAG、資料庫、背景工作與 KM Callback。

兩邊只透過明確 API Contract 交換資料。KM 不得直接讀寫 CSIT DB，也不得直接存取 CSIT Staging 或 NAS；KM 不得自行重定義 CSIT 資料模型、權限或正式流程。KM 規劃方向是 Neo4j＋Qdrant＋TimescaleDB，搭配 RAG／GraphRAG／Agentic RAG，但規劃不等於已完成的實作或真實整合。

OpenVitamin、Test Station、Controller 是另一個自動化領域，不得混入既有 CSIT－KM 資料契約。

## 2. CSIT 業務與資料流

### 核心業務資料

- `ModuleEntity` 是模組目錄；`ModuleRecord` 是專案／申請單層級。
- `ModuleRecordCase`、`ModuleRecordTask` 分別承載 Case 與 Task 狀態、負責人、日期、結果與測試項目參照。
- `ModuleCaseFile` 保存 Case/Task 附件與 Test Report；`FileKind=TestReport`、審核狀態、`IsFinal`、`DocumentId`、版本、SHA256 與 NAS/raw-data 欄位共同描述正式文件。
- `VerificationApplication` 先處於 Draft/Returned，送出後進入 Submitted；主管或授權 Admin/Team Leader 核准後轉 Accepted 並建立 ModuleRecord。`VerificationApplicationFile` 在正式歸檔前屬申請附件，不能直接視為正式 KM 文件。
- `DocumentLibraryFile` 是正式文件庫物理版本，具 `DocumentId`、`Version`、`SupersedesFileId`、SHA256、`StorageArea=Library` 與相對路徑。`DocumentProcessingJob` 管理 KM 分析與 NAS archive 生命週期。

`AppDbContext` 負責上述 Entity 與關聯映射；主要 HTTP 邊界包含 Modules、Module Records、Cases、Tasks、Case Files、Document Library、Verification Application Files，以及 KM integration／callback controllers。Controller 負責授權與契約邊界，Service 負責狀態轉移、檔案生命週期與 Outbox；兩者都不能被交接文件解讀成 Anderson KM 的直接資料庫介面。

### 文件審核、NAS 與 Outbox

1. 一般員工上傳附件先進入 approval/staging 流程；`ApprovalSubmissionService`、各類 approval upload service 與 `ApprovalCommitWorker` 負責保存提交、主管審核與正式 commit。
2. 一般員工附件必須經主管核准才正式入庫；Admin 與有效啟用中的 Team Leader 上傳免審，但仍須符合正式檔案規則。
3. 草稿、退件、未核准或未正式歸檔的檔案不得供 KM 下載或索引。草稿保留 15 天；DEV cleanup 必須遵守 dry-run、marker 與安全限制。
4. 正式 NAS 歸檔由 `DocumentLibraryArchiveWorker`／相關 archive service 執行，驗證檔案長度與 SHA256 後才將 archive 狀態與正式路徑寫入 DB。
5. 正式入庫與 KM 分析狀態分離：不必等待 KM `Completed` 才能完成 CSIT 正式歸檔。
6. 正式 commit 建立 `KmIntegrationOutbox`。主要 factory 包括 `ForCommittedTestReport`、`ForDocumentLibraryFile`、`ForArchivedVerificationAttachment`、`ForArchivedVendorFile` 與 `ForArchivedEquipmentDocument`。Outbox 保存 `EventId`、來源、`DocumentId`/版本、檔案摘要、狀態、lease、retry、錯誤與 KM Job ID。

文件專區整合既有正式附件來源，不複製檔案，也不平行建立第二套 KM 排程。

### KM ingestion、callback 與 deletion

CSIT v1/v2 Contract 位於 `Services/KmIntegration/*Contracts.cs`，Controller 目前包含：

- v1 授權文件下載：`GET /api/integrations/km/v1/documents/{documentId}/versions/{version}/content`、Document Library、Vendor 與 Equipment 文件內容端點。
- v1 ingestion 狀態：`POST /api/integrations/km/v1/ingestion-status`，以及 Document Library/Equipment 的相容狀態端點。
- v2 文件狀態與分析結果：`POST /api/integrations/km/v2/document-jobs/status`、`/analysis-results`。
- v2 metadata change callback：`POST /api/integrations/km/v2/document-metadata-changes/status`，要求與 Callback ID 相符的 `Idempotency-Key`。
- v1 deletion callback：`POST /api/integrations/km/v1/document-deletion-status`；v2 deletion callback：`POST /api/integrations/km/v2/document-deletions/status`。

CSIT 正式入庫後由 Outbox/worker 送出 ingestion request。KM 持久化接收後回 HTTP 202 與 KM Job ID；202 只代表已接受，不代表 Completed。Callback 必須驗證來源、Job、文件/版本、SHA256、狀態順序與 payload，並以 receipt／唯一鍵提供冪等性。Processing、Completed、Failed、retry、lease、restart、重送與亂序 callback 都必須可控。

刪除流程先建立 durable deletion job 與 anti-revival hold，再依正式 Deletion Contract 等待 KM 接受／驗證，最後清理本地 DB/NAS/staging。不得留下可重新索引的工作或孤立檔案；完成狀態要有完整清理證據。

### 既定業務規則

- Test Report v1、DocumentLibrary 版本化 API 與既有 DTO/Outbox 契約不可破壞。
- 文件正式歸檔與 KM analysis status 分離。
- HTTP 202、Processing、Completed、Failed 是不同狀態，不能將 202 當成完成。
- Callback 不能倒退狀態；相同 callback ID 與不同 payload 必須拒絕。
- Deletion 必須保留必要的 job/receipt/tombstone 證據，避免來源刪除後工作消失而造成 KM 殘留或復活。
- KM Global、Vendor、Equipment 與 deletion flags 在未完成正式驗收前保持 disabled。

## 3. DEV 與 Production 安全

DEV 入口：[`https://csit-identity-dev.duckdns.org:7443/account/login`](https://csit-identity-dev.duckdns.org:7443/account/login)。

- Production 5010 禁止擅自修改、部署或重啟。
- Runtime 修改必須遵守根目錄 `AGENTS.md` 的 DEV Ready／Watcher 流程；不能使用 `run-site.ps1`、`run-site1.ps1` 或 Folder Publish profile 代替 DEV 流程。
- Migration 必須先有獨立 DBA Gate、備份、Preflight、核准、exact-SHA marker 與驗證；Owner 同時管理資料庫也不能跳過安全檢查。
- Migration change 會停止一般自動部署，等待 DBA review。
- 隔離測試、Mock E2E 與 GitHub Actions 不等於 DEV 部署；不可把 test DB、test NAS 或 mock server 證據寫成正式 DEV 證據。

## 4. 已完成且有證據的工作

### KM A-D Mock E2E

`CSIT KM A-D Mock E2E` 使用隔離 PostgreSQL、in-process Mock KM ingestion/deletion server 與既有 ApprovalFramework tests。它不呼叫 LLM、不連 Anderson KM、不使用 Shared Identity、正式 NAS、DEV 或 Production。既有紀錄為 648/648 PASS；PR [#7](https://github.com/pattymou/CSIT-System/pull/7) 對應 fail-closed Gate 與 workflow bootstrap。

### 跨平台回歸與隔離負載

PR [#10](https://github.com/pattymou/CSIT-System/pull/10) 合併了 workflow-only bootstrap。Run [42574710863](https://github.com/pattymou/CSIT-System/actions/runs/42574710863) 的證據為：

- Windows 完整 regression：638/638 PASS。
- Linux regression：636/636 PASS；僅排除兩個 Windows-only DEV deployment tests。
- Linux isolated k6：5、20、50 users 均 PASS。
- Fail-closed load Gate PASS。
- 50-user 負載時 PostgreSQL 連線峰值 86/100，未發生連線耗盡。
- Run artifacts： [load-test artifact](https://github.com/pattymou/CSIT-System/actions/runs/42574710863/artifacts/13655095781)、[Windows regression artifact](https://github.com/pattymou/CSIT-System/actions/runs/42574710863/artifacts/13654289429)。

這些都是隔離 PostgreSQL 16、loopback host、synthetic identity、temporary storage 與 test database 的結果。它們不是實體 DEV 容量認證，也不是 Anderson KM 真實整合完成的證據。

PR [#9](https://github.com/pattymou/CSIT-System/pull/9) 是較早的負載 workflow bootstrap；目前以 PR #10 與上述 Run 為較新的 workflow 證據。

## 5. 待確認與等待 Anderson 的工作

下列項目不能直接寫成已完成或未完成；目前均標示為「待確認」：

- DocumentLibraryFile × KM v1.2 的 DEV DBA Gate、Migration、exact-SHA 核准、部署與 smoke 證據。
- KM v1.4／v1.4.1 Task B/C/D migration 是否實際進入 DEV，以及 DEV migration history、schema、backup、marker 與 post-deploy 驗證。
- GitHub Issue [#6](https://github.com/pattymou/CSIT-System/issues/6) 是否已過時；目前 Issue 仍為 open，內容記錄 DocumentLibrary KM DBA Gate／DEV 部署尚未完成。
- CSIT 目前 DEV Runtime SHA、部署版本與尚未交付的功能。
- Anderson KM 真正 E2E、M2M authentication、文件解析、metadata、Callback、retry/lease/restart 與 deletion acceptance。
- 正式 KM ingestion/deletion 派送及各 Global/Vendor/Equipment/deletion flag 的啟用條件與核准紀錄。
- 任何真實 NAS、Shared Identity、production credential、M2M secret 或正式 DEV 資料證據。

目前 feature branch 的 source evidence 是 commit `e610ed7d564e0490003c417bef1bd146e1c4edfa`；這不等於 DEV Runtime SHA，後者待從 DEV state 與部署 log 核實。

## 6. 技術與 GitHub 證據索引

- [AGENTS.md](../AGENTS.md)：DEV Ready、Watcher、Migration、Production 安全規範。
- [KM Mock E2E 文件](km-mock-e2e.md)
- [隔離負載測試文件](csit-multi-user-load-test.md)
- [DocumentLibrary KM v1.2 DBA review](documentlibrary-km-dba-migration-review.md)
- [KM analysis v1.4 DBA review](km-analysis-v14-dba-migration-review.md)
- [KM v1.4.1 Task C DBA review](km-v141-task-c-dba-migration-review.md)
- [KM v1.4.1 Task D DBA review](km-v141-task-d-dba-migration-review.md)
- [TestReport KM integration checklist](testreport-km-integration-checklist.md)
- [Issue #6](https://github.com/pattymou/CSIT-System/issues/6)
- [PR #7：Mock E2E Gate](https://github.com/pattymou/CSIT-System/pull/7)
- [PR #9：load-test workflow bootstrap](https://github.com/pattymou/CSIT-System/pull/9)
- [PR #10：cross-platform regression/load workflow](https://github.com/pattymou/CSIT-System/pull/10)
- [Run #42574710863](https://github.com/pattymou/CSIT-System/actions/runs/42574710863)
- [Feature source commit e610ed7](https://github.com/pattymou/CSIT-System/commit/e610ed7d564e0490003c417bef1bd146e1c4edfa)
- [Cross-platform fix commit 5187cb4](https://github.com/pattymou/CSIT-System/commit/5187cb4)
- [DEV Ready baseline commit 8fe25c6](https://github.com/pattymou/CSIT-System/commit/8fe25c608d4b78aae170cb7380dab0ce8a603f4c)

## 7. 交接原則

先確認證據，再判斷狀態；將 Owner 決議、程式實作、Mock 測試、隔離 GitHub Actions、DEV deployment 與 Anderson KM 真實驗收分開記錄。任何新工作若涉及 Runtime、DB、Migration、Workflow、KM flags、DEV 或 Production，先回到 `AGENTS.md` 的安全流程，不可因文件交接而放寬既有 Gate。
