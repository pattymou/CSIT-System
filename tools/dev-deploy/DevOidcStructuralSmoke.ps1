function Get-DevOidcQuery([Uri] $Uri) {
    $result = @{}
    foreach ($part in $Uri.Query.TrimStart('?').Split('&')) {
        if (-not $part) { continue }
        $pair = $part.Split('=', 2)
        $name = [Net.WebUtility]::UrlDecode($pair[0])
        if ($result.ContainsKey($name)) { throw 'OIDC challenge has duplicate parameters.' }
        $result[$name] = if ($pair.Count -eq 2) { [Net.WebUtility]::UrlDecode($pair[1]) } else { '' }
    }
    return $result
}

function Assert-DevOidcChallenge([string[]] $Headers, [string] $ClientId,
    [string] $AuthorizationEndpoint) {
    $status = @($Headers | Where-Object { $_ -match '^HTTP/' })
    $locations = @($Headers | Where-Object { $_ -match '^Location:' })
    $cookies = @($Headers | Where-Object { $_ -match '^Set-Cookie:' })
    if ($status.Count -ne 1 -or $status[0] -notmatch '^HTTP/\S+ 302\b' -or
        $locations.Count -ne 1 -or $cookies.Count -lt 2) {
        throw 'OIDC challenge headers are incomplete.'
    }
    $location = $locations[0].Substring('Location:'.Length).Trim()
    $uri = $null
    if (-not [Uri]::TryCreate($location, [UriKind]::Absolute, [ref] $uri) -or
        $uri.Scheme -ne 'https' -or $uri.GetLeftPart([UriPartial]::Path) -cne
            $AuthorizationEndpoint.TrimEnd('/')) {
        throw 'OIDC authorization endpoint does not match discovery.'
    }
    $query = Get-DevOidcQuery $uri
    if ($query['client_id'] -cne $ClientId -or $query['response_type'] -cne 'code' -or
        $query['redirect_uri'] -cne 'https://csit-dev-da40.duckdns.org:7443/signin-oidc' -or
        $query['code_challenge_method'] -cne 'S256' -or
        $query['code_challenge'] -notmatch '^[A-Za-z0-9_-]{43}$' -or
        [string]::IsNullOrWhiteSpace($query['state']) -or
        [string]::IsNullOrWhiteSpace($query['nonce'])) {
        throw 'OIDC authorization request structure is invalid.'
    }
    $correlation = @($cookies | Where-Object { $_ -match '^Set-Cookie:\s*\.AspNetCore\.Correlation\.' })
    $nonce = @($cookies | Where-Object { $_ -match '^Set-Cookie:\s*\.AspNetCore\.OpenIdConnect\.Nonce\.' })
    if ($correlation.Count -ne 1 -or $nonce.Count -ne 1) {
        throw 'OIDC correlation or nonce cookie is absent.'
    }
    foreach ($cookie in @($correlation[0], $nonce[0])) {
        if ($cookie -notmatch '(?i);\s*secure(?:;|$)' -or
            $cookie -notmatch '(?i);\s*httponly(?:;|$)' -or
            $cookie -notmatch '(?i);\s*samesite=none(?:;|$)') {
            throw 'OIDC correlation or nonce cookie attributes are invalid.'
        }
    }
}

function Invoke-DevOidcCurl([string] $Url, [string] $Resolve, [switch] $Headers) {
    $arguments = @('-sS','--noproxy','*','--max-time','12','--resolve',$Resolve)
    if ($Headers) { $arguments += @('-D','-','-o','NUL') }
    $arguments += @('-w',"`nCSIT_CURL_STATUS:%{http_code}/%{ssl_verify_result}",$Url)
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $output = @(& $script:curlExe @arguments 2>$null)
        if ($LASTEXITCODE -eq 0 -and $output.Count -ge 1 -and
            $output[-1] -match '^CSIT_CURL_STATUS:(200|302)/0$') {
            return @($output[0..($output.Count - 2)])
        }
        if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
    }
    throw 'OIDC public HTTPS request failed after three attempts.'
}

function Assert-DevOidcStructuralSmoke([string] $Authority, [string] $ClientId,
    [string] $ExpectedSha) {
    if ($Authority.TrimEnd('/') -cne 'https://csit-identity-dev.duckdns.org:7443' -or
        $ClientId -cne 'csit-dev') {
        throw 'DEV OIDC issuer or client differs from the approved baseline.'
    }
    $issuer = $null
    if (-not [Uri]::TryCreate($Authority, [UriKind]::Absolute, [ref] $issuer) -or
        $issuer.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($ClientId)) {
        throw 'DEV OIDC runtime settings are incomplete.'
    }
    $identityResolve = $issuer.Host + ':' + $issuer.Port + ':127.0.0.1'
    $discoveryUrl = $issuer.GetLeftPart([UriPartial]::Authority) +
        '/.well-known/openid-configuration'
    $discovery = (Invoke-DevOidcCurl $discoveryUrl $identityResolve) -join "`n" |
        ConvertFrom-Json
    if (([string]$discovery.issuer).TrimEnd('/') -cne $Authority.TrimEnd('/') -or
        [string]$discovery.authorization_endpoint -notmatch '^https://' -or
        [string]$discovery.token_endpoint -notmatch '^https://' -or
        [string]$discovery.jwks_uri -notmatch '^https://') {
        throw 'OIDC discovery metadata is invalid.'
    }
    foreach ($endpoint in @([string]$discovery.authorization_endpoint,
            [string]$discovery.token_endpoint, [string]$discovery.jwks_uri)) {
        $endpointUri = [Uri]$endpoint
        if ($endpointUri.Host -cne $issuer.Host -or $endpointUri.Port -ne $issuer.Port) {
            throw 'OIDC discovery endpoint differs from the configured issuer.'
        }
    }
    $jwksUri = [Uri]$discovery.jwks_uri
    if ($jwksUri.Host -cne $issuer.Host -or $jwksUri.Port -ne $issuer.Port) {
        throw 'OIDC JWKS host differs from the configured issuer.'
    }
    $jwks = (Invoke-DevOidcCurl ([string]$jwksUri) $identityResolve) -join "`n" |
        ConvertFrom-Json
    if (@($jwks.keys | Where-Object { $_.use -eq 'sig' -or -not $_.use }).Count -lt 1) {
        throw 'OIDC JWKS has no signing key.'
    }
    $publicResolve = 'csit-dev-da40.duckdns.org:7443:127.0.0.1'
    $headers = @(Invoke-DevOidcCurl 'https://csit-dev-da40.duckdns.org:7443/signin' `
        $publicResolve -Headers)
    Assert-DevOidcChallenge $headers $ClientId ([string]$discovery.authorization_endpoint)
    Write-Output "OIDC_STRUCTURAL_SMOKE_PASS $ExpectedSha"
    Write-Output 'REAL_LOGIN_NOT_VERIFIED no approved automated DEV identity'
}
