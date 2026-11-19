$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevOidcStructuralSmoke.ps1')

$authorize = 'https://idp.example.test/connect/authorize'
$parameters = @(
    'client_id=csit-test', 'response_type=code',
    'redirect_uri=https%3A%2F%2Fcsit-dev-da40.duckdns.org%3A7443%2Fsignin-oidc',
    'code_challenge_method=S256',
    ('code_challenge=' + ('A' * 43)), 'state=opaque-state', 'nonce=opaque-nonce')
$headers = @(
    'HTTP/1.1 302 Found', ('Location: ' + $authorize + '?' + ($parameters -join '&')),
    'Set-Cookie: .AspNetCore.Correlation.test=opaque; path=/; secure; httponly; samesite=none',
    'Set-Cookie: .AspNetCore.OpenIdConnect.Nonce.test=opaque; path=/; secure; httponly; samesite=none')

function Assert-Rejected([string[]] $InputHeaders, [string] $Case) {
    $rejected = $false
    try { Assert-DevOidcChallenge $InputHeaders 'csit-test' $authorize }
    catch { $rejected = $true }
    if (-not $rejected) { throw "OIDC structural gate did not reject $Case." }
    Write-Output "PASS $Case"
}

Assert-DevOidcChallenge $headers 'csit-test' $authorize
Write-Output 'PASS valid_challenge'
Assert-Rejected ($headers -replace 'client_id=csit-test','client_id=wrong') 'wrong_client'
Assert-Rejected ($headers -replace 'signin-oidc','wrong-callback') 'wrong_redirect'
Assert-Rejected ($headers -replace 'code_challenge_method=S256','code_challenge_method=plain') 'no_pkce_s256'
Assert-Rejected (@($headers | Where-Object { $_ -notmatch 'Correlation' })) 'missing_correlation'
Assert-Rejected ($headers -replace '; secure','') 'insecure_cookies'
Assert-Rejected ($headers -replace 'HTTP/1.1 302 Found','HTTP/1.1 200 OK') 'status_only'
