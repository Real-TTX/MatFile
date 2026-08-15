param(
    [string]$BaseUrl = "http://localhost:2222",
    [string]$SeedPassword = "admin",
    [string]$AdminPassword = "admin",
    [string]$TesterPassword = "Tester-2026-abc",
    [string]$Container = "matfile-dev"
)

# Der Test arbeitet in einem containerinternen Ordner, damit sample-data
# (Bind-Mount aus dem Repo) nicht mit Testdateien verschmutzt wird.
$WorkDir = "/storage/optest"

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

function Wait-Job($session, [string]$token, [int]$timeoutSeconds = 30) {
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

# --- Frischen Arbeitsordner im Container anlegen -------------------------
docker exec $Container sh -c "rm -rf $WorkDir && mkdir -p $WorkDir/Dokumente && echo hallo > $WorkDir/liesmich.txt && echo notiz > $WorkDir/Dokumente/notiz.txt" | Out-Null
Add-Result "Arbeitsordner vorbereitet" ($LASTEXITCODE -eq 0) $WorkDir

# --- Anmeldung und Root ---------------------------------------------------
$admin = New-MatSession
$r = Connect-Mat $BaseUrl $admin 'admin' $SeedPassword
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

$roles = Get-MatRoles $BaseUrl $admin

# Rolle User bekommt nur Lesen - fuer die Rechtepruefung weiter unten
New-MatRoot $BaseUrl $admin @{
    name = 'Sample'; path = $WorkDir; sortOrder = 1
    access = @((New-MatAccess $roles['User'] $true $false $false $false $false))
} | Out-Null
$rootId = ((Api GET "$BaseUrl/api/fs/roots" $admin).Data | Where-Object { $_.name -eq 'Sample' }).id
Add-Result "Root angelegt" ($null -ne $rootId) "id=$rootId"

# --- Ordner anlegen, umbenennen ------------------------------------------
$folder = Api POST "$BaseUrl/api/fs/folder" $admin @{ rootId = $rootId; path = ''; name = 'Arbeit' }
Add-Result "Ordner angelegt" ($folder.Status -eq 200 -and $folder.Data.isDirectory -eq $true) ""
Add-Result "Ordner in der Liste" ((Names $admin $rootId '') -contains 'Arbeit') ""

$dup = Api POST "$BaseUrl/api/fs/folder" $admin @{ rootId = $rootId; path = ''; name = 'Arbeit' }
Add-Result "Doppelter Ordner abgelehnt" ($dup.Status -eq 409) "status=$($dup.Status)"

$bad = Api POST "$BaseUrl/api/fs/folder" $admin @{ rootId = $rootId; path = ''; name = '../ausbruch' }
Add-Result "Ungueltiger Ordnername abgelehnt" ($bad.Status -eq 400) "status=$($bad.Status)"

$ren = Api POST "$BaseUrl/api/fs/rename" $admin @{ rootId = $rootId; path = 'Arbeit'; newName = 'Projekte' }
$afterRename = Names $admin $rootId ''
Add-Result "Ordner umbenannt" ($ren.Status -eq 200 -and $afterRename -contains 'Projekte' -and $afterRename -notcontains 'Arbeit') "status=$($ren.Status) $($afterRename -join ',')"

# --- Datei anlegen --------------------------------------------------------
$file = Api POST "$BaseUrl/api/fs/file" $admin @{ rootId = $rootId; path = 'Projekte'; name = 'notizen.json' }
Add-Result "Datei angelegt" (
    $file.Status -eq 200 -and $file.Data.isDirectory -eq $false -and $file.Data.size -eq 0) "size=$($file.Data.size)"
Add-Result "Datei in der Liste" ((Names $admin $rootId 'Projekte') -contains 'notizen.json') ""

$fileDup = Api POST "$BaseUrl/api/fs/file" $admin @{ rootId = $rootId; path = 'Projekte'; name = 'notizen.json' }
Add-Result "Doppelte Datei abgelehnt" ($fileDup.Status -eq 409) "status=$($fileDup.Status)"

$fileBad = Api POST "$BaseUrl/api/fs/file" $admin @{ rootId = $rootId; path = 'Projekte'; name = '../ausbruch.txt' }
Add-Result "Ungueltiger Dateiname abgelehnt" ($fileBad.Status -eq 400) "status=$($fileBad.Status)"

# Frisch angelegt heisst leer und sofort bearbeitbar
$fresh = Api GET "$BaseUrl/api/fs/text?rootId=$rootId&path=$(Enc 'Projekte/notizen.json')" $admin
$written = Api PUT "$BaseUrl/api/fs/text" $admin @{
    rootId = $rootId; path = 'Projekte/notizen.json'; content = '{ "neu": true }'
}
$readBack = Api GET "$BaseUrl/api/fs/text?rootId=$rootId&path=$(Enc 'Projekte/notizen.json')" $admin
Add-Result "Neue Datei ist leer und bearbeitbar" (
    $fresh.Data.content -eq '' -and $fresh.Data.editable -eq $true) "len=$($fresh.Data.content.Length)"
Add-Result "Inhalt in die neue Datei geschrieben" (
    $written.Status -eq 200 -and $readBack.Data.content -eq '{ "neu": true }') "status=$($written.Status)"

$fileLog = Api GET "$BaseUrl/api/fs/activity?rootId=$rootId&path=$(Enc 'Projekte/notizen.json')" $admin
Add-Result "Anlegen im Protokoll" (
    ($fileLog.Data | ForEach-Object { $_.action }) -contains 'CreateFile') "n=$(@($fileLog.Data).Count)"

$removeNew = Api POST "$BaseUrl/api/fs/delete" $admin @{ rootId = $rootId; paths = @('Projekte/notizen.json') }
Wait-Job $admin $removeNew.Data.token | Out-Null

# --- Upload in zwei Teilstuecken -----------------------------------------
$content = ('MatFile Upload Test ' * 200)
$bytes = [System.Text.Encoding]::UTF8.GetBytes($content)
$half = [int]($bytes.Length / 2)

$begin = Api POST "$BaseUrl/api/fs/upload/begin" $admin @{ rootId = $rootId; path = 'Projekte'; name = 'upload.txt'; size = $bytes.Length; overwrite = $false }
Add-Result "Upload begonnen" ($begin.Status -eq 200 -and $null -ne $begin.Data.uploadId) ""

$uploadId = $begin.Data.uploadId
$part1 = [byte[]]$bytes[0..($half - 1)]
$part2 = [byte[]]$bytes[$half..($bytes.Length - 1)]

$c1 = Invoke-WebRequest "$BaseUrl/api/fs/upload/chunk?uploadId=$uploadId&offset=0" -Method Post -WebSession $admin -UseBasicParsing -Body $part1 -ContentType 'application/octet-stream'
$received = ($c1.Content | ConvertFrom-Json).received
Add-Result "Erstes Teilstueck" ($received -eq $part1.Length) "received=$received"

# Falscher Offset liefert die tatsaechliche Position zurueck
$wrong = Invoke-WebRequest "$BaseUrl/api/fs/upload/chunk?uploadId=$uploadId&offset=999999" -Method Post -WebSession $admin -UseBasicParsing -Body $part2 -ContentType 'application/octet-stream'
Add-Result "Falscher Offset wird korrigiert" ((($wrong.Content | ConvertFrom-Json).received) -eq $part1.Length) ""

$c2 = Invoke-WebRequest "$BaseUrl/api/fs/upload/chunk?uploadId=$uploadId&offset=$received" -Method Post -WebSession $admin -UseBasicParsing -Body $part2 -ContentType 'application/octet-stream'
Add-Result "Zweites Teilstueck" ((($c2.Content | ConvertFrom-Json).received) -eq $bytes.Length) ""

$finish = Api POST "$BaseUrl/api/fs/upload/finish?uploadId=$uploadId" $admin
Add-Result "Upload abgeschlossen" ($finish.Status -eq 200 -and $finish.Data.size -eq $bytes.Length) "size=$($finish.Data.size)"

$down = Invoke-WebRequest "$BaseUrl/api/fs/download?rootId=$rootId&path=$(Enc 'Projekte/upload.txt')" -WebSession $admin -UseBasicParsing
Add-Result "Hochgeladener Inhalt stimmt" ($down.Content -eq $content) ""

$conflict = Api POST "$BaseUrl/api/fs/upload/begin" $admin @{ rootId = $rootId; path = 'Projekte'; name = 'upload.txt'; size = 10; overwrite = $false }
Add-Result "Upload-Konflikt erkannt" ($conflict.Status -eq 409) "status=$($conflict.Status)"

# --- Kopieren, Verschieben ------------------------------------------------
$copy = Api POST "$BaseUrl/api/fs/transfer" $admin @{ sourceRootId = $rootId; paths = @('Projekte'); targetRootId = $rootId; targetPath = ''; move = $false; overwrite = $false }
$job = Wait-Job $admin $copy.Data.token
Add-Result "Kopieren abgeschlossen" ($null -ne $job -and $job.status -eq 'Completed') "status=$($job.status)"
Add-Result "Kopie mit neuem Namen" ((Names $admin $rootId '') -contains 'Projekte (2)') ((Names $admin $rootId '') -join ', ')
Add-Result "Inhalt mitkopiert" ((Names $admin $rootId 'Projekte (2)') -contains 'upload.txt') ""

$move = Api POST "$BaseUrl/api/fs/transfer" $admin @{ sourceRootId = $rootId; paths = @('Projekte (2)'); targetRootId = $rootId; targetPath = 'Dokumente'; move = $true; overwrite = $false }
$job = Wait-Job $admin $move.Data.token
Add-Result "Verschieben abgeschlossen" ($null -ne $job -and $job.status -eq 'Completed') "status=$($job.status)"
Add-Result "Quelle verschwunden" ((Names $admin $rootId '') -notcontains 'Projekte (2)') ""
Add-Result "Ziel vorhanden" ((Names $admin $rootId 'Dokumente') -contains 'Projekte (2)') ""

$into = Api POST "$BaseUrl/api/fs/transfer" $admin @{ sourceRootId = $rootId; paths = @('Projekte'); targetRootId = $rootId; targetPath = 'Projekte'; move = $false; overwrite = $true }
$job = Wait-Job $admin $into.Data.token
Add-Result "Ordner nicht in sich selbst" ($null -ne $job -and $job.status -eq 'Failed' -and $job.error -eq 'target_inside_source') "error=$($job.error)"

# --- Komprimieren, ZIP-Download, Entpacken -------------------------------
$zip = Api POST "$BaseUrl/api/fs/compress" $admin @{ rootId = $rootId; paths = @('Projekte'); targetPath = ''; name = 'Archiv.zip' }
$job = Wait-Job $admin $zip.Data.token
Add-Result "Archiv erstellt" ($null -ne $job -and $job.status -eq 'Completed' -and (Names $admin $rootId '') -contains 'Archiv.zip') "status=$($job.status)"

$zipDownload = Invoke-WebRequest "$BaseUrl/api/fs/zip?rootId=$rootId&path=$(Enc 'Projekte')&path=$(Enc 'liesmich.txt')" -WebSession $admin -UseBasicParsing
Add-Result "ZIP-Download" ([int]$zipDownload.StatusCode -eq 200 -and $zipDownload.Headers['Content-Type'] -match 'zip' -and $zipDownload.RawContentLength -gt 100) "len=$($zipDownload.RawContentLength)"

$extract = Api POST "$BaseUrl/api/fs/extract" $admin @{ rootId = $rootId; path = 'Archiv.zip'; targetPath = '' }
$job = Wait-Job $admin $extract.Data.token
Add-Result "Archiv entpackt" ($null -ne $job -and $job.status -eq 'Completed' -and (Names $admin $rootId '') -contains 'Archiv') "status=$($job.status)"
Add-Result "Entpackter Inhalt" ((Names $admin $rootId 'Archiv/Projekte') -contains 'upload.txt') ((Names $admin $rootId 'Archiv') -join ', ')

# --- Zip-Slip: Archiv mit ../ Eintrag ------------------------------------
$evil = Join-Path $env:TEMP 'matfile-evil.zip'
Remove-Item $evil -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression | Out-Null
$stream = [System.IO.File]::Open($evil, 'Create')
$archive = New-Object System.IO.Compression.ZipArchive($stream, 'Create')
$entry = $archive.CreateEntry('../../escaped.txt')
$writer = New-Object System.IO.StreamWriter($entry.Open())
$writer.Write('pwned')
$writer.Dispose()
$archive.Dispose()
$stream.Dispose()
docker cp $evil "${Container}:$WorkDir/evil.zip" | Out-Null

$slip = Api POST "$BaseUrl/api/fs/extract" $admin @{ rootId = $rootId; path = 'evil.zip'; targetPath = '' }
$job = Wait-Job $admin $slip.Data.token
$escapedOutside = docker exec $Container sh -c "test -f /storage/escaped.txt && echo yes || echo no"
Add-Result "Zip-Slip blockiert" ($job.status -eq 'Completed' -and $escapedOutside.Trim() -eq 'no' -and (Names $admin $rootId 'evil') -notcontains 'escaped.txt') "outside=$($escapedOutside.Trim())"
Remove-Item $evil -Force -ErrorAction SilentlyContinue

# --- Loeschen -------------------------------------------------------------
$del = Api POST "$BaseUrl/api/fs/delete" $admin @{ rootId = $rootId; paths = @('Archiv', 'Archiv.zip', 'evil', 'evil.zip') }
$job = Wait-Job $admin $del.Data.token
$remaining = Names $admin $rootId ''
Add-Result "Loeschen abgeschlossen" ($null -ne $job -and $job.status -eq 'Completed') "status=$($job.status)"
Add-Result "Eintraege entfernt" ($remaining -notcontains 'Archiv' -and $remaining -notcontains 'Archiv.zip' -and $remaining -notcontains 'evil.zip') ($remaining -join ', ')

# --- Rechte eines Benutzers ohne Schreibrecht ----------------------------
New-MatUser $BaseUrl $admin @{
    username = 'tester'; displayName = 'Tester'; newPassword = $TesterPassword; roleIds = @($roles['User'])
} | Out-Null

$tester = New-MatSession
Connect-Mat $BaseUrl $tester 'tester' $TesterPassword | Out-Null

$noFolder = Api POST "$BaseUrl/api/fs/folder" $tester @{ rootId = $rootId; path = ''; name = 'Verboten' }
Add-Result "Ordner ohne Schreibrecht gesperrt" ($noFolder.Status -eq 403) "status=$($noFolder.Status)"

$noFile = Api POST "$BaseUrl/api/fs/file" $tester @{ rootId = $rootId; path = ''; name = 'verboten.txt' }
Add-Result "Datei ohne Schreibrecht gesperrt" ($noFile.Status -eq 403) "status=$($noFile.Status)"

$noUpload = Api POST "$BaseUrl/api/fs/upload/begin" $tester @{ rootId = $rootId; path = ''; name = 'x.txt'; size = 1; overwrite = $false }
Add-Result "Upload ohne Recht gesperrt" ($noUpload.Status -eq 403) "status=$($noUpload.Status)"

$noDelete = Api POST "$BaseUrl/api/fs/delete" $tester @{ rootId = $rootId; paths = @('Projekte') }
Add-Result "Loeschen ohne Recht gesperrt" ($noDelete.Status -eq 403) "status=$($noDelete.Status)"

# --- Groessengrenze beim Hochladen ---------------------------------------
$system = (Get-MatJson "$BaseUrl/api/system" $admin).Data
$limit = @{
    sessionLifetimeDays = $system.sessionLifetimeDays
    loginMaxFailedAttempts = $system.loginMaxFailedAttempts
    loginLockoutMinutes = $system.loginLockoutMinutes
    loginAttemptsPerMinute = $system.loginAttemptsPerMinute
    minPasswordLength = $system.minPasswordLength
    maxUploadMegabytes = 1
    requireSecureCookie = $system.requireSecureCookie
    requirePasswordChangeOnFirstLogin = $system.requirePasswordChangeOnFirstLogin
}
Add-Result "Ohne Grenze steht 0" ($system.maxUploadMegabytes -eq 0) "maxUpload=$($system.maxUploadMegabytes)"

$setLimit = Send-MatJson 'Put' "$BaseUrl/api/system" $admin $limit
Add-Result "Grenze gesetzt" ($setLimit.Status -eq 200 -and $setLimit.Data.maxUploadMegabytes -eq 1) "maxUpload=$($setLimit.Data.maxUploadMegabytes)"

$tooBig = Api POST "$BaseUrl/api/fs/upload/begin" $admin @{
    rootId = $rootId; path = ''; name = 'zu-gross.bin'; size = 5MB; overwrite = $false
}
Add-Result "Zu grosse Datei abgelehnt" (
    $tooBig.Status -eq 413 -and $tooBig.Data.error -eq 'upload_too_large') "status=$($tooBig.Status)"

$smallEnough = Api POST "$BaseUrl/api/fs/upload/begin" $admin @{
    rootId = $rootId; path = ''; name = 'passt.bin'; size = 1024; overwrite = $false
}
Add-Result "Kleine Datei weiter erlaubt" ($smallEnough.Status -eq 200) "status=$($smallEnough.Status)"

# Wer mehr schickt als angekuendigt, wird beim Teilstueck gestoppt - sonst liesse
# sich die Grenze umgehen, indem man eine kleine Groesse ankuendigt.
$overflow = [byte[]]::new(2MB)
$overflowResult = $null
try {
    Invoke-WebRequest "$BaseUrl/api/fs/upload/chunk?uploadId=$($smallEnough.Data.uploadId)&offset=0" `
        -Method Post -WebSession $admin -UseBasicParsing -Body $overflow -ContentType 'application/octet-stream' | Out-Null
} catch {
    $overflowResult = [int]$_.Exception.Response.StatusCode
}
Add-Result "Mehr als angekuendigt wird gestoppt" ($overflowResult -eq 413 -or $overflowResult -eq 400) "status=$overflowResult"

$limit.maxUploadMegabytes = 0
$reset = Send-MatJson 'Put' "$BaseUrl/api/system" $admin $limit
Add-Result "Grenze wieder aufgehoben" ($reset.Data.maxUploadMegabytes -eq 0) "maxUpload=$($reset.Data.maxUploadMegabytes)"

# --- Zugriffsrechte auf einem lokalen Ordner -----------------------------
$perm = Api GET "$BaseUrl/api/fs/permissions?rootId=$rootId&path=$(Enc 'Dokumente')" $admin
Add-Result "Lokale Rechte gelesen" (
    $perm.Status -eq 200 -and $perm.Data.supported -and $perm.Data.canChange -and
    $perm.Data.modeText -match '^\d{3}$') "mode=$($perm.Data.modeText)"

$setPerm = Api PUT "$BaseUrl/api/fs/permissions" $admin @{ rootId = $rootId; path = 'Dokumente'; mode = 493 }
$onDisk = (docker exec $Container sh -c "ls -ld $WorkDir/Dokumente" | Out-String).Trim()
Add-Result "Lokale Rechte gesetzt" (
    $setPerm.Status -eq 200 -and $setPerm.Data.modeText -eq '755' -and $onDisk -match '^drwxr-xr-x') "$onDisk"

$readBack = Api GET "$BaseUrl/api/fs/permissions?rootId=$rootId&path=$(Enc 'Dokumente')" $admin
Add-Result "Lokale Rechte wieder gelesen" ($readBack.Data.modeText -eq '755') "mode=$($readBack.Data.modeText)"

$permTester = Api GET "$BaseUrl/api/fs/permissions?rootId=$rootId&path=$(Enc 'Dokumente')" $tester
Add-Result "Rechte ohne Schreibrecht nur lesbar" (
    $permTester.Status -eq 200 -and $permTester.Data.supported -and
    $permTester.Data.canChange -eq $false) "canChange=$($permTester.Data.canChange)"

$permDenied = Api PUT "$BaseUrl/api/fs/permissions" $tester @{ rootId = $rootId; path = 'Dokumente'; mode = 511 }
Add-Result "Rechte ohne Schreibrecht nicht setzbar" ($permDenied.Status -eq 403) "status=$($permDenied.Status)"

$permAnon = Api GET "$BaseUrl/api/fs/permissions?rootId=$rootId&path=$(Enc 'Dokumente')" (New-MatSession)
Add-Result "Rechte ohne Anmeldung gesperrt" ($permAnon.Status -eq 401) "status=$($permAnon.Status)"

# --- Job eines fremden Benutzers ----------------------------------------
$foreign = Api POST "$BaseUrl/api/jobs/$($del.Data.token)/cancel" $tester
Add-Result "Fremder Job nicht abbrechbar" ($foreign.Status -eq 404) "status=$($foreign.Status)"

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
