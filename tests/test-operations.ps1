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
$admin = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$r = Invoke-WebRequest "$BaseUrl/Login" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$r = Post-Form "$BaseUrl/Login" $admin "__RequestVerificationToken=$(Enc $t)&Input.Username=admin&Input.Password=$(Enc $SeedPassword)"
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

$r = Invoke-WebRequest "$BaseUrl/Admin/Roots/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$roleRows = @{}
foreach ($m in [regex]::Matches($r.Content, '(?s)name="Input\.Access\[(\d+)\]\.RoleId" value="(\d+)"[^>]*>\s*([^\s<]+)')) {
    $roleRows[$m.Groups[3].Value.Trim()] = @{ Index = $m.Groups[1].Value; RoleId = $m.Groups[2].Value }
}

# Rolle User bekommt nur Lesen - fuer die Rechtepruefung weiter unten
$body = "__RequestVerificationToken=$(Enc $t)&Input.Name=Sample&Input.StorageType=Local&Input.BasePath=$(Enc $WorkDir)&Input.SortOrder=1&Input.IsEnabled=true"
foreach ($role in $roleRows.Keys) {
    $row = $roleRows[$role]
    $body += "&Input.Access[$($row.Index)].RoleId=$($row.RoleId)"
    if ($role -eq 'User') { $body += "&Input.Access[$($row.Index)].CanRead=true" }
}
$r = Post-Form "$BaseUrl/Admin/Roots/Edit" $admin $body
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
$r = Invoke-WebRequest "$BaseUrl/Admin/Users/Edit" -WebSession $admin -UseBasicParsing
$t = Get-Token $r.Content
$roleIds = @{}
foreach ($m in [regex]::Matches($r.Content, '(?s)name="Input\.RoleIds" value="(\d+)".*?<span>([^<]+)</span>')) {
    $roleIds[$m.Groups[2].Value.Trim()] = $m.Groups[1].Value
}
$body = "__RequestVerificationToken=$(Enc $t)&Input.Username=tester&Input.DisplayName=Tester&Input.NewPassword=$(Enc $TesterPassword)&Input.IsActive=true&Input.MustChangePassword=false&Input.RoleIds=$($roleIds['User'])"
Post-Form "$BaseUrl/Admin/Users/Edit" $admin $body | Out-Null

$tester = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$r = Invoke-WebRequest "$BaseUrl/Login" -WebSession $tester -UseBasicParsing
$t = Get-Token $r.Content
Post-Form "$BaseUrl/Login" $tester "__RequestVerificationToken=$(Enc $t)&Input.Username=tester&Input.Password=$(Enc $TesterPassword)" | Out-Null

$noFolder = Api POST "$BaseUrl/api/fs/folder" $tester @{ rootId = $rootId; path = ''; name = 'Verboten' }
Add-Result "Ordner ohne Schreibrecht gesperrt" ($noFolder.Status -eq 403) "status=$($noFolder.Status)"

$noUpload = Api POST "$BaseUrl/api/fs/upload/begin" $tester @{ rootId = $rootId; path = ''; name = 'x.txt'; size = 1; overwrite = $false }
Add-Result "Upload ohne Recht gesperrt" ($noUpload.Status -eq 403) "status=$($noUpload.Status)"

$noDelete = Api POST "$BaseUrl/api/fs/delete" $tester @{ rootId = $rootId; paths = @('Projekte') }
Add-Result "Loeschen ohne Recht gesperrt" ($noDelete.Status -eq 403) "status=$($noDelete.Status)"

# --- Job eines fremden Benutzers ----------------------------------------
$foreign = Api POST "$BaseUrl/api/jobs/$($del.Data.token)/cancel" $tester
Add-Result "Fremder Job nicht abbrechbar" ($foreign.Status -eq 404) "status=$($foreign.Status)"

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
