param(
    [string]$OldZip = "",
    [string]$TargetFolder = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$target = Join-Path $TargetFolder 'bin\odx-capture.json'
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
    throw "Cannot find bin\odx-capture.json in target: $TargetFolder"
}
if (-not $OldZip) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Title = 'Select original odx-capture-0.3.2-win-x64.zip'
    $dialog.Filter = 'ODX 0.3.2 ZIP (*.zip)|*.zip'
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
    $OldZip = $dialog.FileName
}
if (-not (Test-Path -LiteralPath $OldZip -PathType Leaf)) { throw "Source ZIP not found: $OldZip" }

$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $OldZip).Path)
try {
    $source = @($archive.Entries | Where-Object { $_.FullName -match '(^|/)bin/odx-capture\.json$' })
    if ($source.Count -ne 1) { throw "Expected one bin/odx-capture.json in original ZIP, found $($source.Count)" }
    $reader = New-Object System.IO.StreamReader($source[0].Open())
    try { $old = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally { $archive.Dispose() }

$current = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
if (-not $old.orchestrator -or -not $old.access_key) {
    throw 'Original package does not contain both orchestrator and access_key'
}
$address = $null
if (-not [uri]::TryCreate([string]$old.orchestrator, [UriKind]::Absolute, [ref]$address) -or
    $address.Scheme -notin @('https', 'http')) { throw 'Invalid orchestrator URL' }

# Preserve the 0.3.3 settings; restore only the room server and access key.
$current | Add-Member -NotePropertyName 'orchestrator' -NotePropertyValue ([string]$old.orchestrator) -Force
$current | Add-Member -NotePropertyName 'access_key' -NotePropertyValue ([string]$old.access_key) -Force
$backup = "$target.before-room-restore.bak"
Copy-Item -LiteralPath $target -Destination $backup -Force
$encoding = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($target, ($current | ConvertTo-Json -Depth 20), $encoding)
Write-Host 'ROOM CONFIG RESTORED. Restart Odx Capture, then test Create Room / Join Room.'
Write-Host "Backup of previous settings: $backup"
Write-Host 'Do not upload the resulting JSON to a public repository.'
