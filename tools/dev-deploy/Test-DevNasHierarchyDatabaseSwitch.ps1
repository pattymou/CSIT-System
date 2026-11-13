$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Reconcile-DevNasBusinessHierarchy.ps1') `
    -TechnicalRoot '\\AI-NAS\Workspace_B\CSIT_TECH_DATA_DEV' -LoadFunctionsOnly

$passed = 0
$root = Join-Path ([IO.Path]::GetTempPath()) `
    ('csit-nas-db-switch-' + [guid]::NewGuid().ToString('N'))

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $documentId = '11111111-1111-1111-1111-111111111111'
    $vendorId = '22222222-2222-2222-2222-222222222222'
    $equipmentId = '33333333-3333-3333-3333-333333333333'
    $caseId = '44444444-4444-4444-4444-444444444444'
    $manifest = @(
        [pscustomobject]@{ Kind='Document'; Key=$documentId; DbValue='舊文件\甲'; NewDbValue='技術文件\乙' },
        [pscustomobject]@{ Kind='Vendor'; Key=$vendorId; DbValue='舊廠商\甲'; NewDbValue='廠商資訊\乙' },
        [pscustomobject]@{ Kind='Equipment'; Key=$equipmentId; DbValue='舊設備\甲';
            NewDbValue='設備資訊\乙'; ParentKey='EQ-1'; StorageFolder='設備資訊';
            SourcePath='C:\legacy\equipment-original\file.pdf' },
        [pscustomobject]@{ Kind='CaseFile'; Key=$caseId; DbValue='舊案件\甲'; NewDbValue='產品驗證資料\乙' }
    )

    $forward = New-DatabaseSwitchSql $manifest
    foreach ($expected in @(
            "Kind=Document Table=document_files RecordId=$documentId Field=relative_path",
            "Kind=Vendor Table=vendor_files RecordId=$vendorId Field=folder_path",
            "Kind=Equipment Table=apparatus_files RecordId=$equipmentId Field=FilePath",
            "Kind=CaseFile Table=module_case_files RecordId=$caseId Field=file_path")) {
        Assert-True ($forward.Contains($expected)) "Missing diagnostic identity: $expected"
    }
    Assert-True ($forward.Contains("relative_path='技術文件\乙'")) `
        'Unicode forward path was not preserved.'
    $passed++; Write-Output 'PASS four_kind_forward_sql_and_diagnostics'

    Assert-True ($OutputEncoding.WebName -eq 'utf-8') 'Native command stdin is not UTF-8.'
    $reader = Join-Path $root 'read-stdin.ps1'
    @'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
$value = [Console]::In.ReadToEnd()
$sha = [Security.Cryptography.SHA256]::Create()
try {
    ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value)))).Replace('-','')
} finally { $sha.Dispose() }
'@ | Set-Content -LiteralPath $reader -Encoding UTF8
    $sample = '產品驗證資料\申請單文件'
    $actualHash = (($sample | & powershell.exe -NoProfile -File $reader) -join '').Trim()
    $expectedHashes = foreach ($suffix in @("`r`n", "`n")) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            ([BitConverter]::ToString($sha.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($sample + $suffix)))).Replace('-','')
        } finally { $sha.Dispose() }
    }
    Assert-True ($actualHash -in $expectedHashes) 'Unicode was changed by the native stdin pipeline.'
    $passed++; Write-Output 'PASS windows_powershell_native_stdin_utf8'

    foreach ($item in $manifest) {
        Assert-True ($forward.Contains("AND") -and $forward.Contains((Quote-Sql $item.DbValue))) `
            "Old-value compare is absent: $($item.Kind)"
    }
    $passed++; Write-Output 'PASS old_value_mismatch_remains_fail_closed'

    Assert-True ($forward.StartsWith('BEGIN;')) 'Transaction BEGIN is absent.'
    Assert-True ($forward.TrimEnd().EndsWith('COMMIT;')) 'Transaction COMMIT is absent.'
    Assert-True ($forward.Contains('IF NOT EXISTS') -and $forward.Contains('RAISE EXCEPTION')) `
        'New-value verification does not abort the transaction.'
    Assert-True (-not $forward.Contains('ON CONFLICT') -and -not $forward.Contains('DO NOTHING')) `
        'DB switch contains a skip path.'
    $passed++; Write-Output 'PASS verification_failure_aborts_transaction'

    $rollback = New-DatabaseSwitchSql $manifest -Rollback
    foreach ($item in $manifest) {
        Assert-True ($rollback.Contains("SET") -and
            $rollback.Contains((Quote-Sql $item.DbValue)) -and
            $rollback.Contains((Quote-Sql $item.NewDbValue))) `
            "Rollback values are incomplete: $($item.Kind)"
    }
    $passed++; Write-Output 'PASS four_kind_rollback_sql_reverses_paths'

    $source = Join-Path $root 'source.bin'
    $target = Join-Path $root 'target.bin'
    [IO.File]::WriteAllBytes($source, [byte[]](0,1,2,3,255))
    [IO.File]::WriteAllBytes($target, [byte[]](9,8,7,6,128))
    $sourceBefore = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $targetBefore = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    $null = New-DatabaseSwitchSql $manifest
    Assert-True ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -eq $sourceBefore -and
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $targetBefore) `
        'SQL generation changed a source or target fixture.'
    $passed++; Write-Output 'PASS source_and_target_files_unchanged'

    if ($passed -ne 6) { throw "Unexpected test count: $passed" }
    Write-Output "DEV_NAS_DB_SWITCH_TEST_PASS count=$passed"
} finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
