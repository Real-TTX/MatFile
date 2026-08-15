param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$AdminPassword = "admin",
    [string]$TesterPassword = "Tester-2026-abc"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\_helpers.ps1"
$results = @()

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Test = $name; OK = $ok; Detail = $detail }
    $mark = if ($ok) { "OK  " } else { "FAIL" }
    Write-Host "$mark  $name  $detail"
}

# --- Anmeldung als Administrator -----------------------------------------
$admin = New-MatSession
$r = Connect-Mat $BaseUrl $admin 'admin' $SeedPassword
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

# Der Explorer traegt die Verwaltung jetzt selbst - kein Adminbereich mehr
$page = Invoke-WebRequest "$BaseUrl/" -WebSession $admin -UseBasicParsing
Add-Result "Menueleiste mit Verwaltung" (
    $page.Content -match 'mfx-menubar' -and $page.Content -match 'data-menu="settings"' -and
    $page.Content -match 'data-menu="admin"') ""
Add-Result "Rechte im Seitenkopf" (
    $page.Content -match 'manageUsers: true' -and $page.Content -match 'manageRoles: true' -and
    $page.Content -match 'manageSettings: true') ""
$gone = Get-MatJson "$BaseUrl/Admin/Users" $admin
Add-Result "Alte Adminseite entfernt" ($gone.Status -eq 404) "status=$($gone.Status)"

# --- Benutzerliste --------------------------------------------------------
$list = Get-MatJson "$BaseUrl/api/users" $admin
$names = $list.Data.items | ForEach-Object { $_.username }
Add-Result "Benutzerliste erreichbar" ($list.Status -eq 200 -and $names -contains 'admin') ($names -join ', ')
Add-Result "Seiteninformationen vorhanden" (
    $list.Data.page -eq 1 -and $list.Data.pageCount -ge 1 -and $list.Data.total -ge 1) "total=$($list.Data.total)"

$roles = @{}
foreach ($role in $list.Data.roles) { $roles[$role.name] = $role.id }
Add-Result "Rollen zur Auswahl" ($roles.ContainsKey('Administrator') -and $roles.ContainsKey('User')) ($roles.Keys -join ', ')

# --- Neuen Benutzer anlegen ----------------------------------------------
$created = New-MatUser $BaseUrl $admin @{
    username = 'tester'; displayName = 'Test Benutzer'; email = 'tester@example.com'
    newPassword = $TesterPassword; roleIds = @($roles['User'])
}
Add-Result "Benutzer angelegt" ($created.Status -eq 200 -and $created.Data.id -gt 0) "id=$($created.Data.id)"
$testerId = $created.Data.id

$detail = (Get-MatJson "$BaseUrl/api/users/$testerId" $admin).Data
Add-Result "Benutzer abrufbar" (
    $detail.username -eq 'tester' -and $detail.displayName -eq 'Test Benutzer' -and
    $detail.roleIds -contains $roles['User']) "$($detail.username)"

$found = Get-MatJson "$BaseUrl/api/users?search=tester" $admin
$foundNames = $found.Data.items | ForEach-Object { $_.username }
Add-Result "Suche filtert" ($foundNames -contains 'tester' -and $foundNames -notcontains 'admin') ($foundNames -join ', ')

$sorted = Get-MatJson "$BaseUrl/api/users?sort=username&dir=desc" $admin
Add-Result "Sortierung absteigend" ($sorted.Data.items[0].username -eq 'tester') "erster=$($sorted.Data.items[0].username)"

$again = New-MatUser $BaseUrl $admin @{ username = 'tester'; newPassword = $TesterPassword }
Add-Result "Doppelter Benutzername abgelehnt" (
    $again.Status -eq 400 -and $again.Data.error -match 'already taken') "status=$($again.Status)"

$noPassword = New-MatUser $BaseUrl $admin @{ username = 'ohnekennwort' }
Add-Result "Neuer Benutzer braucht Kennwort" ($noPassword.Status -eq 400) "status=$($noPassword.Status)"

# --- Rechte pruefen -------------------------------------------------------
$tester = New-MatSession
$r = Connect-Mat $BaseUrl $tester 'tester' $TesterPassword
Add-Result "Testbenutzer angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath
Add-Result "Keine Verwaltungsrechte in der Oberflaeche" (
    $r.Content -match 'manageUsers: false' -and $r.Content -match 'manageRoles: false') ""
Add-Result "Kein Speicherortmenue" ($r.Content -notmatch 'data-menu="locations"') ""
Add-Result "Keine Verwaltung in der Menueleiste" ($r.Content -notmatch 'data-menu="admin"') ""

$denied = Get-MatJson "$BaseUrl/api/users" $tester
Add-Result "Benutzerverwaltung gesperrt" ($denied.Status -eq 403) "status=$($denied.Status)"

$deniedRoles = Get-MatJson "$BaseUrl/api/roles" $tester
Add-Result "Rollenverwaltung gesperrt" ($deniedRoles.Status -eq 403) "status=$($deniedRoles.Status)"

Add-Result "Kein Recht auf Systemeinstellungen" ($r.Content -match 'manageSettings: false') ""
$deniedSystem = Get-MatJson "$BaseUrl/api/system" $tester
Add-Result "Systemeinstellungen gesperrt" ($deniedSystem.Status -eq 403) "status=$($deniedSystem.Status)"

$anon = New-MatSession
$unauth = Get-MatJson "$BaseUrl/api/users" $anon
Add-Result "Ohne Anmeldung gesperrt" ($unauth.Status -eq 401) "status=$($unauth.Status)"
$unauthSystem = Get-MatJson "$BaseUrl/api/system" $anon
Add-Result "Systemeinstellungen ohne Anmeldung gesperrt" ($unauthSystem.Status -eq 401) "status=$($unauthSystem.Status)"

$deniedLog = Get-MatJson "$BaseUrl/api/activity" $tester
Add-Result "Protokoll ohne Recht gesperrt" ($deniedLog.Status -eq 403) "status=$($deniedLog.Status)"
$unauthLog = Get-MatJson "$BaseUrl/api/activity" $anon
Add-Result "Protokoll ohne Anmeldung gesperrt" ($unauthLog.Status -eq 401) "status=$($unauthLog.Status)"

# --- Rollen ---------------------------------------------------------------
$roleList = Get-MatJson "$BaseUrl/api/roles" $admin
$roleNames = $roleList.Data.items | ForEach-Object { $_.name }
Add-Result "Rollenliste" ($roleList.Status -eq 200 -and $roleNames -contains 'Administrator') ($roleNames -join ', ')
$catalog = $roleList.Data.catalog | ForEach-Object { $_.name }
Add-Result "Rechtekatalog vollstaendig" (
    $catalog -contains 'UseExplorer' -and $catalog -contains 'ManageRoots' -and $catalog.Count -eq 7) "n=$($catalog.Count)"

$adminRole = $roleList.Data.items | Where-Object { $_.name -eq 'Administrator' }
Add-Result "Systemrolle erkennbar" ($adminRole.isSystem -eq $true -and $adminRole.permissions.Count -eq 7) "rechte=$($adminRole.permissions.Count)"

$newRole = Send-MatJson 'Post' "$BaseUrl/api/roles" $admin @{
    name = 'Testrolle'; description = 'Nur zum Testen'; permissions = @('UseExplorer', 'ManageRoots')
}
Add-Result "Rolle angelegt" ($newRole.Status -eq 200 -and $newRole.Data.id -gt 0) "id=$($newRole.Data.id)"
$testRoleId = $newRole.Data.id

$roleDetail = (Get-MatJson "$BaseUrl/api/roles/$testRoleId" $admin).Data
Add-Result "Rechte gespeichert" (
    $roleDetail.permissions -contains 'UseExplorer' -and $roleDetail.permissions -contains 'ManageRoots' -and
    $roleDetail.permissions.Count -eq 2) ($roleDetail.permissions -join ', ')

$renamed = Send-MatJson 'Put' "$BaseUrl/api/roles/$testRoleId" $admin @{
    name = 'Testrolle neu'; description = 'Geaendert'; permissions = @('UseExplorer')
}
$afterRename = (Get-MatJson "$BaseUrl/api/roles/$testRoleId" $admin).Data
Add-Result "Rolle geaendert" (
    $renamed.Status -eq 200 -and $afterRename.name -eq 'Testrolle neu' -and
    $afterRename.permissions.Count -eq 1) "$($afterRename.name)"

$systemProtected = Send-MatJson 'Delete' "$BaseUrl/api/roles/$($roles['Administrator'])" $admin $null
Add-Result "Systemrolle geschuetzt" (
    $systemProtected.Status -eq 400 -and $systemProtected.Data.error -match 'System roles') "status=$($systemProtected.Status)"

$roleInUse = Send-MatJson 'Delete' "$BaseUrl/api/roles/$($roles['User'])" $admin $null
Add-Result "Rolle in Benutzung nicht loeschbar" ($roleInUse.Status -eq 400) "status=$($roleInUse.Status)"

$removedRole = Send-MatJson 'Delete' "$BaseUrl/api/roles/$testRoleId" $admin $null
$afterDelete = Get-MatJson "$BaseUrl/api/roles" $admin
$afterNames = $afterDelete.Data.items | ForEach-Object { $_.name }
Add-Result "Rolle geloescht" ($removedRole.Status -eq 200 -and $afterNames -notcontains 'Testrolle neu') ($afterNames -join ', ')

# --- Schutzregeln fuer Benutzer ------------------------------------------
$adminId = ($list.Data.items | Where-Object { $_.username -eq 'admin' }).id
$self = Send-MatJson 'Delete' "$BaseUrl/api/users/$adminId" $admin $null
Add-Result "Eigenes Konto geschuetzt" (
    $self.Status -eq 400 -and $self.Data.error -match 'own account') "status=$($self.Status)"

$deactivate = Send-MatJson 'Put' "$BaseUrl/api/users/$adminId" $admin @{
    username = 'admin'; displayName = 'Administrator'; isActive = $false; roleIds = @($roles['Administrator'])
}
Add-Result "Eigenes Konto nicht abschaltbar" ($deactivate.Status -eq 400) "status=$($deactivate.Status)"

$removed = Send-MatJson 'Delete' "$BaseUrl/api/users/$testerId" $admin $null
$afterUsers = (Get-MatJson "$BaseUrl/api/users" $admin).Data.items | ForEach-Object { $_.username }
Add-Result "Benutzer geloescht" ($removed.Status -eq 200 -and $afterUsers -notcontains 'tester') ($afterUsers -join ', ')

$r = Invoke-WebRequest "$BaseUrl/" -WebSession $tester -UseBasicParsing
Add-Result "Sitzung des geloeschten Benutzers beendet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/Login') $r.BaseResponse.ResponseUri.AbsolutePath

# --- Kennwortwechsel je Benutzer ------------------------------------------
$forced = New-MatUser $BaseUrl $admin @{
    username = 'wechsler'; newPassword = $TesterPassword
    mustChangePassword = $true; roleIds = @($roles['User'])
}
Add-Result "Benutzer mit Kennwortzwang angelegt" ($forced.Status -eq 200) "status=$($forced.Status)"

$forcedSession = New-MatSession
$r = Connect-Mat $BaseUrl $forcedSession 'wechsler' $TesterPassword
Add-Result "Anmeldung fuehrt zum Kennwortwechsel" (
    $r.BaseResponse.ResponseUri.AbsolutePath -eq '/Account/ChangePassword') $r.BaseResponse.ResponseUri.AbsolutePath

$stillForced = (Get-MatJson "$BaseUrl/api/users/$($forced.Data.id)" $admin).Data
Add-Result "Kennwortzwang steht am Benutzer" ($stillForced.mustChangePassword) "flag=$($stillForced.mustChangePassword)"

$token = Get-MatToken (Invoke-WebRequest "$BaseUrl/Account/ChangePassword" -WebSession $forcedSession -UseBasicParsing).Content
$changed = Invoke-WebRequest "$BaseUrl/Account/ChangePassword" -Method Post -WebSession $forcedSession -UseBasicParsing -Body @{
    __RequestVerificationToken = $token
    'Input.CurrentPassword'    = $TesterPassword
    'Input.NewPassword'        = "$TesterPassword-neu"
    'Input.ConfirmPassword'    = "$TesterPassword-neu"
}
Add-Result "Kennwort gewechselt" ($changed.Content -match 'The password was changed') ""

$afterChange = (Get-MatJson "$BaseUrl/api/users/$($forced.Data.id)" $admin).Data
Add-Result "Kennwortzwang danach aufgehoben" (-not $afterChange.mustChangePassword) "flag=$($afterChange.mustChangePassword)"

Send-MatJson 'Delete' "$BaseUrl/api/users/$($forced.Data.id)" $admin $null | Out-Null

# --- Systemeinstellungen --------------------------------------------------
$defaults = @{
    sessionLifetimeDays = 14; loginMaxFailedAttempts = 5; loginLockoutMinutes = 15
    minPasswordLength = 8; requireSecureCookie = $false; requirePasswordChangeOnFirstLogin = $false
}
$system = Get-MatJson "$BaseUrl/api/system" $admin
Add-Result "Systemeinstellungen lesbar" (
    $system.Status -eq 200 -and $system.Data.minPasswordLength -ge 4 -and
    $system.Data.configFile -match 'matfile\.json' -and $system.Data.version) "$($system.Data.configFile)"

$saved = Send-MatJson 'Put' "$BaseUrl/api/system" $admin @{
    sessionLifetimeDays = 30; loginMaxFailedAttempts = 3; loginLockoutMinutes = 10
    minPasswordLength = 20; requireSecureCookie = $false; requirePasswordChangeOnFirstLogin = $true
}
Add-Result "Systemeinstellungen gespeichert" (
    $saved.Status -eq 200 -and $saved.Data.minPasswordLength -eq 20 -and
    $saved.Data.sessionLifetimeDays -eq 30 -and $saved.Data.requirePasswordChangeOnFirstLogin) "status=$($saved.Status)"

$reread = Get-MatJson "$BaseUrl/api/system" $admin
Add-Result "Systemeinstellungen dauerhaft" (
    $reread.Data.loginMaxFailedAttempts -eq 3 -and $reread.Data.loginLockoutMinutes -eq 10) "versuche=$($reread.Data.loginMaxFailedAttempts)"

$tooShort = New-MatUser $BaseUrl $admin @{ username = 'kurzeskennwort'; newPassword = 'nur8zeic' }
Add-Result "Mindestlaenge wirkt sofort" (
    $tooShort.Status -eq 400 -and $tooShort.Data.error -match '20') "status=$($tooShort.Status)"

$clamped = Send-MatJson 'Put' "$BaseUrl/api/system" $admin @{
    sessionLifetimeDays = 9999; loginMaxFailedAttempts = 0; loginLockoutMinutes = 99999
    minPasswordLength = 1; requireSecureCookie = $false; requirePasswordChangeOnFirstLogin = $false
}
Add-Result "Werte auf gueltigen Bereich begrenzt" (
    $clamped.Data.sessionLifetimeDays -eq 365 -and $clamped.Data.loginMaxFailedAttempts -eq 1 -and
    $clamped.Data.loginLockoutMinutes -eq 1440 -and $clamped.Data.minPasswordLength -eq 4) "tage=$($clamped.Data.sessionLifetimeDays)"

$restored = Send-MatJson 'Put' "$BaseUrl/api/system" $admin $defaults
Add-Result "Systemeinstellungen zurueckgesetzt" (
    $restored.Status -eq 200 -and $restored.Data.minPasswordLength -eq 8) "status=$($restored.Status)"

# --- Protokoll ------------------------------------------------------------
$log = Get-MatJson "$BaseUrl/api/activity?pageSize=10" $admin
$actions = $log.Data.items | ForEach-Object { $_.action }
Add-Result "Protokoll lesbar" (
    $log.Status -eq 200 -and $log.Data.total -ge 1 -and $actions -contains 'Login') "total=$($log.Data.total)"
Add-Result "Protokoll seitenweise" (
    @($log.Data.items).Count -le 10 -and $log.Data.page -eq 1 -and $log.Data.pageCount -ge 1) "seiten=$($log.Data.pageCount)"
Add-Result "Auswahl fuer die Filter" (
    $log.Data.actions -contains 'Login' -and $null -ne $log.Data.roots) "aktionen=$($log.Data.actions.Count)"

$newest = $log.Data.items[0].date
$older = $log.Data.items[-1].date
Add-Result "Neueste zuerst" ([datetime]$newest -ge [datetime]$older) "$newest >= $older"

$filtered = Get-MatJson "$BaseUrl/api/activity?action=Login" $admin
$onlyLogin = @($filtered.Data.items | Where-Object { $_.action -ne 'Login' }).Count
Add-Result "Nach Aktion gefiltert" (
    $filtered.Status -eq 200 -and $filtered.Data.total -ge 1 -and $onlyLogin -eq 0) "fremde=$onlyLogin"

$nothing = Get-MatJson "$BaseUrl/api/activity?action=GibtsNicht" $admin
Add-Result "Filter ohne Treffer" ($nothing.Data.total -eq 0 -and @($nothing.Data.items).Count -eq 0) "total=$($nothing.Data.total)"

$byUser = Get-MatJson "$BaseUrl/api/activity?search=admin" $admin
Add-Result "Suche im Protokoll" ($byUser.Status -eq 200 -and $byUser.Data.total -ge 1) "total=$($byUser.Data.total)"

# --- Sprache --------------------------------------------------------------
$page = Invoke-WebRequest "$BaseUrl/" -WebSession $admin -UseBasicParsing
$t = Get-MatToken $page.Content
$r = Post-Form "$BaseUrl/SetLanguage" $admin "__RequestVerificationToken=$(Enc $t)&culture=de&returnUrl=$(Enc '/')"
Add-Result "Oberflaeche auf Deutsch" ($r.Content -match '>Bearbeiten<' -and $r.Content -match 'Einstellungen') ""

$t = Get-MatToken $r.Content
$r = Post-Form "$BaseUrl/SetLanguage" $admin "__RequestVerificationToken=$(Enc $t)&culture=en&returnUrl=$(Enc '/')"
Add-Result "Zurueck auf Englisch" ($r.Content -match '>Edit<' -and $r.Content -match '>View<') ""

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
