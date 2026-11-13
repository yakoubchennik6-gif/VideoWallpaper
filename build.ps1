# Builds VideoWallpaper.exe using the .NET Framework compiler built into Windows
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$args = @(
    "/nologo", "/codepage:65001", "/target:winexe", "/platform:anycpu", "/optimize+", "/win32manifest:$dir\app.manifest",
    "/out:$dir\VideoWallpaper.exe",
    "/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll", "/r:Microsoft.CSharp.dll",
    "$dir\VideoWallpaper.cs"
)
& $csc $args
if ($LASTEXITCODE -eq 0) {
    $size = (Get-Item "$dir\VideoWallpaper.exe").Length
    Write-Host ("Build OK: {0}  ({1:N1} KB)" -f "$dir\VideoWallpaper.exe", ($size/1KB)) -ForegroundColor Green
} else {
    Write-Host "Build FAILED" -ForegroundColor Red
}
