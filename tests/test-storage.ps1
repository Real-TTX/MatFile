param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$AdminPassword = "admin",
    [string]$TesterPassword = "Tester-2026-abc",
    [string]$Container = "matfile-dev"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\_helpers.ps1"
$results = @()

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Test = $name; OK = $ok; Detail = $detail }
    $mark = if ($ok) { "OK  " } else { "FAIL" }
    Write-Host "$mark  $name  $detail"
}

function Get-Api([string]$url, $session) { return Get-MatJson $url $session }

# Testdaten im Container anlegen: Unterordner, Datei und ein Symlink nach draussen
docker exec $Container sh -c "mkdir -p /storage/internal/sub && echo hallo > /storage/internal/datei.txt && ln -sfn /etc /storage/internal/escape" | Out-Null
Add-Result "Testdaten im Container" ($LASTEXITCODE -eq 0) ""

# --- Anmeldung ------------------------------------------------------------
$admin = New-MatSession
$r = Connect-Mat $BaseUrl $admin 'admin' $SeedPassword
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

# --- Speicherorte verwalten ----------------------------------------------
$empty = Get-MatJson "$BaseUrl/api/roots" $admin
Add-Result "Speicherortliste erreichbar" ($empty.Status -eq 200 -and $empty.Data.total -eq 0) "n=$($empty.Data.total)"

$roles = @{}
foreach ($role in $empty.Data.roles) { $roles[$role.roleName] = $role.roleId }
Add-Result "Rollenmatrix in der Liste" ($roles.ContainsKey('User') -and $roles.ContainsKey('Administrator')) ($roles.Keys -join ', ')

# Ungueltige Pfade
$relative = New-MatRoot $BaseUrl $admin @{ name = 'Fehler'; path = 'relativ/pfad' }
Add-Result "Relativer Pfad abgelehnt" (
    $relative.Status -eq 400 -and $relative.Data.error -match 'absolute path') "status=$($relative.Status)"

$missing = New-MatRoot $BaseUrl $admin @{ name = 'Fehler'; path = '/gibts/nicht' }
Add-Result "Fehlender Pfad abgelehnt" (
    $missing.Status -eq 400 -and $missing.Data.error -match 'does not exist in the container') "status=$($missing.Status)"

$dataDir = New-MatRoot $BaseUrl $admin @{ name = 'Datenordner'; path = '/data' }
Add-Result "Datenverzeichnis geschuetzt" (
    $dataDir.Status -eq 400 -and $dataDir.Data.error -match 'data directory cannot be shared') "status=$($dataDir.Status)"

$noName = New-MatRoot $BaseUrl $admin @{ name = ''; path = '/storage/sample' }
Add-Result "Name ist Pflicht" ($noName.Status -eq 400) "status=$($noName.Status)"

# Gueltiger Speicherort, Rolle User darf lesen und herunterladen
$sampleCreated = New-MatRoot $BaseUrl $admin @{
    name = 'Sample'; path = '/storage/sample'; icon = 'folder'; sortOrder = 1
    access = @((New-MatAccess $roles['User'] $true $true $false $false $false))
}
Add-Result "Speicherort angelegt" ($sampleCreated.Status -eq 200 -and $sampleCreated.Data.id -gt 0) "id=$($sampleCreated.Data.id)"

$listed = (Get-MatJson "$BaseUrl/api/roots" $admin).Data.items | Where-Object { $_.name -eq 'Sample' }
Add-Result "Speicherort ist verfuegbar" ($listed.isAvailable -eq $true -and $listed.roleCount -eq 1) "rollen=$($listed.roleCount)"

$duplicate = New-MatRoot $BaseUrl $admin @{ name = 'Sample'; path = '/storage/sample' }
Add-Result "Doppelter Name abgelehnt" (
    $duplicate.Status -eq 400 -and $duplicate.Data.error -match 'already exists') "status=$($duplicate.Status)"

# Zweiter Speicherort nur fuer Administratoren
$internCreated = New-MatRoot $BaseUrl $admin @{
    name = 'Intern'; path = '/storage/internal'; icon = 'hard-drive'; sortOrder = 2
}
Add-Result "Zweiter Speicherort angelegt" ($internCreated.Status -eq 200) "id=$($internCreated.Data.id)"

$access = (Get-MatJson "$BaseUrl/api/roots/$($sampleCreated.Data.id)" $admin).Data.access
$userAccess = $access | Where-Object { $_.roleName -eq 'User' }
Add-Result "Rechte je Rolle gespeichert" (
    $userAccess.canRead -and $userAccess.canDownload -and -not $userAccess.canUpload) ""

# --- API ------------------------------------------------------------------
$api = Get-Api "$BaseUrl/api/fs/roots" $admin
$sample = $api.Data | Where-Object { $_.name -eq 'Sample' }
$intern = $api.Data | Where-Object { $_.name -eq 'Intern' }
Add-Result "API liefert Roots" ($api.Status -eq 200 -and $null -ne $sample -and $null -ne $intern) "n=$($api.Data.Count)"
Add-Result "Administrator hat Vollzugriff" ($sample.permissions.canRead -and $sample.permissions.canDelete) ""

$list = Get-Api "$BaseUrl/api/fs/list?rootId=$($sample.id)" $admin
$names = $list.Data.entries | ForEach-Object { $_.name }
Add-Result "Listing der obersten Ebene" ($list.Status -eq 200 -and $names -contains 'Dokumente' -and $names -contains 'liesmich.txt') ($names -join ', ')
Add-Result "Ordner stehen vor Dateien" ($list.Data.entries[0].isDirectory -eq $true) ""

$sub = Get-Api "$BaseUrl/api/fs/list?rootId=$($sample.id)&path=Dokumente" $admin
Add-Result "Listing eines Unterordners" ($sub.Status -eq 200 -and ($sub.Data.entries | ForEach-Object { $_.name }) -contains 'notiz.txt') ""
Add-Result "Elternpfad gesetzt" ($sub.Data.parentPath -eq '') "parent='$($sub.Data.parentPath)'"

# Symlink nach draussen wird nicht angezeigt
$internList = Get-Api "$BaseUrl/api/fs/list?rootId=$($intern.id)" $admin
$internNames = $internList.Data.entries | ForEach-Object { $_.name }
Add-Result "Symlink nach aussen ausgeblendet" ($internNames -contains 'datei.txt' -and $internNames -notcontains 'escape') ($internNames -join ', ')

# Pfadausbruch
$escape = Get-Api "$BaseUrl/api/fs/list?rootId=$($sample.id)&path=$(Enc '../../etc')" $admin
Add-Result "Pfadausbruch mit .. blockiert" ($escape.Status -eq 400) "status=$($escape.Status)"

$absolute = Get-Api "$BaseUrl/api/fs/list?rootId=$($sample.id)&path=$(Enc '/etc/passwd')" $admin
Add-Result "Absoluter Pfad blockiert" ($absolute.Status -eq 404) "status=$($absolute.Status)"

$unknown = Get-Api "$BaseUrl/api/fs/list?rootId=999999" $admin
Add-Result "Unbekannter Root" ($unknown.Status -eq 404) "status=$($unknown.Status)"

# --- Rechte eines normalen Benutzers -------------------------------------
$userRoleId = (Get-MatJson "$BaseUrl/api/users" $admin).Data.roles | Where-Object { $_.name -eq 'User' }
$created = New-MatUser $BaseUrl $admin @{
    username = 'tester'; displayName = 'Tester'; newPassword = $TesterPassword; roleIds = @($userRoleId.id)
}
Add-Result "Testbenutzer angelegt" ($created.Status -eq 200) "id=$($created.Data.id)"

$tester = New-MatSession
$r = Connect-Mat $BaseUrl $tester 'tester' $TesterPassword
Add-Result "Testbenutzer angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

$api = Get-Api "$BaseUrl/api/fs/roots" $tester
$names = $api.Data | ForEach-Object { $_.name }
Add-Result "Nur freigegebene Roots sichtbar" ($api.Status -eq 200 -and $names -contains 'Sample' -and $names -notcontains 'Intern') ($names -join ', ')

$perm = ($api.Data | Where-Object { $_.name -eq 'Sample' }).permissions
Add-Result "Rechte aus der Rollenmatrix" ($perm.canRead -and $perm.canDownload -and -not $perm.canDelete -and -not $perm.canUpload) ""

$list = Get-Api "$BaseUrl/api/fs/list?rootId=$($sample.id)" $tester
Add-Result "Testbenutzer darf lesen" ($list.Status -eq 200) "status=$($list.Status)"

$denied = Get-Api "$BaseUrl/api/fs/list?rootId=$($intern.id)" $tester
Add-Result "Gesperrter Root nicht lesbar" ($denied.Status -eq 404) "status=$($denied.Status)"

$noManage = New-MatRoot $BaseUrl $tester @{ name = 'Verboten'; path = '/storage/sample' }
Add-Result "Ohne Recht kein Speicherort" ($noManage.Status -eq 403) "status=$($noManage.Status)"

$noBrowse = Send-MatJson 'Post' "$BaseUrl/api/roots/browse?path=" $tester @{
    name = 'x'; type = 'Local'; path = ''; port = 0; useTls = $false; passive = $true; isEnabled = $true; sortOrder = 0
}
Add-Result "Ohne Recht kein Durchsehen" ($noBrowse.Status -eq 403) "status=$($noBrowse.Status)"

# Der Ordnerbrowser des Dialogs zeigt den Container - ohne das Datenverzeichnis
$adminBrowse = Send-MatJson 'Post' "$BaseUrl/api/roots/browse?path=" $admin @{
    name = 'x'; type = 'Local'; path = ''; port = 0; useTls = $false; passive = $true; isEnabled = $true; sortOrder = 0
}
$browseNames = $adminBrowse.Data.entries | ForEach-Object { $_.name }
Add-Result "Ordnerbrowser zeigt nur Ordner" (
    $adminBrowse.Status -eq 200 -and $browseNames -contains 'storage' -and $browseNames -notcontains 'data') "n=$($browseNames.Count)"

# Abgemeldet kein API-Zugriff
$anon = New-MatSession
$anonApi = Get-Api "$BaseUrl/api/fs/roots" $anon
Add-Result "API ohne Anmeldung gesperrt" ($anonApi.Status -eq 401) "status=$($anonApi.Status)"

# Rechte nachtraeglich erweitern
$widened = Set-MatRootAccess $BaseUrl $admin $sample.id @((New-MatAccess $roles['User'] $true $true $true $true $false))
$afterWiden = (Get-Api "$BaseUrl/api/fs/roots" $tester).Data | Where-Object { $_.name -eq 'Sample' }
Add-Result "Rechte nachtraeglich erweitert" (
    $widened.Status -eq 200 -and $afterWiden.permissions.canUpload -and $afterWiden.permissions.canWrite) ""

# --- Speicherort auf der Containerwurzel ---------------------------------
# Wurzelpfade haben schon ein Trennzeichen - der Pfadschutz darf daran nicht scheitern.
$created = New-MatRoot $BaseUrl $admin @{ name = 'Wurzel'; path = '/'; icon = 'hard-drive'; sortOrder = 9 }
Add-Result "Wurzel als Speicherort erlaubt" ($created.Status -eq 200 -and $created.Data.id -gt 0) "id=$($created.Data.id)"
$wurzelId = $created.Data.id

$top = Get-Api "$BaseUrl/api/fs/list?rootId=$wurzelId" $admin
$topNames = $top.Data.entries | ForEach-Object { $_.name }
Add-Result "Wurzel listet den Container" ($top.Status -eq 200 -and $topNames -contains 'storage' -and $topNames -contains 'etc') "n=$($topNames.Count)"
Add-Result "Datenverzeichnis unsichtbar" ($topNames -notcontains 'data') ""

$deep = Get-Api "$BaseUrl/api/fs/list?rootId=$wurzelId&path=$(Enc 'storage/sample/Dokumente')" $admin
$deepNames = $deep.Data.entries | ForEach-Object { $_.name }
Add-Result "Unterordner unter der Wurzel" ($deep.Status -eq 200 -and $deepNames -contains 'notiz.txt') "status=$($deep.Status) parent='$($deep.Data.parentPath)'"

$firstLevel = Get-Api "$BaseUrl/api/fs/list?rootId=$wurzelId&path=usr" $admin
Add-Result "Elternpfad direkt unter der Wurzel" ($firstLevel.Status -eq 200 -and $firstLevel.Data.parentPath -eq '') "parent='$($firstLevel.Data.parentPath)'"

$dataDirList = Get-Api "$BaseUrl/api/fs/list?rootId=$wurzelId&path=data" $admin
Add-Result "Datenverzeichnis nicht betretbar" ($dataDirList.Status -eq 404) "status=$($dataDirList.Status)"

$dbFile = Get-Api "$BaseUrl/api/fs/download?rootId=$wurzelId&path=$(Enc 'data/matfile.db')" $admin
Add-Result "Datenbank nicht herunterladbar" ($dbFile.Status -eq 404) "status=$($dbFile.Status)"

$removed = Send-MatJson 'Delete' "$BaseUrl/api/roots/$wurzelId" $admin $null
$afterRemove = (Get-MatJson "$BaseUrl/api/roots" $admin).Data.items | ForEach-Object { $_.name }
Add-Result "Wurzel wieder entfernt" ($removed.Status -eq 204 -and $afterRemove -notcontains 'Wurzel') ($afterRemove -join ', ')

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
