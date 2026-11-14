# Creates a Development JWT for each lab user and writes them all to tokens.md (replaced on every run).
# Run it from anywhere:  ./make-tokens.ps1
# If PowerShell blocks scripts:  powershell -ExecutionPolicy Bypass -File make-tokens.ps1
# Same thing for macOS/Linux: make-tokens.sh
$ErrorActionPreference = 'Stop'

$project  = 'src/Ticketing.Api'
$settings = Join-Path $PSScriptRoot "$project/appsettings.Development.json"
$out      = Join-Path $PSScriptRoot 'tokens.md'
$validFor = '30d'
$users    = @(@('alice', 'requester'), @('bob', 'requester'), @('dana', 'agent'), @('admin', 'admin'))

# user-jwts rewrites appsettings.Development.json on every run, even when nothing changes
# (line endings, final newline). If the Bearer settings are already there, put the file back as it was.
# If they aren't (Lab 3 Start), keep what user-jwts wrote: adding them is the point.
$backup = $null
if ((Test-Path $settings) -and (Select-String -Path $settings -Pattern 'dotnet-user-jwts' -Quiet)) {
    $backup = [IO.File]::ReadAllBytes($settings)
}

$minted = @()
Push-Location $PSScriptRoot
try {
    foreach ($u in $users) {
        $json = (& dotnet user-jwts create --project $project --name $u[0] --role $u[1] --valid-for $validFor --output json) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Couldn't create a token for $($u[0]). user-jwts said:`n$json" }
        $jwt = $json | ConvertFrom-Json
        $minted += [pscustomobject]@{ Name = $u[0]; Role = $u[1]; Token = $jwt.Token; Expires = $jwt.Expires }
        Write-Host "  $($u[0]) ($($u[1]))"
    }
}
finally {
    if ($backup) { [IO.File]::WriteAllBytes($settings, $backup) }
    Pop-Location
}

# ConvertFrom-Json turns the date into a DateTime in PowerShell 7 and leaves it a string in 5.1
$expires = $minted[0].Expires
if ($expires -is [datetime]) { $expires = $expires.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss+00:00") }
$fence = '```'

$lines = @(
    '# Dev tokens'
    ''
    "Made by ``make-tokens`` on $(Get-Date -Format 'yyyy-MM-dd HH:mm'). They expire $expires."
    'Run the script again any time for fresh ones. This file is replaced on every run.'
    ''
    '| User | Role | What they can do |'
    '|---|---|---|'
    '| alice | requester | Create, read, update, and comment on her own tickets |'
    "| bob | requester | The same, for his own. Try reading alice's ticket as bob |"
    '| dana | agent | Every ticket, plus assign, close, and bulk close |'
    '| admin | admin | Everything an agent can |'
    ''
    '## Lab client'
    ''
    'Pick the role in **Signed in as**, click **Set token**, and paste the token:'
)
foreach ($m in $minted) { $lines += @('', "**$($m.Name)** ($($m.Role))", '', $fence, $m.Token, $fence) }
$lines += @('', '## .http files', '', 'Paste these over the variables at the top of any file in `http/`:', '', $fence)
foreach ($m in $minted) { $lines += "@$($m.Name) = $($m.Token)" }
$lines += @(
    $fence
    ''
    '## Should a token ever be committed?'
    ''
    "These are harmless, and it's worth knowing why rather than assuming:"
    ''
    '- They''re signed with a key in *your* user secrets, so only the API on your machine accepts them.'
    '- The API only trusts this issuer (`dotnet-user-jwts`) in Development, and they expire.'
    ''
    'A real token is a different story. A JWT is a bearer credential: whoever holds it *is* that user until it expires.'
    "It's signed, not encrypted, so anyone can read what's inside. Never commit one, paste one into a ticket or chat, or write one to a log."
    'GitHub secret scanning and most CI scanners flag JWTs on push.'
    ''
    'That''s why `tokens.md` is in `.gitignore` even though these tokens don''t matter: build the habit on the ones that don''t.'
)

# UTF-8 without a BOM, LF line endings, same as the bash version
[IO.File]::WriteAllText($out, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
Write-Host "Wrote tokens.md (valid until $expires)"
