param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$AdminPassword = "admin",
    [string]$SftpPassword = "Sftp-Geheim-2026",
    [string]$FtpPassword = "Ftp-Geheim-2026",
    [string]$SmbPassword = "Smb-Geheim-2026",
    [string]$Container = "matfile-dev",
    [string]$Compose = "$PSScriptRoot\..\docker-compose.dev.yml",
    # Namen der Gegenstellen-Container; im Teststack lauten sie matfile-test-*
    [string]$RemotePrefix = "matfile"
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\_helpers.ps1"
$results = @()

function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Test = $name; OK = $ok; Detail = $detail }
    $mark = if ($ok) { "OK  " } else { "FAIL" }
    Write-Host "$mark  $name  $detail"
}

# Kurzform fuer die JSON-Aufrufe aus _helpers.ps1 - liefert auch im Fehlerfall den Koerper.
function Api([string]$method, [string]$url, $session, $body) {
    return Send-MatJson $method $url $session $body
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

# Docker schreibt Fortschritt auf stderr - das darf den Test nicht abbrechen.
function Invoke-Docker {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & docker @args 2>&1 } finally { $ErrorActionPreference = $previous }
}

function Save-Root($session, [hashtable]$values) {
    return New-MatRoot $BaseUrl $session $values
}

# Verbindungstest ohne zu speichern - liefert @{ ok; error }.
function Test-Root($session, [hashtable]$values) {
    $payload = @{
        name = ''; type = 'Local'; path = ''; host = ''; port = 0; share = $null; url = $null
        username = $null; password = $null; useTls = $false; passive = $true
        isEnabled = $true; icon = $null; sortOrder = 0
    }

    foreach ($key in $values.Keys) { $payload[$key] = $values[$key] }

    return Send-MatJson 'Post' "$BaseUrl/api/roots/test" $session $payload
}

# --- SFTP-Gegenstelle starten --------------------------------------------
Invoke-Docker compose -f $Compose --profile testing up -d sftp | Out-Null
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    $state = (docker inspect -f '{{.State.Running}}' "$RemotePrefix-sftp" | Out-String).Trim()
    if ($state -eq 'true') { $ready = $true; break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
Add-Result "SFTP-Server laeuft" $ready "running=$ready"

Invoke-Docker exec "$RemotePrefix-sftp" sh -c "mkdir -p /home/matfile/upload && chown matfile /home/matfile/upload && echo 'Hallo von der Gegenstelle' > /home/matfile/upload/remote.txt && chown matfile /home/matfile/upload/remote.txt" | Out-Null

# --- Anmeldung ------------------------------------------------------------
$admin = New-MatSession
$r = Connect-Mat $BaseUrl $admin 'admin' $SeedPassword
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

# --- Verbindungstest ------------------------------------------------------
$base = @{
    name = 'Fernspeicher'; type = 'Sftp'; path = '/upload'; host = 'sftp'; port = 22
    username = 'matfile'; sortOrder = 1
}

$wrong = Test-Root $admin ($base + @{ password = 'falsch' })
Add-Result "Test mit falschem Kennwort schlaegt fehl" (
    $wrong.Status -eq 200 -and $wrong.Data.ok -eq $false -and $wrong.Data.error -match 'could not be established') "ok=$($wrong.Data.ok)"

$good = Test-Root $admin ($base + @{ password = $SftpPassword })
Add-Result "Verbindungstest erfolgreich" ($good.Status -eq 200 -and $good.Data.ok -eq $true) "ok=$($good.Data.ok)"

# --- Ordner der Gegenstelle durchsehen, bevor gespeichert wird -----------
$browse = Api POST "$BaseUrl/api/roots/browse?path=" $admin ($base + @{ password = $SftpPassword })
$browseNames = $browse.Data.entries | ForEach-Object { $_.name }
Add-Result "SFTP-Ordner zur Auswahl" (
    $browse.Status -eq 200 -and $browse.Data.mode -eq 'folders' -and $browseNames -contains 'upload') ($browseNames -join ', ')

$browseSub = Api POST "$BaseUrl/api/roots/browse?path=upload" $admin ($base + @{ password = $SftpPassword })
Add-Result "Unterordner beim Durchsehen" (
    $browseSub.Status -eq 200 -and $browseSub.Data.parentPath -eq '') "parent='$($browseSub.Data.parentPath)'"

$browseBad = Api POST "$BaseUrl/api/roots/browse?path=" $admin ($base + @{ password = 'falsch' })
Add-Result "Durchsehen ohne Zugang scheitert" ($browseBad.Status -eq 400) "status=$($browseBad.Status) error=$($browseBad.Data.error)"

# --- Root speichern -------------------------------------------------------
Save-Root $admin ($base + @{ password = $SftpPassword }) | Out-Null
$roots = (Api GET "$BaseUrl/api/fs/roots" $admin).Data
$sftpRoot = ($roots | Where-Object { $_.name -eq 'Fernspeicher' })
Add-Result "SFTP-Root angelegt" ($null -ne $sftpRoot -and $sftpRoot.type -eq 'Sftp') "type=$($sftpRoot.type)"

# Kennwort darf nirgends im Klartext in der Datenbank stehen
$plain = (Invoke-Docker exec $Container sh -c "grep -a -o '$SftpPassword' /data/matfile.db | wc -l" | Out-String).Trim()
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
Invoke-Docker exec $Container sh -c "rm -rf /storage/lokal && mkdir -p /storage/lokal && echo 'lokale Datei' > /storage/lokal/lokal.txt" | Out-Null
Save-Root $admin @{ name = 'Lokal'; type = 'Local'; path = '/storage/lokal'; sortOrder = 2 } | Out-Null
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

$backContent = Invoke-Docker exec $Container sh -c "cat /storage/lokal/remote.txt"
Add-Result "Inhalt korrekt uebertragen" ($backContent -match 'Hallo von der Gegenstelle') ""

# --- Zugriffsrechte ueber SFTP -------------------------------------------
$perm = Api GET "$BaseUrl/api/fs/permissions?rootId=$($sftpRoot.id)&path=$(Enc 'Ordner')" $admin
Add-Result "SFTP-Rechte gelesen" (
    $perm.Status -eq 200 -and $perm.Data.supported -and $perm.Data.canChange -and
    $perm.Data.modeText -match '^\d{3}$') "mode=$($perm.Data.modeText) owner=$($perm.Data.owner)"

$setPerm = Api PUT "$BaseUrl/api/fs/permissions" $admin @{ rootId = $sftpRoot.id; path = 'Ordner'; mode = 448 }
$onDisk = (Invoke-Docker exec "$RemotePrefix-sftp" sh -c "ls -ld /home/matfile/upload/Ordner" | Out-String).Trim()
Add-Result "SFTP-Rechte gesetzt" (
    $setPerm.Status -eq 200 -and $setPerm.Data.modeText -eq '700' -and $onDisk -match '^drwx------') "$onDisk"

$readBack = Api GET "$BaseUrl/api/fs/permissions?rootId=$($sftpRoot.id)&path=$(Enc 'Ordner')" $admin
Add-Result "SFTP-Rechte wieder gelesen" ($readBack.Data.modeText -eq '700') "mode=$($readBack.Data.modeText)"

Api PUT "$BaseUrl/api/fs/permissions" $admin @{ rootId = $sftpRoot.id; path = 'Ordner'; mode = 493 } | Out-Null

$badMode = Api PUT "$BaseUrl/api/fs/permissions" $admin @{ rootId = $sftpRoot.id; path = 'Ordner'; mode = 4096 }
Add-Result "Ungueltiger Rechtewert abgelehnt" (
    $badMode.Status -eq 400 -and $badMode.Data.error -eq 'invalid_mode') "status=$($badMode.Status)"

$missingPerm = Api GET "$BaseUrl/api/fs/permissions?rootId=$($sftpRoot.id)&path=$(Enc 'gibtsnicht')" $admin
Add-Result "Rechte eines fehlenden Eintrags" ($missingPerm.Status -eq 404) "status=$($missingPerm.Status)"

# --- Pfadausbruch ---------------------------------------------------------
$escape = Api GET "$BaseUrl/api/fs/list?rootId=$($sftpRoot.id)&path=$(Enc '../..')" $admin
Add-Result "Pfadausbruch auf der Gegenstelle blockiert" ($escape.Status -eq 400) "status=$($escape.Status)"

# --- Loeschen -------------------------------------------------------------
$del = Api POST "$BaseUrl/api/fs/delete" $admin @{ rootId = $sftpRoot.id; paths = @('Ordner') }
$job = Wait-Job $admin $del.Data.token
Add-Result "Ferner Ordner geloescht" ($job.status -eq 'Completed' -and (Names $admin $sftpRoot.id '') -notcontains 'Ordner') "status=$($job.status)"

# --- WebDAV ---------------------------------------------------------------
Invoke-Docker compose -f $Compose --profile testing up -d webdav | Out-Null
$davReady = $false
for ($i = 0; $i -lt 60; $i++) {
    $state = (docker inspect -f '{{.State.Running}}' "$RemotePrefix-webdav" | Out-String).Trim()
    if ($state -eq 'true') { $davReady = $true; break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 3
Invoke-Docker exec "$RemotePrefix-webdav" sh -c "rm -rf /var/lib/dav/data/* 2>/dev/null; true" | Out-Null
Add-Result "WebDAV-Server laeuft" $davReady "running=$davReady"

$davBase = @{
    name = 'Cloud'; type = 'WebDav'; path = ''; url = 'http://webdav'; username = 'matfile'; sortOrder = 3
}

$davTest = Test-Root $admin ($davBase + @{ password = 'Dav-Geheim-2026' })
Add-Result "WebDAV-Verbindungstest" ($davTest.Status -eq 200 -and $davTest.Data.ok -eq $true) "ok=$($davTest.Data.ok)"

Save-Root $admin ($davBase + @{ password = 'Dav-Geheim-2026' }) | Out-Null
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

# WebDAV kennt keine POSIX-Rechte - der Dialog blendet den Abschnitt dann aus
$davPerm = Api GET "$BaseUrl/api/fs/permissions?rootId=$($davRoot.id)&path=$(Enc 'Ablage')" $admin
Add-Result "WebDAV ohne Rechteverwaltung" (
    $davPerm.Status -eq 200 -and $davPerm.Data.supported -eq $false) "supported=$($davPerm.Data.supported)"

$davSet = Api PUT "$BaseUrl/api/fs/permissions" $admin @{ rootId = $davRoot.id; path = 'Ablage'; mode = 493 }
Add-Result "WebDAV-Rechte nicht setzbar" (
    $davSet.Status -eq 501 -and $davSet.Data.error -eq 'permissions_unsupported') "status=$($davSet.Status)"

$davDel = Api POST "$BaseUrl/api/fs/delete" $admin @{ rootId = $davRoot.id; paths = @('Ablage') }
$job = Wait-Job $admin $davDel.Data.token
Add-Result "WebDAV Ordner geloescht" ($job.status -eq 'Completed' -and (Names $admin $davRoot.id '') -notcontains 'Ablage') "status=$($job.status)"

# --- FTP ------------------------------------------------------------------
Invoke-Docker compose -f $Compose --profile testing up -d ftp | Out-Null
$ftpReady = $false
for ($i = 0; $i -lt 60; $i++) {
    $state = (docker inspect -f '{{.State.Running}}' "$RemotePrefix-ftp" | Out-String).Trim()
    if ($state -eq 'true') { $ftpReady = $true; break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 3
Invoke-Docker exec "$RemotePrefix-ftp" sh -c "rm -rf /ftp/matfile/* 2>/dev/null; echo 'Datei per FTP' > /ftp/matfile/ftp.txt; chown -R matfile /ftp/matfile" | Out-Null
Add-Result "FTP-Server laeuft" $ftpReady "running=$ftpReady"

# Der Server chrootet den Benutzer nicht, deshalb begrenzt der Basispfad den Zugriff.
$ftpBase = @{
    name = 'Austausch'; type = 'Ftp'; path = '/ftp/matfile'; host = 'ftp'; port = 21
    username = 'matfile'; passive = $true; sortOrder = 4
}

$ftpTest = Test-Root $admin ($ftpBase + @{ password = $FtpPassword })
Add-Result "FTP-Verbindungstest" ($ftpTest.Status -eq 200 -and $ftpTest.Data.ok -eq $true) "ok=$($ftpTest.Data.ok)"

Save-Root $admin ($ftpBase + @{ password = $FtpPassword }) | Out-Null
$ftpRoot = ((Api GET "$BaseUrl/api/fs/roots" $admin).Data | Where-Object { $_.name -eq 'Austausch' })
Add-Result "FTP-Root angelegt" ($null -ne $ftpRoot -and $ftpRoot.type -eq 'Ftp') "type=$($ftpRoot.type)"

$ftpNames = Names $admin $ftpRoot.id ''
Add-Result "FTP-Verzeichnis gelesen" ($ftpNames -contains 'ftp.txt') ($ftpNames -join ', ')

$ftpDownload = Invoke-WebRequest "$BaseUrl/api/fs/download?rootId=$($ftpRoot.id)&path=ftp.txt" -WebSession $admin -UseBasicParsing
Add-Result "FTP-Datei heruntergeladen" ($ftpDownload.Content -match 'Datei per FTP') ""

$ftpFolder = Api POST "$BaseUrl/api/fs/folder" $admin @{ rootId = $ftpRoot.id; path = ''; name = 'Eingang' }
Add-Result "FTP-Ordner angelegt" ($ftpFolder.Status -eq 200 -and (Names $admin $ftpRoot.id '') -contains 'Eingang') "status=$($ftpFolder.Status)"

$ftpCopy = Api POST "$BaseUrl/api/fs/transfer" $admin @{
    sourceRootId = $localRoot.id; paths = @('lokal.txt'); targetRootId = $ftpRoot.id
    targetPath = 'Eingang'; move = $false; overwrite = $false
}
$job = Wait-Job $admin $ftpCopy.Data.token
Add-Result "Kopie lokal nach FTP" ($job.status -eq 'Completed' -and (Names $admin $ftpRoot.id 'Eingang') -contains 'lokal.txt') "status=$($job.status)"

$ftpRen = Api POST "$BaseUrl/api/fs/rename" $admin @{ rootId = $ftpRoot.id; path = 'Eingang/lokal.txt'; newName = 'neu.txt' }
Add-Result "FTP-Datei umbenannt" ($ftpRen.Status -eq 200 -and (Names $admin $ftpRoot.id 'Eingang') -contains 'neu.txt') "status=$($ftpRen.Status)"

# Rechte ueber SITE CHMOD - der Server muss sie auch im Listing mitschicken
$ftpPerm = Api GET "$BaseUrl/api/fs/permissions?rootId=$($ftpRoot.id)&path=$(Enc 'Eingang')" $admin
Add-Result "FTP-Rechte gelesen" (
    $ftpPerm.Status -eq 200 -and $ftpPerm.Data.supported -and
    $ftpPerm.Data.modeText -match '^\d{3}$') "mode=$($ftpPerm.Data.modeText) owner=$($ftpPerm.Data.owner)"

$ftpSet = Api PUT "$BaseUrl/api/fs/permissions" $admin @{ rootId = $ftpRoot.id; path = 'Eingang'; mode = 493 }
$ftpOnDisk = (Invoke-Docker exec "$RemotePrefix-ftp" sh -c "ls -ld /ftp/matfile/Eingang" | Out-String).Trim()
Add-Result "FTP-Rechte gesetzt" (
    $ftpSet.Status -eq 200 -and $ftpSet.Data.modeText -eq '755' -and $ftpOnDisk -match '^drwxr-xr-x') "$ftpOnDisk"

$ftpDel = Api POST "$BaseUrl/api/fs/delete" $admin @{ rootId = $ftpRoot.id; paths = @('Eingang') }
$job = Wait-Job $admin $ftpDel.Data.token
Add-Result "FTP-Ordner geloescht" ($job.status -eq 'Completed' -and (Names $admin $ftpRoot.id '') -notcontains 'Eingang') "status=$($job.status)"

$ftpEscape = Api GET "$BaseUrl/api/fs/list?rootId=$($ftpRoot.id)&path=$(Enc '../..')" $admin
Add-Result "Basispfad begrenzt den FTP-Zugriff" ($ftpEscape.Status -eq 400) "status=$($ftpEscape.Status)"

# --- SMB ------------------------------------------------------------------
Invoke-Docker compose -f $Compose --profile testing up -d smb | Out-Null
$smbReady = $false
for ($i = 0; $i -lt 60; $i++) {
    $state = (docker inspect -f '{{.State.Running}}' "$RemotePrefix-smb" | Out-String).Trim()
    if ($state -eq 'true') { $smbReady = $true; break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 5
Invoke-Docker exec "$RemotePrefix-smb" sh -c "mkdir -p /share && rm -rf /share/* 2>/dev/null; echo 'Datei per SMB' > /share/smb.txt; chown -R matfile /share" | Out-Null
Add-Result "SMB-Server laeuft" $smbReady "running=$smbReady"

$smbBase = @{
    name = 'Freigabe'; type = 'Smb'; path = ''; host = 'smb'; port = 445
    share = 'daten'; username = 'matfile'; sortOrder = 5
}

$smbTest = Test-Root $admin ($smbBase + @{ password = $SmbPassword })
Add-Result "SMB-Verbindungstest" ($smbTest.Status -eq 200 -and $smbTest.Data.ok -eq $true) "ok=$($smbTest.Data.ok)"

# Ordnerauswahl im Dialog: ohne Freigabe kommen die Freigaben, mit Freigabe die Ordner
$smbNoShare = $smbBase.Clone()
$smbNoShare.password = $SmbPassword
$smbNoShare.share = ''
$shareList = Api POST "$BaseUrl/api/roots/browse?path=" $admin $smbNoShare
Add-Result "SMB-Freigaben zur Auswahl" (
    $shareList.Status -eq 200 -and $shareList.Data.mode -eq 'shares' -and
    ($shareList.Data.entries | ForEach-Object { $_.name }) -contains 'daten') (($shareList.Data.entries | ForEach-Object { $_.name }) -join ', ')

$smbFolders = Api POST "$BaseUrl/api/roots/browse?path=" $admin ($smbBase + @{ password = $SmbPassword })
Add-Result "SMB-Ordner zur Auswahl" (
    $smbFolders.Status -eq 200 -and $smbFolders.Data.mode -eq 'folders') "n=$(@($smbFolders.Data.entries).Count)"

Save-Root $admin ($smbBase + @{ password = $SmbPassword }) | Out-Null
$smbRoot = ((Api GET "$BaseUrl/api/fs/roots" $admin).Data | Where-Object { $_.name -eq 'Freigabe' })
Add-Result "SMB-Root angelegt" ($null -ne $smbRoot -and $smbRoot.type -eq 'Smb') "type=$($smbRoot.type)"

$smbNames = Names $admin $smbRoot.id ''
Add-Result "SMB-Verzeichnis gelesen" ($smbNames -contains 'smb.txt') ($smbNames -join ', ')

$smbDownload = Invoke-WebRequest "$BaseUrl/api/fs/download?rootId=$($smbRoot.id)&path=smb.txt" -WebSession $admin -UseBasicParsing
Add-Result "SMB-Datei heruntergeladen" ($smbDownload.Content -match 'Datei per SMB') ""

$smbFolder = Api POST "$BaseUrl/api/fs/folder" $admin @{ rootId = $smbRoot.id; path = ''; name = 'Ablage' }
Add-Result "SMB-Ordner angelegt" ($smbFolder.Status -eq 200 -and (Names $admin $smbRoot.id '') -contains 'Ablage') "status=$($smbFolder.Status)"

$smbCopy = Api POST "$BaseUrl/api/fs/transfer" $admin @{
    sourceRootId = $localRoot.id; paths = @('lokal.txt'); targetRootId = $smbRoot.id
    targetPath = 'Ablage'; move = $false; overwrite = $false
}
$job = Wait-Job $admin $smbCopy.Data.token
Add-Result "Kopie lokal nach SMB" ($job.status -eq 'Completed' -and (Names $admin $smbRoot.id 'Ablage') -contains 'lokal.txt') "status=$($job.status)"

$smbBack = Invoke-WebRequest "$BaseUrl/api/fs/download?rootId=$($smbRoot.id)&path=$(Enc 'Ablage/lokal.txt')" -WebSession $admin -UseBasicParsing
Add-Result "Inhalt auf SMB korrekt" ($smbBack.Content -match 'lokale Datei') ""

$smbRen = Api POST "$BaseUrl/api/fs/rename" $admin @{ rootId = $smbRoot.id; path = 'Ablage/lokal.txt'; newName = 'neu.txt' }
Add-Result "SMB-Datei umbenannt" ($smbRen.Status -eq 200 -and (Names $admin $smbRoot.id 'Ablage') -contains 'neu.txt') "status=$($smbRen.Status)"

$smbPerm = Api GET "$BaseUrl/api/fs/permissions?rootId=$($smbRoot.id)&path=$(Enc 'Ablage')" $admin
Add-Result "SMB ohne Rechteverwaltung" (
    $smbPerm.Status -eq 200 -and $smbPerm.Data.supported -eq $false) "supported=$($smbPerm.Data.supported)"

$smbDel = Api POST "$BaseUrl/api/fs/delete" $admin @{ rootId = $smbRoot.id; paths = @('Ablage') }
$job = Wait-Job $admin $smbDel.Data.token
Add-Result "SMB-Ordner geloescht" ($job.status -eq 'Completed' -and (Names $admin $smbRoot.id '') -notcontains 'Ablage') "status=$($job.status)"

$smbEscape = Api GET "$BaseUrl/api/fs/list?rootId=$($smbRoot.id)&path=$(Enc '../..')" $admin
Add-Result "Pfadausbruch auf SMB blockiert" ($smbEscape.Status -eq 400) "status=$($smbEscape.Status)"

# --- Erreichbarkeit fuer die Seitenleiste --------------------------------
$status = Api GET "$BaseUrl/api/fs/status?rootId=$($ftpRoot.id)" $admin
Add-Result "Statusabfrage erreichbar" ($status.Status -eq 200 -and $status.Data.available -eq $true) "available=$($status.Data.available)"

Save-Root $admin @{
    name = 'Kaputt'; type = 'Sftp'; path = '/upload'; host = 'gibtsnicht'; port = 22
    username = 'x'; password = 'y'; sortOrder = 9
} | Out-Null
$brokenRoot = ((Api GET "$BaseUrl/api/fs/roots" $admin).Data | Where-Object { $_.name -eq 'Kaputt' })
$brokenStatus = Api GET "$BaseUrl/api/fs/status?rootId=$($brokenRoot.id)" $admin
Add-Result "Statusabfrage meldet Ausfall" ($brokenStatus.Status -eq 200 -and $brokenStatus.Data.available -eq $false) "available=$($brokenStatus.Data.available)"

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
