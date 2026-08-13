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
$WorkDir = "/storage/pvtest"

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

# --- Testdaten im Container ----------------------------------------------
docker exec $Container sh -c @"
rm -rf $WorkDir && mkdir -p $WorkDir/Unterordner
head -c 2048 /dev/urandom > $WorkDir/bild.png
head -c 4096 /dev/urandom > $WorkDir/clip.mp4
head -c 512 /dev/urandom > $WorkDir/daten.bin
printf 'Zeile eins\nZeile zwei\n' > $WorkDir/notiz.txt
printf '<script>alert(1)</script>' > $WorkDir/seite.html
head -c 600000 /dev/zero | tr '\0' 'a' > $WorkDir/gross.txt
"@ | Out-Null
Add-Result "Testdaten angelegt" ($LASTEXITCODE -eq 0) $WorkDir

# --- Anmeldung und Root ---------------------------------------------------
$admin = New-MatSession
$r = Connect-Mat $BaseUrl $admin 'admin' $SeedPassword
Add-Result "Administrator angemeldet" ($r.BaseResponse.ResponseUri.AbsolutePath -eq '/') $r.BaseResponse.ResponseUri.AbsolutePath

$roles = Get-MatRoles $BaseUrl $admin

# Rolle User darf nur lesen, nicht herunterladen - damit ist auch keine Vorschau erlaubt
New-MatRoot $BaseUrl $admin @{
    name = 'Medien'; path = $WorkDir; sortOrder = 1
    access = @((New-MatAccess $roles['User'] $true $false $false $false $false))
} | Out-Null
$rootId = ((Api GET "$BaseUrl/api/fs/roots" $admin).Data | Where-Object { $_.name -eq 'Medien' }).id
Add-Result "Root angelegt" ($null -ne $rootId) "id=$rootId"

# --- Vorschautypen im Listing --------------------------------------------
$list = (Api GET "$BaseUrl/api/fs/list?rootId=$rootId" $admin).Data
$kind = @{}
$list.entries | ForEach-Object { $kind[$_.name] = $_.preview }
Add-Result "Vorschautypen im Listing" (
    $kind['bild.png'] -eq 'image' -and $kind['clip.mp4'] -eq 'video' -and
    $kind['notiz.txt'] -eq 'text' -and $kind['daten.bin'] -eq 'none' -and
    $kind['Unterordner'] -eq 'none') ($kind.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } | Sort-Object) -join ' '

# HTML wird als Text eingestuft, nicht als anzeigbares Dokument
Add-Result "HTML gilt als Text" ($kind['seite.html'] -eq 'text') "seite.html=$($kind['seite.html'])"

# --- Streaming ------------------------------------------------------------
$image = Invoke-WebRequest "$BaseUrl/api/fs/preview?rootId=$rootId&path=bild.png" -WebSession $admin -UseBasicParsing
Add-Result "Bild wird ausgeliefert" ([int]$image.StatusCode -eq 200 -and $image.Headers['Content-Type'] -eq 'image/png') "type=$($image.Headers['Content-Type'])"
Add-Result "Inline statt Anhang" ($image.Headers['Content-Disposition'] -match '^inline') "disp=$($image.Headers['Content-Disposition'])"
Add-Result "Schutzheader gesetzt" (
    $image.Headers['X-Content-Type-Options'] -eq 'nosniff' -and
    $image.Headers['Content-Security-Policy'] -match 'sandbox') "csp=$($image.Headers['Content-Security-Policy'])"

Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.CookieContainer = $admin.Cookies
$client = New-Object System.Net.Http.HttpClient($handler)
$request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$BaseUrl/api/fs/preview?rootId=$rootId&path=clip.mp4")
$request.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue(0, 99)
$response = $client.SendAsync($request).Result
$rangeStatus = [int]$response.StatusCode
$rangeLength = $response.Content.ReadAsByteArrayAsync().Result.Length
$client.Dispose()
Add-Result "Video mit Range streamen" ($rangeStatus -eq 206 -and $rangeLength -eq 100) "status=$rangeStatus len=$rangeLength"

# --- Nicht anzeigbare Typen ----------------------------------------------
$binary = Api GET "$BaseUrl/api/fs/preview?rootId=$rootId&path=daten.bin" $admin
Add-Result "Unbekannter Typ nicht inline" ($binary.Status -eq 415) "status=$($binary.Status)"

$html = Api GET "$BaseUrl/api/fs/preview?rootId=$rootId&path=seite.html" $admin
Add-Result "HTML nie inline ausgeliefert" ($html.Status -eq 415) "status=$($html.Status)"

$folder = Api GET "$BaseUrl/api/fs/preview?rootId=$rootId&path=Unterordner" $admin
Add-Result "Ordner ohne Vorschau" ($folder.Status -eq 404) "status=$($folder.Status)"

$escape = Api GET "$BaseUrl/api/fs/preview?rootId=$rootId&path=$(Enc '../../etc/passwd')" $admin
Add-Result "Pfadausbruch blockiert" ($escape.Status -eq 400 -or $escape.Status -eq 404) "status=$($escape.Status)"

# --- Textvorschau ---------------------------------------------------------
$text = Api GET "$BaseUrl/api/fs/text?rootId=$rootId&path=notiz.txt" $admin
Add-Result "Text gelesen" ($text.Status -eq 200 -and $text.Data.content -match 'Zeile eins' -and $text.Data.truncated -eq $false) ""

$big = Api GET "$BaseUrl/api/fs/text?rootId=$rootId&path=gross.txt" $admin
Add-Result "Grosse Datei gekuerzt" ($big.Status -eq 200 -and $big.Data.truncated -eq $true -and $big.Data.content.Length -le 524288) "len=$($big.Data.content.Length)"

$htmlText = Api GET "$BaseUrl/api/fs/text?rootId=$rootId&path=seite.html" $admin
Add-Result "HTML als Text lesbar" ($htmlText.Status -eq 200 -and $htmlText.Data.content -match 'script') ""

$noText = Api GET "$BaseUrl/api/fs/text?rootId=$rootId&path=bild.png" $admin
Add-Result "Bild nicht als Text" ($noText.Status -eq 415) "status=$($noText.Status)"

# --- Protokoll ------------------------------------------------------------
Api POST "$BaseUrl/api/fs/folder" $admin @{ rootId = $rootId; path = ''; name = 'Protokolltest' } | Out-Null
$activity = Api GET "$BaseUrl/api/fs/activity?rootId=$rootId&path=Protokolltest" $admin
Add-Result "Protokoll zum Eintrag" ($activity.Status -eq 200 -and @($activity.Data).Count -ge 1 -and $activity.Data[0].action -eq 'CreateFolder') "n=$(@($activity.Data).Count)"

# --- Rechte ---------------------------------------------------------------
New-MatUser $BaseUrl $admin @{
    username = 'tester'; displayName = 'Tester'; newPassword = $TesterPassword; roleIds = @($roles['User'])
} | Out-Null

$tester = New-MatSession
Connect-Mat $BaseUrl $tester 'tester' $TesterPassword | Out-Null

$denied = Api GET "$BaseUrl/api/fs/preview?rootId=$rootId&path=bild.png" $tester
Add-Result "Vorschau ohne Downloadrecht gesperrt" ($denied.Status -eq 403) "status=$($denied.Status)"

$deniedText = Api GET "$BaseUrl/api/fs/text?rootId=$rootId&path=notiz.txt" $tester
Add-Result "Textvorschau ohne Recht gesperrt" ($deniedText.Status -eq 403) "status=$($deniedText.Status)"

$anon = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$anonPreview = Api GET "$BaseUrl/api/fs/preview?rootId=$rootId&path=bild.png" $anon
Add-Result "Vorschau ohne Anmeldung gesperrt" ($anonPreview.Status -eq 401) "status=$($anonPreview.Status)"

Write-Host ""
$failed = @($results | Where-Object { -not $_.OK })
Write-Host "Ergebnis: $($results.Count - $failed.Count)/$($results.Count) bestanden"
if ($failed.Count -gt 0) { exit 1 }
