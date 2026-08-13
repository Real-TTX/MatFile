param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$AdminPassword = "admin",
    [string]$SftpPassword = "Sftp-Geheim-2026",
    [string]$Container = "matfile-dev",
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

function Enc([string]$s) { return [System.Uri]::EscapeDataString($s) }

function Post-Form([string]$url, $session, [string]$body) {
    return Invoke-WebRequest $url -Method Post -WebSession $session -UseBasicParsing `
        -Body $body -ContentType 'application/x-www-form-urlencoded'
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

function Wait-Job($session, [string]$token, [int]$timeoutSeconds = 40) {
    for ($i = 0; $i -lt $timeoutSeconds * 4; $i++) {
        $jobs = (Api GET "$BaseUrl/api/jobs" $session).Data
        $job = $jobs | Where-Object { $_.token -eq $token }
        if ($job -and $job.status -ne 'Pending' -and $job.status -ne 'Running') { return $job }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Names($session, $rootId, $path) {
    $list = (Api GET "$BaseUrl/api/fs/list?rootId=$rootId&path=$(Enc $path)" $session).Data
    return @($list.entries | ForEach-Object { $_.name })
}

function Save-Root($session, [string]$body) {
    $page = Invoke-WebRequest "$BaseUrl/Admin/Roots/Edit" -WebSession $session -UseBasicParsing
    $token = Get-Token $page.Content
    return Post-Form "$BaseUrl/Admin/Roots/Edit" $session "__RequestVerificationToken=$(Enc $token)&$body"
}

# --- SFTP-Gegenstelle starten --------------------------------------------
docker compose -f $Compose --profile testing up -d sftp | Out-Null
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    $state = (docker inspect -f '{{.State.Running}}' matfile-sftp | Out-String).Trim()
    if ($state -eq 'true') { $ready = $true; break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
Add-Result "SFTP-Server laeuft" $ready "running=$ready"

docker exec matfile-sftp sh -c "mkdir -p /home/matfile/upload && chown matfile /home/matfile/upload && echo 'Hallo von der Gegenstelle' > /home/matfile/upload/remote.txt && chown matfile /home/matfile/upload/remote.txt" | Out-Null

# --- Anmeldung ------------------------------------------------------------
$admin = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$r = Invoke-WebRequest "$BaseUrl/Login" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Login" $admin "__RequestVerificationToken=$(Enc $t)&Input.Username=admin&Input.Password=$(Enc $SeedPassword)"
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

# --- Verbindungstest ------------------------------------------------------
$base = "Input.Name=Fernspeicher&Input.StorageType=Sftp&Input.BasePath=$(Enc '/upload')&Input.Host=sftp&Input.Port=22&Input.Username=matfile&Input.SortOrder=1&Input.IsEnabled=true"

$page = Invoke-WebRequest "$BaseUrl/Admin/Roots/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $page.Content
$wrong = Post-Form "$BaseUrl/Admin/Roots/Edit?handler=Test" $admin "__RequestVerificationToken=$(Enc $t)&$base&Input.Password=falsch"
Add-Result "Test mit falschem Kennwort schlaegt fehl" ($wrong.Content -match 'could not be established') ""

$page = Invoke-WebRequest "$BaseUrl/Admin/Roots/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $page.Content
$good = Post-Form "$BaseUrl/Admin/Roots/Edit?handler=Test" $admin "__RequestVerificationToken=$(Enc $t)&$base&Input.Password=$(Enc $SftpPassword)"
Add-Result "Verbindungstest erfolgreich" ($good.Content -match 'Connection established') ""

# --- Root speichern -------------------------------------------------------
Save-Root $admin "$base&Input.Password=$(Enc $SftpPassword)" | Out-Null
$roots = (Api GET "$BaseUrl/api/fs/roots" $admin).Data
$sftpRoot = ($roots | Where-Object { $_.name -eq 'Fernspeicher' })
Add-Result "SFTP-Root angelegt" ($null -ne $sftpRoot -and $sftpRoot.type -eq 'Sftp') "type=$($sftpRoot.type)"

# Kennwort darf nirgends im Klartext in der Datenbank stehen
$plain = (docker exec $Container sh -c "grep -a -o '$SftpPassword' /data/matfile.db | wc -l" | Out-String).Trim()
Add-Result "Kennwort verschluesselt gespeichert" ($plain -eq '0') "Treffer im Klartext=$plain"

# --- Lesen ----------------------------------------------------------------
$names = Names $admin $sftpRoot.id ''
Add-Result "Fernes Verzeichnis gelesen" ($names -contains 'remote.txt') ($names -join ', ')

$content = Invoke-WebRequest "$BaseUrl/api/fs/download?rootId=$($sftpRoot.id)&path=remote.txt" -WebSession $admin -UseBasicParsing
Add-Result "Ferne Datei heruntergeladen" ($content.Content -match 'Hallo von der Gegenstelle') ""

# --- Schreiben ------------------------------------------------------------
$folder = Api POST "$BaseUrl/api/fs/folder" $admin @{ rootId = $sftpRoot.id; path = ''; name = 'Ordner' }
Add-Result "Ferner Ordner angelegt" ($folder.Status -eq 200 -and (Names $admin $sftpRoot.id '') -contains 'Ordner') "status=$($folder.Status)"

$bytes = [System.Text.Encoding]::UTF8.GetBytes('Hochgeladen ueber SFTP')
$begin = Api POST "$BaseUrl/api/fs/upload/begin" $admin @{ rootId = $sftpRoot.id; path = 'Ordner'; name = 'upload.txt'; size = $bytes.Length; overwrite = $false }
Invoke-WebRequest "$BaseUrl/api/fs/upload/chunk?uploadId=$($begin.Data.uploadId)&offset=0" -Method Post -WebSession $admin -UseBasicParsing -Body $bytes -ContentType 'application/octet-stream' | Out-Null
$finish = Api POST "$BaseUrl/api/fs/upload/finish?uploadId=$($begin.Data.uploadId)" $admin
Add-Result "Upload auf die Gegenstelle" ($finish.Status -eq 200 -and (Names $admin $sftpRoot.id 'Ordner') -contains 'upload.txt') "status=$($finish.Status)"

$ren = Api POST "$BaseUrl/api/fs/rename" $admin @{ rootId = $sftpRoot.id; path = 'Ordner/upload.txt'; newName = 'neu.txt' }
Add-Result "Ferne Datei umbenannt" ($ren.Status -eq 200 -and (Names $admin $sftpRoot.id 'Ordner') -contains 'neu.txt') "status=$($ren.Status)"

# --- Kopieren zwischen Speicherorten -------------------------------------
docker exec $Container sh -c "rm -rf /storage/lokal && mkdir -p /storage/lokal && echo 'lokale Datei' > /storage/lokal/lokal.txt" | Out-Null
Save-Root $admin "Input.Name=Lokal&Input.StorageType=Local&Input.BasePath=$(Enc '/storage/lokal')&Input.SortOrder=2&Input.IsEnabled=true" | Out-Null
$localRoot = ((Api GET "$BaseUrl/api/fs/roots" $admin).Data | Where-Object { $_.name -eq 'Lokal' })
Add-Result "Lokaler Root angelegt" ($null -ne $localRoot) "id=$($localRoot.id)"

$copy = Api POST "$BaseUrl/api/fs/transfer" $admin @{
    sourceRootId = $localRoot.id; paths = @('lokal.txt'); targetRootId = $sftpRoot.id
    targetPath = 'Ordner'; move = $false; overwrite = $false
}
$job = Wait-Job $admin $copy.Data.token
Add-Result "Kopie lokal nach SFTP" ($job.status -eq 'Completed' -and (Names $admin $sftpRoot.id 'Ordner') -contains 'lokal.txt') "status=$($job.status)"

$back = Api POST "$BaseUrl/api/fs/transfer" $admin @{
    sourceRootId = $sftpRoot.id; paths = @('remote.txt'); targetRootId = $localRoot.id
    targetPath = ''; move = $false; overwrite = $false
}
$job = Wait-Job $admin $back.Data.token
Add-Result "Kopie SFTP nach lokal" ($job.status -eq 'Completed' -and (Names $admin $localRoot.id '') -contains 'remote.txt') "status=$($job.status)"

$backContent = docker exec $Container sh -c "cat /storage/lokal/remote.txt"
Add-Result "Inhalt korrekt uebertragen" ($backContent -match 'Hallo von der Gegenstelle') ""

# --- Pfadausbruch ---------------------------------------------------------
$escape = Api GET "$BaseUrl/api/fs/list?rootId=$($sftpRoot.id)&path=$(Enc '../..')" $admin
Add-Result "Pfadausbruch auf der Gegenstelle blockiert" ($escape.Status -eq 400) "status=$($escape.Status)"

# --- Loeschen -------------------------------------------------------------
$del = Api POST "$BaseUrl/api/fs/delete" $admin @{ rootId = $sftpRoot.id; paths = @('Ordner') }
$job = Wait-Job $admin $del.Data.token
Add-Result "Ferner Ordner geloescht" ($job.status -eq 'Completed' -and (Names $admin $sftpRoot.id '') -notcontains 'Ordner') "status=$($job.status)"

# --- WebDAV ---------------------------------------------------------------
docker compose -f $Compose --profile testing up -d webdav | Out-Null
$davReady = $false
for ($i = 0; $i -lt 60; $i++) {
    $state = (docker inspect -f '{{.State.Running}}' matfile-webdav | Out-String).Trim()
    if ($state -eq 'true') { $davReady = $true; break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 3
docker exec matfile-webdav sh -c "rm -rf /var/lib/dav/data/* 2>/dev/null; true" | Out-Null
Add-Result "WebDAV-Server laeuft" $davReady "running=$davReady"

$davBase = "Input.Name=Cloud&Input.StorageType=WebDav&Input.BasePath=&Input.Url=$(Enc 'http://webdav')&Input.Username=matfile&Input.SortOrder=3&Input.IsEnabled=true"

$page = Invoke-WebRequest "$BaseUrl/Admin/Roots/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $page.Content
$davTest = Post-Form "$BaseUrl/Admin/Roots/Edit?handler=Test" $admin "__RequestVerificationToken=$(Enc $t)&$davBase&Input.Password=$(Enc 'Dav-Geheim-2026')"
Add-Result "WebDAV-Verbindungstest" ($davTest.Content -match 'Connection established') ""

Save-Root $admin "$davBase&Input.Password=$(Enc 'Dav-Geheim-2026')" | Out-Null
$davRoot = ((Api GET "$BaseUrl/api/fs/roots" $admin).Data | Where-Object { $_.name -eq 'Cloud' })
Add-Result "WebDAV-Root angelegt" ($null -ne $davRoot) "id=$($davRoot.id)"

$davFolder = Api POST "$BaseUrl/api/fs/folder" $admin @{ rootId = $davRoot.id; path = ''; name = 'Ablage' }
Add-Result "WebDAV Ordner angelegt" ($davFolder.Status -eq 200 -and (Names $admin $davRoot.id '') -contains 'Ablage') "status=$($davFolder.Status)"

$davCopy = Api POST "$BaseUrl/api/fs/transfer" $admin @{
    sourceRootId = $localRoot.id; paths = @('lokal.txt'); targetRootId = $davRoot.id
    targetPath = 'Ablage'; move = $false; overwrite = $false
}
$job = Wait-Job $admin $davCopy.Data.token
Add-Result "Kopie lokal nach WebDAV" ($job.status -eq 'Completed' -and (Names $admin $davRoot.id 'Ablage') -contains 'lokal.txt') "status=$($job.status)"

$davDownload = Invoke-WebRequest "$BaseUrl/api/fs/download?rootId=$($davRoot.id)&path=$(Enc 'Ablage/lokal.txt')" -WebSession $admin -UseBasicParsing
Add-Result "WebDAV Datei gelesen" ($davDownload.Content -match 'lokale Datei') ""

$davRen = Api POST "$BaseUrl/api/fs/rename" $admin @{ rootId = $davRoot.id; path = 'Ablage/lokal.txt'; newName = 'umbenannt.txt' }
Add-Result "WebDAV Datei umbenannt" ($davRen.Status -eq 200 -and (Names $admin $davRoot.id 'Ablage') -contains 'umbenannt.txt') "status=$($davRen.Status)"

$davDel = Api POST "$BaseUrl/api/fs/delete" $admin @{ rootId = $davRoot.id; paths = @('Ablage') }
$job = Wait-Job $admin $davDel.Data.token
Add-Result "WebDAV Ordner geloescht" ($job.status -eq 'Completed' -and (Names $admin $davRoot.id '') -notcontains 'Ablage') "status=$($job.status)"

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
