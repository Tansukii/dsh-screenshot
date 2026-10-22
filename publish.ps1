# Publishes this repository to GitHub.
#
# 1. Create a classic PAT with the "repo" scope: https://github.com/settings/tokens/new
# 2. Copy the token (Ctrl+C).
# 3. Run:  pwsh -File publish.ps1
#
# The token is read from the clipboard, never echoed, and cleared afterwards.
# The remote URL is reset to a clean https URL when done, so the token is not stored in .git/config.

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoDir = $PSScriptRoot
$name = 'dsh-screenshot'
$description = 'WeChat/QQ-style screenshot tool for DeepSeek Harness on Windows: instant region capture, annotations, colour picker, all local.'
$topics = @('dsh-plugin', 'screenshot', 'snipping-tool', 'wechat', 'windows', 'annotation')

$token = (Get-Clipboard -Raw).Trim()
if (-not $token -or $token.Length -lt 20) {
    throw 'No token on the clipboard. Copy the PAT first, then run this script again.'
}

$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'dsh-screenshot-publish'
}

# who are we pushing as
$me = Invoke-RestMethod -Uri 'https://api.github.com/user' -Headers $headers -Method Get
$owner = $me.login
"authenticated as: $owner"

# create the repository (422 means it already exists)
$body = @{
    name        = $name
    description = $description
    private     = $false
    has_issues  = $true
    has_wiki    = $false
    auto_init   = $false
} | ConvertTo-Json
try {
    $repo = Invoke-RestMethod -Uri 'https://api.github.com/user/repos' -Headers $headers -Method Post -Body $body -ContentType 'application/json'
    "created: $($repo.html_url)"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    if ($code -eq 422) { "repository already exists, reusing it" }
    else { throw }
}

# push (token only in this process, removed from the remote afterwards)
Push-Location $repoDir
try {
    git branch -M main 2>&1 | Out-Null
    git remote remove origin 2>&1 | Out-Null
    git remote add origin "https://$owner`:$token@github.com/$owner/$name.git"
    git -c credential.helper= push -u origin main 2>&1 | ForEach-Object { "  $_" }
    git remote set-url origin "https://github.com/$owner/$name.git"
} finally {
    Pop-Location
}

# topics and description
$topicBody = @{ names = $topics } | ConvertTo-Json
Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$name/topics" -Headers $headers -Method Put -Body $topicBody -ContentType 'application/json' | Out-Null
Invoke-RestMethod -Uri "https://api.github.com/repos/$owner/$name" -Headers $headers -Method Patch -Body (@{ description = $description } | ConvertTo-Json) -ContentType 'application/json' | Out-Null

Set-Clipboard -Value ''
""
"done -> https://github.com/$owner/$name"
"topics: $($topics -join ', ')"
