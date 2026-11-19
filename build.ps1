# Builds DriveWatch365.exe with the C# compiler that ships with Windows. No installs needed.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
$dist = Join-Path $here 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$exe  = Join-Path $dist 'DriveWatch365.exe'
$ico  = Join-Path $here 'app.ico'
$refs = '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
        '/r:System.Management.dll', '/r:System.Web.Extensions.dll'

function Compile($withIcon) {
    $a = @('/nologo', '/target:winexe', "/out:$exe", '/optimize+', "/win32manifest:$here\app.manifest") + $refs
    if ($withIcon) { $a += "/win32icon:$ico" }
    $a += "$here\DriveWatch.cs"
    & $csc @a
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

# Pass 1 builds without an icon so the program can draw its own icon file; pass 2 embeds it.
Compile $false
& $exe --make-icon $ico
Compile $true
Write-Host "Built $exe"

