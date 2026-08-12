param(
    [string]$BaseUrl = "http://localhost:2222",
    [Parameter(Mandatory = $true)][string]$SeedPassword,
    [string]$NewPassword = "MatFile-Test-2026"
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
Add-Result "Anmeldung -> Kennwortwechsel" ($r3.BaseResponse.ResponseUri.AbsolutePath -eq '/Account/ChangePassword') $r3.BaseResponse.ResponseUri.AbsolutePath

$cookie = $session.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq 'matfile.session' }
Add-Result "Session-Cookie gesetzt" ($null -ne $cookie) $(if ($cookie) { "HttpOnly=$($cookie.HttpOnly)" } else { "" })

# 4. Andere Seiten sind bis zum Wechsel gesperrt
$r4 = Invoke-WebRequest "$BaseUrl/" -WebSession $session -UseBasicParsing
Add-Result "Kennwortwechsel erzwungen" ($r4.BaseResponse.ResponseUri.AbsolutePath -eq '/Account/ChangePassword') $r4.BaseResponse.ResponseUri.AbsolutePath

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
Add-Result "Startseite erreichbar" ($r6.Content -match 'Welcome' -and $r6.Content -match 'Administrator') ""

# 7. Sprache auf Deutsch umstellen
$token = Get-Token $r6.Content
$r7 = Invoke-WebRequest "$BaseUrl/SetLanguage" -Method Post -WebSession $session -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    culture                    = 'de'
    returnUrl                  = '/'
}
Add-Result "Sprachwechsel auf Deutsch" ($r7.Content -match 'Willkommen') $r7.BaseResponse.ResponseUri.AbsolutePath

# 8. Neustart des Containers -> Session muss bestehen bleiben
docker compose -f C:\Users\Matthias\Desktop\Development\MatFile\docker-compose.dev.yml restart matfile | Out-Null

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
Add-Result "Session ueberlebt Neustart" ($r8.Content -match 'Willkommen') $r8.BaseResponse.ResponseUri.AbsolutePath

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
Add-Result "Sprachwahl bleibt am Benutzer" ($f2.Content -match 'Willkommen') ""

# 11. Zurueck auf Englisch
$token = Get-Token $f2.Content
$f3 = Invoke-WebRequest "$BaseUrl/SetLanguage" -Method Post -WebSession $fresh -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    culture                    = 'en'
    returnUrl                  = '/'
}
Add-Result "Sprachwechsel zurueck auf Englisch" ($f3.Content -match 'Welcome') ""

# 12. Health ohne Anmeldung
$anon = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$h2 = Invoke-RestMethod "$BaseUrl/healthz" -WebSession $anon
Add-Result "healthz anonym" ($h2.status -eq 'healthy' -and $h2.databaseReady -eq $true) "status=$($h2.status)"

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
