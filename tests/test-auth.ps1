param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$NewPassword = "MatFile-Test-2026",
    [string]$Compose = "$PSScriptRoot\..\docker-compose.dev.yml"
)

$ErrorActionPreference = "Stop"
$results = @()

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Test = $name; OK = $ok; Detail = $detail }
    $mark = if ($ok) { "OK  " } else { "FAIL" }
    Write-Host "$mark  $name  $detail"
}

function Get-Token([string]$html) {
    if ($html -match 'name="__RequestVerificationToken"[^>]*value="([^"]+)"') { return $Matches[1] }
    return $null
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

# 1. Ohne Anmeldung auf die Startseite -> Login, Standardsprache Englisch
$r = Invoke-WebRequest "$BaseUrl/" -WebSession $session -UseBasicParsing
Add-Result "Unangemeldet -> Login" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/Login') $r.BaseResponse.ResponseUri.AbsolutePath
Add-Result "Standardsprache Englisch" ($r.Content -match 'Sign in' -and $r.Content -notmatch 'Anmelden') ""

# 2. Falsches Kennwort
$token = Get-Token $r.Content
$r2 = Invoke-WebRequest "$BaseUrl/Login" -Method Post -WebSession $session -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    'Input.Username'           = 'admin'
    'Input.Password'           = 'definitiv-falsch'
}
Add-Result "Falsches Kennwort abgelehnt" ($r2.Content -match 'Username or password is incorrect') ""

# 3. Richtiges Kennwort -> erzwungener Kennwortwechsel
$token = Get-Token $r2.Content
$r3 = Invoke-WebRequest "$BaseUrl/Login" -Method Post -WebSession $session -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    'Input.Username'           = 'admin'
    'Input.Password'           = $SeedPassword
}
Add-Result "Anmeldung ohne erzwungenen Wechsel" ($r3.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r3.BaseResponse.ResponseUri.AbsolutePath

$cookie = $session.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq 'matfile.session' }
Add-Result "Session-Cookie gesetzt" ($null -ne $cookie) $(if ($cookie) { "HttpOnly=$($cookie.HttpOnly)" } else { "" })

# 4. Seite zum Kennwortwechsel ist erreichbar
$r4 = Invoke-WebRequest "$BaseUrl/Account/ChangePassword" -WebSession $session -UseBasicParsing
Add-Result "Kennwortwechsel erreichbar" ($r4.BaseResponse.ResponseUri.AbsolutePath -eq '/Account/ChangePassword') $r4.BaseResponse.ResponseUri.AbsolutePath

# 5. Kennwort aendern
$token = Get-Token $r4.Content
$r5 = Invoke-WebRequest "$BaseUrl/Account/ChangePassword" -Method Post -WebSession $session -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    'Input.CurrentPassword'    = $SeedPassword
    'Input.NewPassword'        = $NewPassword
    'Input.ConfirmPassword'    = $NewPassword
}
Add-Result "Kennwort geaendert" ($r5.Content -match 'The password was changed') ""

# 6. Startseite jetzt erreichbar
$r6 = Invoke-WebRequest "$BaseUrl/" -WebSession $session -UseBasicParsing
Add-Result "Startseite erreichbar" ($r6.Content -match 'mfx-listing' -and $r6.Content -match '>Storage locations') ""

# 7. Sprache auf Deutsch umstellen
$token = Get-Token $r6.Content
$r7 = Invoke-WebRequest "$BaseUrl/SetLanguage" -Method Post -WebSession $session -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    culture                    = 'de'
    returnUrl                  = '/'
}
Add-Result "Sprachwechsel auf Deutsch" ($r7.Content -match '>Speicherorte') $r7.BaseResponse.ResponseUri.AbsolutePath

# 8. Neustart des Containers -> Session muss bestehen bleiben
# Docker schreibt den Fortschritt auf stderr - das darf den Test nicht abbrechen.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { docker compose -f $Compose restart matfile 2>&1 | Out-Null }
finally { $ErrorActionPreference = $previous }

$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $h = Invoke-RestMethod "$BaseUrl/healthz" -TimeoutSec 3
        if ($h.status -eq 'healthy') { $ready = $true; break }
    } catch { }
    Start-Sleep -Milliseconds 700
}
Add-Result "Container nach Neustart bereit" $ready ""

$r8 = Invoke-WebRequest "$BaseUrl/" -WebSession $session -UseBasicParsing
Add-Result "Session ueberlebt Neustart" ($r8.Content -match '>Speicherorte') $r8.BaseResponse.ResponseUri.AbsolutePath

# 9. Abmelden
$token = Get-Token $r8.Content
$r9 = Invoke-WebRequest "$BaseUrl/Logout" -Method Post -WebSession $session -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
}
Add-Result "Abmeldung" ($r9.BaseResponse.ResponseUri.AbsolutePath -eq '/Login') $r9.BaseResponse.ResponseUri.AbsolutePath

$r10 = Invoke-WebRequest "$BaseUrl/" -WebSession $session -UseBasicParsing
Add-Result "Nach Abmeldung gesperrt" ($r10.BaseResponse.ResponseUri.AbsolutePath -eq '/Login') $r10.BaseResponse.ResponseUri.AbsolutePath

# 10. Frische Sitzung ohne Cookies: Englisch, nach Anmeldung gespeicherte Sprache Deutsch
$fresh = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$f1 = Invoke-WebRequest "$BaseUrl/Login" -WebSession $fresh -UseBasicParsing
Add-Result "Neue Sitzung startet englisch" ($f1.Content -match 'Sign in' -and $f1.Content -notmatch 'Anmelden') ""

$token = Get-Token $f1.Content
$f2 = Invoke-WebRequest "$BaseUrl/Login" -Method Post -WebSession $fresh -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    'Input.Username'           = 'admin'
    'Input.Password'           = $NewPassword
}
Add-Result "Anmeldung mit neuem Kennwort" ($f2.BaseResponse.ResponseUri.AbsolutePath -eq '/') $f2.BaseResponse.ResponseUri.AbsolutePath
Add-Result "Sprachwahl bleibt am Benutzer" ($f2.Content -match '>Speicherorte') ""

# 11. Zurueck auf Englisch
$token = Get-Token $f2.Content
$f3 = Invoke-WebRequest "$BaseUrl/SetLanguage" -Method Post -WebSession $fresh -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    culture                    = 'en'
    returnUrl                  = '/'
}
Add-Result "Sprachwechsel zurueck auf Englisch" ($f3.Content -match '>Storage locations') ""

# 12. Startkennwort wiederherstellen - sonst scheitern die folgenden Suiten an der Anmeldung.
# Das kurze Startkennwort unterschreitet die Mindestlaenge, deshalb kurz absenken.
$system = Invoke-RestMethod "$BaseUrl/api/system" -WebSession $fresh
$body = @{
    sessionLifetimeDays = $system.sessionLifetimeDays
    loginMaxFailedAttempts = $system.loginMaxFailedAttempts
    loginLockoutMinutes = $system.loginLockoutMinutes
    minPasswordLength = 4
    requireSecureCookie = $system.requireSecureCookie
    requirePasswordChangeOnFirstLogin = $system.requirePasswordChangeOnFirstLogin
}
Invoke-RestMethod "$BaseUrl/api/system" -Method Put -WebSession $fresh -ContentType 'application/json' `
    -Body ($body | ConvertTo-Json) | Out-Null

$c1 = Invoke-WebRequest "$BaseUrl/Account/ChangePassword" -WebSession $fresh -UseBasicParsing
$token = Get-Token $c1.Content
$c2 = Invoke-WebRequest "$BaseUrl/Account/ChangePassword" -Method Post -WebSession $fresh -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    'Input.CurrentPassword'    = $NewPassword
    'Input.NewPassword'        = $SeedPassword
    'Input.ConfirmPassword'    = $SeedPassword
}
Add-Result "Startkennwort wiederhergestellt" ($c2.Content -match 'The password was changed') ""

$body.minPasswordLength = $system.minPasswordLength
$restored = Invoke-RestMethod "$BaseUrl/api/system" -Method Put -WebSession $fresh -ContentType 'application/json' `
    -Body ($body | ConvertTo-Json)
Add-Result "Mindestlaenge wieder wie zuvor" (
    $restored.minPasswordLength -eq $system.minPasswordLength) "min=$($restored.minPasswordLength)"

# 13. Health ohne Anmeldung
$anon = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$h2 = Invoke-RestMethod "$BaseUrl/healthz" -WebSession $anon
Add-Result "healthz anonym" ($h2.status -eq 'healthy' -and $h2.databaseReady -eq $true) "status=$($h2.status)"

# 14. Anmeldeversuche je Minute begrenzt.
# Steht bewusst am Ende: danach ist die Anmeldung fuer diese Adresse eine Minute
# lang gebremst. Der Benutzername existiert nicht - so wird kein Konto gesperrt.
$flood = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$page = Invoke-WebRequest "$BaseUrl/Login" -WebSession $flood -UseBasicParsing
$token = Get-Token $page.Content
$body = "__RequestVerificationToken=$([System.Uri]::EscapeDataString($token))&Input.Username=gibtsnicht&Input.Password=egal"
$codes = @()

for ($i = 0; $i -lt 25; $i++) {
    try {
        $attempt = Invoke-WebRequest "$BaseUrl/Login" -Method Post -WebSession $flood -UseBasicParsing `
            -Body $body -ContentType 'application/x-www-form-urlencoded'
        $codes += [int]$attempt.StatusCode
    } catch {
        $codes += [int]$_.Exception.Response.StatusCode
        $blocked = $_.Exception.Response
    }
}

$limited = @($codes | Where-Object { $_ -eq 429 }).Count
Add-Result "Anmeldeversuche begrenzt" ($limited -ge 1) "$limited von $($codes.Count) abgewiesen"
Add-Result "Hinweis auf Wartezeit" ($blocked -and $blocked.Headers['Retry-After'] -eq '60') "Retry-After=$($blocked.Headers['Retry-After'])"

$stillOpen = Invoke-WebRequest "$BaseUrl/Login" -WebSession $flood -UseBasicParsing
Add-Result "Anmeldeseite bleibt erreichbar" ($stillOpen.StatusCode -eq 200) "status=$($stillOpen.StatusCode)"

$stillHealthy = Invoke-RestMethod "$BaseUrl/healthz"
Add-Result "Andere Aufrufe ungebremst" ($stillHealthy.status -eq 'healthy') "status=$($stillHealthy.status)"

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
