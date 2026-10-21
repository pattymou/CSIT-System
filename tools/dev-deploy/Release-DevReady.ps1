param()

$ErrorActionPreference = 'Stop'
$branch = 'feature/km-stage-integration'
$remote = 'https://github.com/pattymou/CSIT-System.git'
$root = 'C:\ProgramData\CSIT-DEV'
$workRoot = Join-Path $root 'work'

if ((git branch --show-current).Trim() -ne $branch) { throw 'Only the designated DEV branch can be released.' }
if (git status --porcelain) { throw 'The working tree must be clean before the DEV ready gate.' }
$sha = (git rev-parse HEAD).Trim()
if ($sha -notmatch '^[0-9a-f]{40}$') { throw 'Invalid HEAD.' }
$remoteHead = ((git ls-remote origin "refs/heads/$branch") -split '\s+')[0]
if ($LASTEXITCODE -ne 0 -or $remoteHead -ne $sha) { throw 'Push the tested commit to the DEV branch first.' }

$work = Join-Path $workRoot ("ready-gate-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
try {
    & git clone --quiet --branch $branch --single-branch --depth 1 $remote $work
    if ($LASTEXITCODE -ne 0 -or (git -C $work rev-parse HEAD).Trim() -ne $sha) {
        throw 'The isolated DEV ready checkout did not match the pushed commit.'
    }
    $tests = Join-Path $work 'tests\ApprovalFramework.Tests\ApprovalFramework.Tests.csproj'
    & dotnet restore $tests --nologo
    if ($LASTEXITCODE -ne 0) { throw 'DEV ready restore failed; no tag was pushed.' }
    & dotnet build $tests -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'DEV ready build failed; no tag was pushed.' }
    & dotnet test $tests -c Release --no-build --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'DEV ready tests failed; no tag was pushed.' }
    $tag = 'dev-ready-{0}-{1}' -f (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'), $sha.Substring(0, 8)
    & git tag $tag $sha
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the DEV ready tag.' }
    & git push origin "refs/tags/$tag"
    if ($LASTEXITCODE -ne 0) { throw 'Could not push the DEV ready tag.' }
    Write-Output "DEV_READY_TAG_PUSHED $tag $sha"
} finally {
    $resolvedRoot = [IO.Path]::GetFullPath($workRoot).TrimEnd('\') + '\'
    if (Test-Path -LiteralPath $work) {
        if (-not [IO.Path]::GetFullPath($work).StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a checkout outside the DEV work root.'
        }
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
