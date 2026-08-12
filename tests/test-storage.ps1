param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$AdminPassword = "MatFile-Test-2026",
    [string]$TesterPassword = "Tester-2026-abc",
    [string]$Container = "matfile-dev"
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

function Get-Api([string]$url, $session) {
    try {
        $r = Invoke-WebRequest $url -WebSession $session -UseBasicParsing
        return @{ Status = [int]$r.StatusCode; Data = ($r.Content | ConvertFrom-Json) }
    } catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        return @{ Status = $status; Data = $null }
    }
}

# Testdaten im Container anlegen: Unterordner, Datei und ein Symlink nach draussen
docker exec $Container sh -c "mkdir -p /storage/internal/sub && echo hallo > /storage/internal/datei.txt && ln -sfn /etc /storage/internal/escape" | Out-Null
Add-Result "Testdaten im Container" ($LASTEXITCODE -eq 0) ""

# --- Anmeldung ------------------------------------------------------------
$admin = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$r = Invoke-WebRequest "$BaseUrl/Login" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Login" $admin "__RequestVerificationToken=$(Enc $t)&Input.Username=admin&Input.Password=$(Enc $SeedPassword)"
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Account/ChangePassword" $admin "__RequestVerificationToken=$(Enc $t)&Input.CurrentPassword=$(Enc $SeedPassword)&Input.NewPassword=$(Enc $AdminPassword)&Input.ConfirmPassword=$(Enc $AdminPassword)"
Add-Result "Administrator angemeldet" ($r.Content -match 'The password was changed') ""

# --- Root-Verwaltung ------------------------------------------------------
$r = Invoke-WebRequest "$BaseUrl/Admin/Roots" -WebSession $admin -UseBasicParsing
Add-Result "Rootliste erreichbar" ($r.Content -match 'No root paths configured') ""

# Rollen-Ids aus dem Formular lesen
$r = Invoke-WebRequest "$BaseUrl/Admin/Roots/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$roleRows = @{}
foreach ($m in [regex]::Matches($r.Content, '(?s)name="Input\.Access\[(\d+)\]\.RoleId" value="(\d+)"[^>]*>\s*([^\s<]+)')) {
    $roleRows[$m.Groups[3].Value.Trim()] = @{ Index = $m.Groups[1].Value; RoleId = $m.Groups[2].Value }
}
Add-Result "Rollenmatrix im Formular" ($roleRows.ContainsKey('User') -and $roleRows.ContainsKey('Administrator')) ($roleRows.Keys -join ', ')

$userIdx = $roleRows['User'].Index
$userRoleId = $roleRows['User'].RoleId
$adminIdx = $roleRows['Administrator'].Index
$adminRoleId = $roleRows['Administrator'].RoleId

function Access-Body([string]$idx, [string]$roleId, [bool]$read, [bool]$download) {
    $b = "Input.Access[$idx].RoleId=$roleId"
    if ($read) { $b += "&Input.Access[$idx].CanRead=true" }
    if ($download) { $b += "&Input.Access[$idx].CanDownload=true" }
    return $b
}

# Ungueltige Pfade
$body = "__RequestVerificationToken=$(Enc $t)&Input.Name=Fehler&Input.StorageType=Local&Input.BasePath=$(Enc 'relativ/pfad')&Input.SortOrder=0&Input.IsEnabled=true"
$r = Post-Form "$BaseUrl/Admin/Roots/Edit" $admin $body
Add-Result "Relativer Pfad abgelehnt" ($r.Content -match 'absolute path') ""

$t = Get-Token $r.Content
$body = "__RequestVerificationToken=$(Enc $t)&Input.Name=Fehler&Input.StorageType=Local&Input.BasePath=$(Enc '/gibts/nicht')&Input.SortOrder=0&Input.IsEnabled=true"
$r = Post-Form "$BaseUrl/Admin/Roots/Edit" $admin $body
Add-Result "Fehlender Pfad abgelehnt" ($r.Content -match 'does not exist in the container') ""

$t = Get-Token $r.Content
$body = "__RequestVerificationToken=$(Enc $t)&Input.Name=Datenordner&Input.StorageType=Local&Input.BasePath=$(Enc '/data')&Input.SortOrder=0&Input.IsEnabled=true"
$r = Post-Form "$BaseUrl/Admin/Roots/Edit" $admin $body
Add-Result "Datenverzeichnis geschuetzt" ($r.Content -match 'data directory cannot be shared') ""

# Gueltigen Root anlegen, Rolle User darf lesen und herunterladen
$t = Get-Token $r.Content
$body = "__RequestVerificationToken=$(Enc $t)&Input.Name=Sample&Input.StorageType=Local&Input.BasePath=$(Enc '/storage/sample')&Input.DisplayIcon=folder&Input.SortOrder=1&Input.IsEnabled=true"
$body += "&" + (Access-Body $userIdx $userRoleId $true $true)
$body += "&" + (Access-Body $adminIdx $adminRoleId $false $false)
$r = Post-Form "$BaseUrl/Admin/Roots/Edit" $admin $body
Add-Result "Root angelegt" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/Admin/Roots' -and $r.Content -match 'Sample') $r.BaseResponse.ResponseUri.AbsolutePath
Add-Result "Root ist verfuegbar" ($r.Content -match 'Available') ""

# Doppelter Name
$r2 = Invoke-WebRequest "$BaseUrl/Admin/Roots/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r2.Content
$body = "__RequestVerificationToken=$(Enc $t)&Input.Name=Sample&Input.StorageType=Local&Input.BasePath=$(Enc '/storage/sample')&Input.SortOrder=0&Input.IsEnabled=true"
$r2 = Post-Form "$BaseUrl/Admin/Roots/Edit" $admin $body
Add-Result "Doppelter Rootname abgelehnt" ($r2.Content -match 'already exists') ""

# Zweiter Root nur fuer Administratoren
$r2 = Invoke-WebRequest "$BaseUrl/Admin/Roots/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r2.Content
$body = "__RequestVerificationToken=$(Enc $t)&Input.Name=Intern&Input.StorageType=Local&Input.BasePath=$(Enc '/storage/internal')&Input.DisplayIcon=hard-drive&Input.SortOrder=2&Input.IsEnabled=true"
$r2 = Post-Form "$BaseUrl/Admin/Roots/Edit" $admin $body
Add-Result "Zweiter Root angelegt" ($r2.Content -match 'Intern') ""

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
$r = Invoke-WebRequest "$BaseUrl/Admin/Users/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$roleIds = @{}
foreach ($m in [regex]::Matches($r.Content, '(?s)name="Input\.RoleIds" value="(\d+)".*?<span>([^<]+)</span>')) {
    $roleIds[$m.Groups[2].Value.Trim()] = $m.Groups[1].Value
}
$body = "__RequestVerificationToken=$(Enc $t)&Input.Username=tester&Input.DisplayName=Tester&Input.NewPassword=$(Enc $TesterPassword)&Input.IsActive=true&Input.MustChangePassword=false&Input.RoleIds=$($roleIds['User'])"
$r = Post-Form "$BaseUrl/Admin/Users/Edit" $admin $body
Add-Result "Testbenutzer angelegt" ($r.Content -match '>tester<') ""

$tester = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$r = Invoke-WebRequest "$BaseUrl/Login" -WebSession $tester -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Login" $tester "__RequestVerificationToken=$(Enc $t)&Input.Username=tester&Input.Password=$(Enc $TesterPassword)"
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

# Abgemeldet kein API-Zugriff
$anon = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$anonApi = Invoke-WebRequest "$BaseUrl/api/fs/roots" -WebSession $anon -UseBasicParsing
Add-Result "API ohne Anmeldung gesperrt" ($anonApi.BaseResponse.ResponseUri.AbsolutePath -eq '/Login') $anonApi.BaseResponse.ResponseUri.AbsolutePath

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
