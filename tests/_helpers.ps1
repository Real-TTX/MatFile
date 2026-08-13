# Gemeinsame Hilfen der Testsuiten. Seit die Verwaltung in Dialogen steckt,
# laufen Anmeldung, Benutzer und Speicherorte ueber /api.

function New-MatSession {
    return New-Object Microsoft.PowerShell.Commands.WebRequestSession
}

function Get-MatToken([string]$html) {
    if ($html -match 'name="__RequestVerificationToken"[^>]*value="([^"]+)"') { return $Matches[1] }
    return $null
}

function Enc([string]$s) { return [System.Uri]::EscapeDataString($s) }

function Post-Form([string]$url, $session, [string]$body) {
    return Invoke-WebRequest $url -Method Post -WebSession $session -UseBasicParsing `
        -Body $body -ContentType 'application/x-www-form-urlencoded'
}

# Meldet einen Benutzer an und liefert die Antwort der Anmeldeseite.
function Connect-Mat([string]$baseUrl, $session, [string]$user, [string]$password) {
    $page = Invoke-WebRequest "$baseUrl/Login" -WebSession $session -UseBasicParsing
    $token = Get-MatToken $page.Content
    return Post-Form "$baseUrl/Login" $session `
        "__RequestVerificationToken=$(Enc $token)&Input.Username=$(Enc $user)&Input.Password=$(Enc $password)"
}

# JSON-Aufruf mit Status und Antwortkoerper - auch im Fehlerfall.
function Send-MatJson([string]$method, [string]$url, $session, $payload) {
    try {
        $body = if ($null -eq $payload) { $null } else { $payload | ConvertTo-Json -Depth 6 -Compress }
        $response = Invoke-WebRequest $url -Method $method -WebSession $session -UseBasicParsing `
            -Body $body -ContentType 'application/json'
        $data = if ($response.Content) { $response.Content | ConvertFrom-Json } else { $null }
        return @{ Status = [int]$response.StatusCode; Data = $data }
    } catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        $text = $null

        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            $text = $_.ErrorDetails.Message
        } elseif ($_.Exception.Response) {
            # In PowerShell 5.1 steckt der Fehlerkoerper nur im Antwortstrom.
            try {
                $stream = $_.Exception.Response.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $text = $reader.ReadToEnd()
                $reader.Dispose()
            } catch {
                $text = $null
            }
        }

        $data = $null
        if ($text) { try { $data = $text | ConvertFrom-Json } catch { $data = $null } }
        return @{ Status = $status; Data = $data; Raw = $text }
    }
}

function Get-MatJson([string]$url, $session) {
    return Send-MatJson 'Get' $url $session $null
}

# Rollen als Zuordnung Name -> Id. Kommt aus der Speicherortliste,
# damit auch ohne das Recht "Rollen verwalten" gearbeitet werden kann.
function Get-MatRoles([string]$baseUrl, $session) {
    $result = Get-MatJson "$baseUrl/api/roots" $session
    $roles = @{}
    foreach ($role in $result.Data.roles) { $roles[$role.roleName] = $role.roleId }
    return $roles
}

# Legt einen Speicherort an. $access ist eine Liste aus @{ roleId; canRead; ... }.
function New-MatRoot([string]$baseUrl, $session, [hashtable]$values) {
    $payload = @{
        name = ''; type = 'Local'; path = ''; host = ''; port = 0; share = $null; url = $null
        username = $null; password = $null; useTls = $false; passive = $true
        isEnabled = $true; icon = $null; sortOrder = 0; access = @()
    }

    foreach ($key in $values.Keys) { $payload[$key] = $values[$key] }

    return Send-MatJson 'Post' "$baseUrl/api/roots" $session $payload
}

function Set-MatRootAccess([string]$baseUrl, $session, [long]$rootId, $access) {
    $detail = (Get-MatJson "$baseUrl/api/roots/$rootId" $session).Data

    $payload = @{
        name = $detail.name; type = $detail.type; path = $detail.path
        host = $detail.host; port = $detail.port; share = $detail.share; url = $detail.url
        username = $detail.username; password = $null
        useTls = $detail.useTls; passive = $detail.passive
        isEnabled = $detail.isEnabled; icon = $detail.icon; sortOrder = $detail.sortOrder
        access = $access
    }

    return Send-MatJson 'Put' "$baseUrl/api/roots/$rootId" $session $payload
}

# Rechte einer Rolle als Hashtabelle fuer die Rechtematrix.
function New-MatAccess([long]$roleId, [bool]$read, [bool]$download, [bool]$upload, [bool]$write, [bool]$delete) {
    return @{
        roleId = $roleId; canRead = $read; canDownload = $download; canUpload = $upload
        canWrite = $write; canDelete = $delete; canShare = $false
    }
}

function New-MatUser([string]$baseUrl, $session, [hashtable]$values) {
    $payload = @{
        username = ''; displayName = ''; email = $null; isActive = $true
        newPassword = $null; mustChangePassword = $false; roleIds = @()
    }

    foreach ($key in $values.Keys) { $payload[$key] = $values[$key] }

    return Send-MatJson 'Post' "$baseUrl/api/users" $session $payload
}
