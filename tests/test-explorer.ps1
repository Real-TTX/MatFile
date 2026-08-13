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

function Api([string]$method, [string]$url, $session, $body) {
    try {
        $args = @{ Uri = $url; Method = $method; WebSession = $session; UseBasicParsing = $true }
        if ($null -ne $body) {
            $args.Body = ($body | ConvertTo-Json -Depth 6 -Compress)
            $args.ContentType = 'application/json'
        }
        $r = Invoke-WebRequest @args
        $data = $null
        if ($r.Content) { try { $data = $r.Content | ConvertFrom-Json } catch { } }
        return @{ Status = [int]$r.StatusCode; Data = $data; Raw = $r }
    } catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        return @{ Status = $status; Data = $null; Raw = $null }
    }
}

# --- Vorbereitung: Anmeldung und ein Root ---------------------------------
$admin = New-MatSession
$r = Connect-Mat $BaseUrl $admin 'admin' $SeedPassword
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

$roles = Get-MatRoles $BaseUrl $admin
Add-Result "Rollenmatrix aus der API" ($roles.ContainsKey('User') -and $roles.ContainsKey('Administrator')) ($roles.Keys -join ', ')

# Rolle User darf nur lesen - bewusst ohne Download.
$created = New-MatRoot $BaseUrl $admin @{
    name = 'Sample'; path = '/storage/sample'; icon = 'folder'; sortOrder = 1
    access = @((New-MatAccess $roles['User'] $true $false $false $false $false))
}
Add-Result "Root angelegt" ($created.Status -eq 200) "id=$($created.Data.id)"

$roots = (Api GET "$BaseUrl/api/fs/roots" $admin).Data
$rootId = ($roots | Where-Object { $_.name -eq 'Sample' }).id

# --- Explorer-Seite -------------------------------------------------------
$page = Invoke-WebRequest "$BaseUrl/" -WebSession $admin -UseBasicParsing
Add-Result "Explorer ist die Startseite" ($page.BaseResponse.ResponseUri.AbsolutePath -eq '/') $page.BaseResponse.ResponseUri.AbsolutePath
Add-Result "Explorer-Grundgeruest" (
    $page.Content -match 'id="mfx-roots"' -and $page.Content -match 'id="mfx-tabs"' -and
    $page.Content -match 'id="mfx-listing"' -and $page.Content -match 'js/explorer/explorer' -and
    $page.Content -match 'css/explorer') ""

# Die Module werden auch unter ihrem unversionierten Pfad ausgeliefert
$module = Invoke-WebRequest "$BaseUrl/js/explorer/api.js" -WebSession $admin -UseBasicParsing
Add-Result "ES-Module erreichbar" ([int]$module.StatusCode -eq 200 -and $module.Content -match 'export const api') ""
Add-Result "Uebersetzungen eingebettet" ($page.Content -match 'window\.matfileText' -and $page.Content -match 'Favorites') ""

# --- Einstellungen --------------------------------------------------------
$s = Api GET "$BaseUrl/api/settings" $admin
Add-Result "Standardeinstellungen" ($s.Status -eq 200 -and $s.Data.viewMode -eq 'list' -and $s.Data.sortKey -eq 'name') ""

$put = Api PUT "$BaseUrl/api/settings" $admin @{ viewMode = 'grid'; sortKey = 'size'; sortDir = 'desc'; showHidden = $true; sidebarWidth = 300 }
$s = Api GET "$BaseUrl/api/settings" $admin
Add-Result "Einstellungen gespeichert" ($put.Status -eq 204 -and $s.Data.viewMode -eq 'grid' -and $s.Data.sortKey -eq 'size' -and $s.Data.showHidden -eq $true) ""

$put = Api PUT "$BaseUrl/api/settings" $admin @{ viewMode = 'unsinn'; sortKey = 'unsinn'; sortDir = 'unsinn'; sidebarWidth = 9999 }
$s = Api GET "$BaseUrl/api/settings" $admin
Add-Result "Ungueltige Werte normalisiert" ($s.Data.viewMode -eq 'list' -and $s.Data.sortKey -eq 'name' -and $s.Data.sidebarWidth -eq 520) "width=$($s.Data.sidebarWidth)"

# --- Favoriten ------------------------------------------------------------
$b = Api GET "$BaseUrl/api/bookmarks" $admin
Add-Result "Favoriten anfangs leer" ($b.Status -eq 200 -and ($null -eq $b.Data -or $b.Data.Count -eq 0)) ""

$add = Api POST "$BaseUrl/api/bookmarks" $admin @{ rootId = $rootId; path = 'Dokumente'; name = 'Docs' }
Add-Result "Favorit angelegt" ($add.Status -eq 200 -and $add.Data.name -eq 'Docs' -and $add.Data.path -eq 'Dokumente') ""

$again = Api POST "$BaseUrl/api/bookmarks" $admin @{ rootId = $rootId; path = 'Dokumente'; name = 'Docs' }
Add-Result "Favorit nicht doppelt" ($again.Data.id -eq $add.Data.id) ""

$b = Api GET "$BaseUrl/api/bookmarks" $admin
Add-Result "Favorit in der Liste" (@($b.Data).Count -eq 1) ""

$bad = Api POST "$BaseUrl/api/bookmarks" $admin @{ rootId = 999999; path = ''; name = 'X' }
Add-Result "Favorit auf fremdem Root abgelehnt" ($bad.Status -eq 404) "status=$($bad.Status)"

$del = Api DELETE "$BaseUrl/api/bookmarks/$($add.Data.id)" $admin
$b = Api GET "$BaseUrl/api/bookmarks" $admin
Add-Result "Favorit geloescht" ($del.Status -eq 204 -and @($b.Data).Count -eq 0) ""

# --- Angeheftete Tabs -----------------------------------------------------
$tabs = Api GET "$BaseUrl/api/tabs" $admin
Add-Result "Tabs anfangs leer" (@($tabs.Data).Count -eq 0) ""

$put = Api PUT "$BaseUrl/api/tabs" $admin @(
    @{ rootId = $rootId; path = 'Dokumente'; title = 'Dokumente'; sortOrder = 0; isActive = $true },
    @{ rootId = 999999; path = ''; title = 'Fremd'; sortOrder = 1; isActive = $false }
)
$tabs = Api GET "$BaseUrl/api/tabs" $admin
Add-Result "Tabs gespeichert, fremder Root gefiltert" ($put.Status -eq 204 -and @($tabs.Data).Count -eq 1 -and $tabs.Data[0].path -eq 'Dokumente') "n=$(@($tabs.Data).Count)"

# --- Download -------------------------------------------------------------
$file = Invoke-WebRequest "$BaseUrl/api/fs/download?rootId=$rootId&path=liesmich.txt" -WebSession $admin -UseBasicParsing
Add-Result "Datei heruntergeladen" ([int]$file.StatusCode -eq 200 -and $file.Content -match 'Beispieldatei') ""
Add-Result "Als Anhang ausgeliefert" ($file.Headers['Content-Disposition'] -match 'attachment' -and $file.Headers['Content-Disposition'] -match 'liesmich.txt') ""

# Range laesst sich in PowerShell 5.1 nur ueber HttpClient setzen
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.CookieContainer = $admin.Cookies
$client = New-Object System.Net.Http.HttpClient($handler)
$request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$BaseUrl/api/fs/download?rootId=$rootId&path=liesmich.txt")
$request.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue(0, 9)
$response = $client.SendAsync($request).Result
$rangeStatus = [int]$response.StatusCode
$rangeBody = $response.Content.ReadAsStringAsync().Result
$client.Dispose()
Add-Result "Range-Anfrage unterstuetzt" ($rangeStatus -eq 206 -and $rangeBody.Length -eq 10) "status=$rangeStatus len=$($rangeBody.Length)"

$dir = Api GET "$BaseUrl/api/fs/download?rootId=$rootId&path=Dokumente" $admin
Add-Result "Ordner nicht herunterladbar" ($dir.Status -eq 404) "status=$($dir.Status)"

$escape = Api GET "$BaseUrl/api/fs/download?rootId=$rootId&path=$(Enc '../../etc/passwd')" $admin
Add-Result "Pfadausbruch beim Download blockiert" ($escape.Status -eq 400 -or $escape.Status -eq 404) "status=$($escape.Status)"

# --- Rechte: Benutzer ohne Downloadrecht ---------------------------------
New-MatUser $BaseUrl $admin @{
    username = 'tester'; displayName = 'Tester'; newPassword = $TesterPassword; roleIds = @($roles['User'])
} | Out-Null

$tester = New-MatSession
$r = Connect-Mat $BaseUrl $tester 'tester' $TesterPassword
Add-Result "Testbenutzer angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

$list = Api GET "$BaseUrl/api/fs/list?rootId=$rootId" $tester
Add-Result "Testbenutzer darf lesen" ($list.Status -eq 200) "status=$($list.Status)"

$deny = Api GET "$BaseUrl/api/fs/download?rootId=$rootId&path=liesmich.txt" $tester
Add-Result "Download ohne Recht gesperrt" ($deny.Status -eq 403) "status=$($deny.Status)"

# Favoriten und Einstellungen sind je Benutzer getrennt
$b = Api GET "$BaseUrl/api/bookmarks" $tester
$s = Api GET "$BaseUrl/api/settings" $tester
Add-Result "Zustand je Benutzer getrennt" (@($b.Data).Count -eq 0 -and $s.Data.viewMode -eq 'list') ""

# --- Speicherorte aus dem Explorer heraus verwalten ----------------------
$created = Api POST "$BaseUrl/api/roots" $admin @{
    name = 'Direkt'; type = 'Local'; path = '/storage/sample'
    port = 0; useTls = $false; passive = $true; isEnabled = $true; sortOrder = 7
}
Add-Result "Speicherort ueber API angelegt" ($created.Status -eq 200 -and $created.Data.id -gt 0) "id=$($created.Data.id)"

$newId = $created.Data.id
$detail = Api GET "$BaseUrl/api/roots/$newId" $admin
Add-Result "Speicherort abrufbar" ($detail.Status -eq 200 -and $detail.Data.name -eq 'Direkt' -and $detail.Data.hasPassword -eq $false) ""

$renamed = Api PUT "$BaseUrl/api/roots/$newId" $admin @{
    name = 'Direkt umbenannt'; type = 'Local'; path = '/storage/sample'
    port = 0; useTls = $false; passive = $true; isEnabled = $true; sortOrder = 7
}
$rootNames = (Api GET "$BaseUrl/api/fs/roots" $admin).Data | ForEach-Object { $_.name }
Add-Result "Speicherort umbenannt" ($renamed.Status -eq 200 -and $rootNames -contains 'Direkt umbenannt') ($rootNames -join ', ')

$badTest = Api POST "$BaseUrl/api/roots/test" $admin @{
    name = 'Probe'; type = 'Sftp'; path = '/'; host = 'gibtsnicht'
    port = 22; username = 'x'; password = 'y'; useTls = $false; passive = $true; isEnabled = $true; sortOrder = 0
}
Add-Result "Verbindungstest meldet Fehler" ($badTest.Status -eq 200 -and $badTest.Data.ok -eq $false) "ok=$($badTest.Data.ok)"

$removed = Api DELETE "$BaseUrl/api/roots/$newId" $admin
$rootNames = (Api GET "$BaseUrl/api/fs/roots" $admin).Data | ForEach-Object { $_.name }
Add-Result "Speicherort entfernt" ($removed.Status -eq 204 -and $rootNames -notcontains 'Direkt umbenannt') ($rootNames -join ', ')

$forbidden = Api POST "$BaseUrl/api/roots" $tester @{
    name = 'Verboten'; type = 'Local'; path = '/storage/sample'
    port = 0; useTls = $false; passive = $true; isEnabled = $true; sortOrder = 0
}
Add-Result "Ohne Recht kein Speicherort" ($forbidden.Status -eq 403) "status=$($forbidden.Status)"

$page = Invoke-WebRequest "$BaseUrl/" -WebSession $admin -UseBasicParsing
Add-Result "Verwaltungsknopf nur mit Recht" ($page.Content -match 'id="mfx-more"' -and $page.Content -match 'manageRoots: true') ""

$testerPage = Invoke-WebRequest "$BaseUrl/" -WebSession $tester -UseBasicParsing
Add-Result "Ohne Recht kein Verwaltungsknopf" ($testerPage.Content -notmatch 'id="mfx-more"' -and $testerPage.Content -match 'manageRoots: false') ""

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
