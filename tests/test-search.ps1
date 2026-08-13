param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$TesterPassword = "Tester-2026-abc",
    [string]$Container = "matfile-dev"
)

# Eigener Ordner im Container, damit sample-data (Bind-Mount) sauber bleibt.
$WorkDir = "/storage/suchtest"

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\_helpers.ps1"
$results = @()

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Test = $name; OK = $ok; Detail = $detail }
    $mark = if ($ok) { "OK  " } else { "FAIL" }
    Write-Host "$mark  $name  $detail"
}

# Startet eine Suche und wartet, bis sie fertig ist.
function Search-Wait($session, [long]$rootId, [string]$query, [bool]$content, [bool]$hidden, [string]$path = '') {
    $start = Send-MatJson 'Post' "$BaseUrl/api/search" $session @{
        rootId = $rootId; path = $path; query = $query; content = $content; includeHidden = $hidden
    }

    if ($start.Status -ne 200) { return @{ Start = $start; Snapshot = $null } }

    $snapshot = $null
    for ($i = 0; $i -lt 60; $i++) {
        $snapshot = (Get-MatJson "$BaseUrl/api/search/$($start.Data.searchId)?from=0" $session).Data
        if ($snapshot.state -ne 'running') { break }
        Start-Sleep -Milliseconds 200
    }

    return @{ Start = $start; Snapshot = $snapshot; Id = $start.Data.searchId }
}

function Paths($snapshot) {
    return @($snapshot.hits | ForEach-Object { $_.path })
}

# --- Testdaten ------------------------------------------------------------
docker exec $Container sh -c @"
rm -rf $WorkDir && mkdir -p $WorkDir/eins/zwei/drei $WorkDir/.geheim $WorkDir/eins/Zebrastreifen
printf 'Der Suchbegriff Zebra steht mitten in dieser Zeile und danach kommt noch mehr\n' > $WorkDir/eins/zwei/drei/notiz.txt
printf 'nichts besonderes\n' > $WorkDir/eins/liesmich.md
printf 'Zebra' > $WorkDir/zebra-datei.txt
printf 'Zebra im Versteckten\n' > $WorkDir/.geheim/geheim.txt
head -c 4096 /dev/urandom > $WorkDir/eins/binaer.dat
"@ | Out-Null
Add-Result "Testdaten angelegt" ($LASTEXITCODE -eq 0) $WorkDir

# --- Anmeldung und Speicherort -------------------------------------------
$admin = New-MatSession
$r = Connect-Mat $BaseUrl $admin 'admin' $SeedPassword
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

$roles = Get-MatRoles $BaseUrl $admin

# Rolle User darf lesen, aber nicht herunterladen - Inhaltssuche bleibt gesperrt.
$created = New-MatRoot $BaseUrl $admin @{
    name = 'Suche'; path = $WorkDir; sortOrder = 1
    access = @((New-MatAccess $roles['User'] $true $false $false $false $false))
}
$rootId = $created.Data.id
Add-Result "Speicherort angelegt" ($created.Status -eq 200 -and $rootId -gt 0) "id=$rootId"

# --- Suche nach Namen -----------------------------------------------------
$run = Search-Wait $admin $rootId 'notiz' $false $false
$snapshot = $run.Snapshot
Add-Result "Suche startet" ($run.Start.Status -eq 200 -and $run.Start.Data.searchId.Length -gt 0) "limit=$($run.Start.Data.limit)"
Add-Result "Suche laeuft durch" ($snapshot.state -eq 'completed' -and $snapshot.percent -eq 100) "state=$($snapshot.state)"
Add-Result "Fortschritt gezaehlt" ($snapshot.folders -ge 5 -and $snapshot.files -ge 4) "ordner=$($snapshot.folders) dateien=$($snapshot.files)"
Add-Result "Treffer im Unterordner" (
    $snapshot.total -eq 1 -and (Paths $snapshot) -contains 'eins/zwei/drei/notiz.txt') (Paths $snapshot) -join ', '
Add-Result "Elternpfad am Treffer" ($snapshot.hits[0].parentPath -eq 'eins/zwei/drei') "parent=$($snapshot.hits[0].parentPath)"
Add-Result "Ohne Inhaltssuche keine Fundstelle" ($null -eq $snapshot.hits[0].snippet) ""

# Ordner werden auch nach Namen gefunden
$run = Search-Wait $admin $rootId 'zebra' $false $false
$paths = Paths $run.Snapshot
Add-Result "Ordner und Datei nach Namen" (
    $paths -contains 'zebra-datei.txt' -and $paths -contains 'eins/Zebrastreifen') ($paths -join ', ')
$folderHit = $run.Snapshot.hits | Where-Object { $_.path -eq 'eins/Zebrastreifen' }
Add-Result "Ordner als Ordner gemeldet" ($folderHit.isDirectory -eq $true) ""

# Gross- und Kleinschreibung spielt keine Rolle
$run = Search-Wait $admin $rootId 'NOTIZ' $false $false
Add-Result "Schreibweise egal" ((Paths $run.Snapshot) -contains 'eins/zwei/drei/notiz.txt') "treffer=$($run.Snapshot.total)"

# Ab einem Unterordner suchen
$run = Search-Wait $admin $rootId 'zebra' $false $false 'eins'
$paths = Paths $run.Snapshot
Add-Result "Suche ab Unterordner" (
    $paths -contains 'eins/Zebrastreifen' -and $paths -notcontains 'zebra-datei.txt') ($paths -join ', ')

# --- Suche im Dateiinhalt ------------------------------------------------
$run = Search-Wait $admin $rootId 'Zebra' $true $false
$snapshot = $run.Snapshot
$content = $snapshot.hits | Where-Object { $_.path -eq 'eins/zwei/drei/notiz.txt' }
Add-Result "Inhaltstreffer gefunden" ($null -ne $content) "treffer=$($snapshot.total)"
Add-Result "Fundstelle als Zeile" ($content.snippet -match '^Der Suchbegriff Zebra steht') "snippet=$($content.snippet)"
Add-Result "Binaerdatei uebersprungen" ((Paths $snapshot) -notcontains 'eins/binaer.dat') ""

$run = Search-Wait $admin $rootId 'besonderes' $true $false
Add-Result "Nur im Inhalt vorhanden" ((Paths $run.Snapshot) -contains 'eins/liesmich.md') "treffer=$($run.Snapshot.total)"

$run = Search-Wait $admin $rootId 'gibtesnicht' $true $false
Add-Result "Ohne Treffer sauber beendet" (
    $run.Snapshot.state -eq 'completed' -and $run.Snapshot.total -eq 0) "treffer=$($run.Snapshot.total)"

# --- Versteckte Dateien --------------------------------------------------
$run = Search-Wait $admin $rootId 'geheim' $false $false
Add-Result "Versteckter Ordner uebersprungen" ((Paths $run.Snapshot).Count -eq 0) "treffer=$($run.Snapshot.total)"

$run = Search-Wait $admin $rootId 'geheim' $false $true
Add-Result "Versteckter Ordner auf Wunsch dabei" (
    (Paths $run.Snapshot) -contains '.geheim/geheim.txt') ((Paths $run.Snapshot) -join ', ')

# --- Abbrechen und Nachladen --------------------------------------------
$start = Send-MatJson 'Post' "$BaseUrl/api/search" $admin @{
    rootId = $rootId; path = ''; query = 'zebra'; content = $true; includeHidden = $true
}
$cancelled = Send-MatJson 'Post' "$BaseUrl/api/search/$($start.Data.searchId)/cancel" $admin $null
Start-Sleep -Milliseconds 500
$after = (Get-MatJson "$BaseUrl/api/search/$($start.Data.searchId)?from=0" $admin).Data
Add-Result "Suche abbrechbar" (
    $cancelled.Status -eq 204 -and ($after.state -eq 'cancelled' -or $after.state -eq 'completed')) "state=$($after.state)"

$run = Search-Wait $admin $rootId 'zebra' $false $false
$rest = (Get-MatJson "$BaseUrl/api/search/$($run.Id)?from=1" $admin).Data
Add-Result "Treffer stueckweise abrufbar" (
    $rest.total -eq $run.Snapshot.total -and @($rest.hits).Count -eq ($run.Snapshot.total - 1)) "nachgeladen=$(@($rest.hits).Count)"

# --- Fehlerfaelle ---------------------------------------------------------
$short = Send-MatJson 'Post' "$BaseUrl/api/search" $admin @{ rootId = $rootId; query = 'a'; content = $false }
Add-Result "Zu kurze Eingabe abgelehnt" (
    $short.Status -eq 400 -and $short.Data.error -eq 'query_too_short') "status=$($short.Status)"

$unknownRoot = Send-MatJson 'Post' "$BaseUrl/api/search" $admin @{ rootId = 999999; query = 'zebra'; content = $false }
Add-Result "Unbekannter Speicherort" ($unknownRoot.Status -eq 404) "status=$($unknownRoot.Status)"

$unknownId = Get-MatJson "$BaseUrl/api/search/gibtsnicht" $admin
Add-Result "Unbekannte Suche" ($unknownId.Status -eq 404) "status=$($unknownId.Status)"

# --- Rechte ---------------------------------------------------------------
New-MatUser $BaseUrl $admin @{
    username = 'tester'; displayName = 'Tester'; newPassword = $TesterPassword; roleIds = @($roles['User'])
} | Out-Null

$tester = New-MatSession
Connect-Mat $BaseUrl $tester 'tester' $TesterPassword | Out-Null

$byName = Search-Wait $tester $rootId 'notiz' $false $false
Add-Result "Namenssuche mit Leserecht erlaubt" (
    $byName.Start.Status -eq 200 -and $byName.Snapshot.state -eq 'completed') "treffer=$($byName.Snapshot.total)"

$byContent = Send-MatJson 'Post' "$BaseUrl/api/search" $tester @{ rootId = $rootId; query = 'Zebra'; content = $true }
Add-Result "Inhaltssuche ohne Downloadrecht gesperrt" ($byContent.Status -eq 403) "status=$($byContent.Status)"

$foreign = Get-MatJson "$BaseUrl/api/search/$($run.Id)" $tester
Add-Result "Fremde Suche nicht lesbar" ($foreign.Status -eq 404) "status=$($foreign.Status)"

$anon = New-MatSession
$anonSearch = Send-MatJson 'Post' "$BaseUrl/api/search" $anon @{ rootId = $rootId; query = 'zebra'; content = $false }
Add-Result "Ohne Anmeldung gesperrt" ($anonSearch.Status -eq 401) "status=$($anonSearch.Status)"

# --- Oberflaeche ----------------------------------------------------------
$page = Invoke-WebRequest "$BaseUrl/" -WebSession $admin -UseBasicParsing
Add-Result "Suchknopf in der Leiste" ($page.Content -match 'id="mfx-search-go"') ""
Add-Result "Suchmodul geladen" ((Invoke-WebRequest "$BaseUrl/js/explorer/search.js" -UseBasicParsing).StatusCode -eq 200) ""

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
