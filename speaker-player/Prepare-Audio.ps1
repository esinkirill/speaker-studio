[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$appRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$work = Join-Path $temporaryBase ('SpeakerStudio-Audio-' + [Guid]::NewGuid().ToString('N'))
$ffmpegUrl = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08/ffmpeg-n8.1.3-9-g29e619e767-win64-lgpl-shared-8.1.zip'
$ffmpegHash = '3e47bda1607740550141e37c0e49d1e5182b34699f15adfd137ee266d346811a'
$essentiaUrl = 'https://registry.npmjs.org/essentia.js/-/essentia.js-0.1.3.tgz'
$essentiaIntegrity = 'vVEPgeVMEBLRXbM5o5H5Rgu53EPHu25vyFKYg+flWLzI/nEoegJQez9FKRv8GR/KxIBwm+fXDEFL+MkQeoHaLw=='
$decoder = Join-Path $appRoot 'assets\ffmpeg\ffmpeg.exe'
$essentiaDirectory = Join-Path $appRoot 'runtime\essentia'

function Test-Prepared([string]$Directory) {
    $marker = Join-Path $Directory 'setup-source.json'
    if (-not (Test-Path -LiteralPath $marker)) { return $false }
    try {
        $saved = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if (-not $saved.files.Count) { return $false }
        foreach ($entry in $saved.files) {
            $file = Join-Path $Directory $entry.name
            if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
                (Get-Item -LiteralPath $file).Length -ne $entry.bytes) { return $false }
        }
        return $true
    } catch { return $false }
}

function Save-Prepared([string]$Directory, [hashtable]$Origin) {
    $Origin.files = @(Get-ChildItem -LiteralPath $Directory -File |
        Where-Object { $_.Name -ne 'setup-source.json' } |
        ForEach-Object { @{name=$_.Name;bytes=$_.Length} })
    $Origin | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'setup-source.json') -Encoding UTF8
}

function Get-CheckedFile([string]$Url, [string]$Path, [string]$Algorithm, [string]$Expected) {
    Write-Host ('Download: ' + $Url)
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $Path
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
    if ($actual -cne $Expected.ToLowerInvariant()) {
        throw ('Downloaded file does not match ' + $Algorithm + ': ' + [IO.Path]::GetFileName($Path))
    }
}

New-Item -ItemType Directory -Path $work -ErrorAction Stop | Out-Null
try {
    if (-not (Test-Prepared (Split-Path -Parent $decoder))) {
        $zip = Join-Path $work 'ffmpeg.zip'
        Get-CheckedFile $ffmpegUrl $zip 'SHA256' $ffmpegHash
        $unpacked = Join-Path $work 'ffmpeg'
        Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
        $package = @(Get-ChildItem -LiteralPath $unpacked -Directory)
        if ($package.Count -ne 1 -or -not (Test-Path -LiteralPath (Join-Path $package[0].FullName 'bin\ffmpeg.exe'))) {
            throw 'Unexpected FFmpeg archive structure.'
        }
        $destination = Split-Path -Parent $decoder
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $package[0].FullName 'bin') -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
        # Keep the upstream README, license and origin beside downloaded binaries.
        Get-ChildItem -LiteralPath $package[0].FullName -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
        Get-ChildItem -LiteralPath $package[0].FullName -Directory | Where-Object { $_.Name -match '^licenses?$' } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force
        }
        Save-Prepared $destination @{component='FFmpeg';url=$ffmpegUrl;sha256=$ffmpegHash}
    } else { Write-Host 'FFmpeg is already present.' }

    if (-not ((Test-Prepared $essentiaDirectory) -and
              (Test-Path -LiteralPath (Join-Path $essentiaDirectory 'essentia-wasm.umd.js')) -and
              (Test-Path -LiteralPath (Join-Path $essentiaDirectory 'essentia.js-core.umd.js')))) {
        $tar = Join-Path $env:WINDIR 'System32\tar.exe'
        if (-not (Test-Path -LiteralPath $tar)) { throw 'Windows tar.exe is required (Windows 10 22H2 / Windows 11).' }
        $archive = Join-Path $work 'essentia.tgz'
        $expectedBytes = [Convert]::FromBase64String($essentiaIntegrity)
        $expectedHex = [BitConverter]::ToString($expectedBytes).Replace('-', '').ToLowerInvariant()
        Get-CheckedFile $essentiaUrl $archive 'SHA512' $expectedHex
        $members = @(& $tar -tzf $archive)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read Essentia archive.' }
        foreach ($member in $members) {
            if ($member -notmatch '^package/' -or $member -match '(^|/)\.\.(/|$)' -or $member.Contains('\')) {
                throw 'Unexpected Essentia archive member.'
            }
        }
        $unpacked = Join-Path $work 'essentia'
        New-Item -ItemType Directory -Path $unpacked | Out-Null
        & $tar -xzf $archive -C $unpacked
        if ($LASTEXITCODE -ne 0) { throw 'Cannot extract Essentia archive.' }
        $package = Join-Path $unpacked 'package'
        New-Item -ItemType Directory -Path $essentiaDirectory -Force | Out-Null
        foreach ($name in @('essentia-wasm.umd.js','essentia.js-core.umd.js')) {
            Copy-Item -LiteralPath (Join-Path $package "dist\$name") -Destination $essentiaDirectory -Force
        }
        foreach ($name in @('LICENSE','README.md','package.json')) {
            if (Test-Path -LiteralPath (Join-Path $package $name)) {
                Copy-Item -LiteralPath (Join-Path $package $name) -Destination $essentiaDirectory -Force
            }
        }
        Save-Prepared $essentiaDirectory @{component='Essentia.js';version='0.1.3';url=$essentiaUrl;sha512=$expectedHex}
    } else { Write-Host 'Essentia.js is already present.' }

    # Persist both origins before runtime checks, so a missing system prerequisite
    # can be fixed and retried without losing the downloaded component metadata.
    $origins = @(
        (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $decoder) 'setup-source.json') -Raw | ConvertFrom-Json),
        (Get-Content -LiteralPath (Join-Path $essentiaDirectory 'setup-source.json') -Raw | ConvertFrom-Json)
    )
    New-Item -ItemType Directory -Path (Join-Path $appRoot 'data') -Force | Out-Null
    $origins | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $appRoot 'data\audio-tools.json') -Encoding UTF8
    & $decoder -hide_banner -loglevel error -f lavfi -i 'anullsrc=r=16000:cl=mono' -t 0.1 -f null -
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg could not load or decode. Install the official Visual C++ x64 runtime if Windows reports a missing DLL.' }
    $node = Join-Path $appRoot 'runtime\node.exe'
    if (Test-Path -LiteralPath $node) {
        $loader = "const p=require('node:path');const r=process.argv[1];const W=require(p.join(r,'essentia-wasm.umd.js'));const C=require(p.join(r,'essentia.js-core.umd.js'));const E=C.Essentia||C;const e=new E(W.EssentiaWASM||W);e.shutdown();console.log('Essentia loaded.');"
        & $node -e $loader $essentiaDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Essentia.js could not load in the bundled Node runtime.' }
    }
    $python = Join-Path $appRoot 'runtime\transcription\python.exe'
    if (Test-Path -LiteralPath $python) {
        & $python -I -B (Join-Path $appRoot 'converter\transcribe-midi.py') --check-runtime
        if ($LASTEXITCODE -ne 0) { throw 'Model/runtime check failed. Install Microsoft Visual C++ v14 x64: https://aka.ms/vc14/vc_redist.x64.exe' }
    }
    Write-Host 'Ready. Open SpeakerStudio.exe, then MP3 -> MIDI.' -ForegroundColor Green
}
finally {
    # This directory was created by this run. Resolve and check it before cleanup.
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $resolvedParent = [IO.Path]::GetFullPath((Split-Path -Parent $resolvedWork))
    if ($resolvedParent.TrimEnd('\') -eq $temporaryBase.TrimEnd('\') -and
        [IO.Path]::GetFileName($resolvedWork) -match '^SpeakerStudio-Audio-[a-f0-9]{32}$' -and
        (Test-Path -LiteralPath $resolvedWork)) {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
