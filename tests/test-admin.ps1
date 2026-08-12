param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$AdminPassword = "MatFile-Test-2026",
    [string]$TesterPassword = "Tester-2026-abc"
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

function Enc([string]$s) { return [System.Uri]::EscapeDataString($s) }

function Post-Form([string]$url, $session, [string]$body) {
    return Invoke-WebRequest $url -Method Post -WebSession $session -UseBasicParsing `
        -Body $body -ContentType 'application/x-www-form-urlencoded'
}

# --- Anmeldung als Administrator, Startkennwort wechseln -------------------
$admin = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$r = Invoke-WebRequest "$BaseUrl/Login" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Login" $admin "__RequestVerificationToken=$(Enc $t)&Input.Username=admin&Input.Password=$(Enc $SeedPassword)"
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Account/ChangePassword" $admin "__RequestVerificationToken=$(Enc $t)&Input.CurrentPassword=$(Enc $SeedPassword)&Input.NewPassword=$(Enc $AdminPassword)&Input.ConfirmPassword=$(Enc $AdminPassword)"
Add-Result "Administrator angemeldet" ($r.Content -match 'The password was changed') ""

# --- Adminbereich ---------------------------------------------------------
$r = Invoke-WebRequest "$BaseUrl/Admin" -WebSession $admin -UseBasicParsing
Add-Result "Adminbereich leitet auf Benutzer" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/Admin/Users') $r.BaseResponse.ResponseUri.AbsolutePath
Add-Result "Liste zeigt Toolbar, Tabelle, Pagination" (
    $r.Content -match 'mf-toolbar' -and $r.Content -match 'mf-table' -and
    $r.Content -match 'mf-pagination' -and $r.Content -match 'Page 1 of 1') ""
Add-Result "Adminbenutzer in der Liste" ($r.Content -match '>admin<') ""

# Sortierung
$r = Invoke-WebRequest "$BaseUrl/Admin/Users?sort=username&dir=desc" -WebSession $admin -UseBasicParsing
Add-Result "Sortierung aktiv" ($r.Content -match 'mf-th-sort is-active') ""

# --- Neuen Benutzer anlegen ----------------------------------------------
$r = Invoke-WebRequest "$BaseUrl/Admin/Users/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$roleIds = @{}
foreach ($m in [regex]::Matches($r.Content, '(?s)name="Input\.RoleIds" value="(\d+)".*?<span>([^<]+)</span>')) {
    $roleIds[$m.Groups[2].Value.Trim()] = $m.Groups[1].Value
}
Add-Result "Rollen im Formular" ($roleIds.ContainsKey('Administrator') -and $roleIds.ContainsKey('User')) ($roleIds.Keys -join ', ')

$body = "__RequestVerificationToken=$(Enc $t)&Input.Username=tester&Input.DisplayName=$(Enc 'Test Benutzer')&Input.Email=$(Enc 'tester@example.com')&Input.NewPassword=$(Enc $TesterPassword)&Input.IsActive=true&Input.MustChangePassword=false&Input.RoleIds=$($roleIds['User'])"
$r = Post-Form "$BaseUrl/Admin/Users/Edit" $admin $body
Add-Result "Benutzer angelegt" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/Admin/Users' -and $r.Content -match '>tester<') $r.BaseResponse.ResponseUri.AbsolutePath

# Suche
$r = Invoke-WebRequest "$BaseUrl/Admin/Users?search=tester" -WebSession $admin -UseBasicParsing
Add-Result "Suche filtert" ($r.Content -match '>tester<' -and $r.Content -notmatch '>admin<') ""

# Doppelter Benutzername
$r = Invoke-WebRequest "$BaseUrl/Admin/Users/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Admin/Users/Edit" $admin "__RequestVerificationToken=$(Enc $t)&Input.Username=tester&Input.NewPassword=$(Enc $TesterPassword)&Input.IsActive=true"
Add-Result "Doppelter Benutzername abgelehnt" ($r.Content -match 'already taken') ""

# --- Rechte pruefen: tester darf nicht in die Verwaltung ------------------
$tester = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$r = Invoke-WebRequest "$BaseUrl/Login" -WebSession $tester -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Login" $tester "__RequestVerificationToken=$(Enc $t)&Input.Username=tester&Input.Password=$(Enc $TesterPassword)"
Add-Result "Testbenutzer angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath
Add-Result "Kein Adminlink fuer Testbenutzer" ($r.Content -notmatch 'Administration') ""

$r = Invoke-WebRequest "$BaseUrl/Admin/Users" -WebSession $tester -UseBasicParsing
Add-Result "Adminbereich gesperrt" ($r.Content -match 'Access denied') $r.BaseResponse.ResponseUri.AbsolutePath

# --- Rollen ---------------------------------------------------------------
$r = Invoke-WebRequest "$BaseUrl/Admin/Roles" -WebSession $admin -UseBasicParsing
Add-Result "Rollenliste" ($r.Content -match 'Administrator' -and $r.Content -match 'Manage users') ""

$r = Invoke-WebRequest "$BaseUrl/Admin/Roles/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Admin/Roles/Edit" $admin "__RequestVerificationToken=$(Enc $t)&Input.Name=$(Enc 'Testrolle')&Input.Description=$(Enc 'Nur zum Testen')&Input.Permissions=UseExplorer&Input.Permissions=ManageRoots"
Add-Result "Rolle angelegt" ($r.Content -match 'Testrolle' -and $r.Content -match 'Manage root paths') ""

$testRoleId = $null
if ($r.Content -match 'Edit\?id=(\d+)&amp;returnUrl=[^"]*">Testrolle') { $testRoleId = $Matches[1] }
elseif ($r.Content -match 'id=(\d+)[^"]*">Testrolle') { $testRoleId = $Matches[1] }
Add-Result "Rollen-Id gefunden" ($null -ne $testRoleId) "id=$testRoleId"

# Systemrolle darf nicht geloescht werden
$adminRoleId = $roleIds['Administrator']
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Admin/Roles?handler=Delete" $admin "__RequestVerificationToken=$(Enc $t)&SelectedIds=$adminRoleId"
Add-Result "Systemrolle geschuetzt" ($r.Content -match 'System roles cannot be deleted') ""

# Testrolle loeschen
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Admin/Roles?handler=Delete" $admin "__RequestVerificationToken=$(Enc $t)&SelectedIds=$testRoleId"
Add-Result "Rolle geloescht" ($r.Content -match 'role\(s\) deleted' -and $r.Content -notmatch '>Testrolle<') ""

# --- Schutzregeln fuer Benutzer ------------------------------------------
$r = Invoke-WebRequest "$BaseUrl/Admin/Users" -WebSession $admin -UseBasicParsing
$adminUserId = $null
if ($r.Content -match 'Edit\?id=(\d+)[^"]*">admin<') { $adminUserId = $Matches[1] }
$testerUserId = $null
if ($r.Content -match 'Edit\?id=(\d+)[^"]*">tester<') { $testerUserId = $Matches[1] }
Add-Result "Benutzer-Ids gefunden" ($null -ne $adminUserId -and $null -ne $testerUserId) "admin=$adminUserId tester=$testerUserId"

$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Admin/Users?handler=Delete" $admin "__RequestVerificationToken=$(Enc $t)&SelectedIds=$adminUserId"
Add-Result "Eigenes Konto geschuetzt" ($r.Content -match 'cannot delete your own account') ""

$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Admin/Users?handler=Delete" $admin "__RequestVerificationToken=$(Enc $t)&SelectedIds=$testerUserId"
Add-Result "Benutzer geloescht" ($r.Content -match 'user\(s\) deleted' -and $r.Content -notmatch '>tester<') ""

# Sitzung des geloeschten Benutzers ist beendet
$r = Invoke-WebRequest "$BaseUrl/" -WebSession $tester -UseBasicParsing
Add-Result "Sitzung des geloeschten Benutzers beendet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/Login') $r.BaseResponse.ResponseUri.AbsolutePath

# --- Deutsche Oberflaeche im Adminbereich --------------------------------
$r = Invoke-WebRequest "$BaseUrl/Admin/Users" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/SetLanguage" $admin "__RequestVerificationToken=$(Enc $t)&culture=de&returnUrl=$(Enc '/Admin/Users')"
Add-Result "Adminbereich auf Deutsch" ($r.Content -match 'Benutzername' -and $r.Content -match 'Seite 1 von 1') ""

$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/SetLanguage" $admin "__RequestVerificationToken=$(Enc $t)&culture=en&returnUrl=$(Enc '/Admin/Users')"
Add-Result "Zurueck auf Englisch" ($r.Content -match 'Username') ""

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
