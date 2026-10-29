param([string]$OutputName = 'DeviceScope-0.8.exe')
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$references = @(
    (Join-Path $wpf 'PresentationCore.dll'),
    (Join-Path $wpf 'PresentationFramework.dll'),
    (Join-Path $wpf 'WindowsBase.dll'),
    (Join-Path $framework 'System.Xaml.dll'),
    (Join-Path $framework 'System.Management.dll'),
    (Join-Path $framework 'System.Web.Extensions.dll'),
    (Join-Path $framework 'System.Xml.dll')
)
$iconPath = Join-Path $PSScriptRoot 'DeviceScope.ico'
$compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', ('/out:' + (Join-Path $PSScriptRoot $OutputName)), '/main:DeviceScope.App', ('/win32icon:' + $iconPath), '/resource:DeviceScope.ico,DeviceScope.Icon.ico')
$compilerArgs += $references | ForEach-Object { '/reference:' + $_ }
$compilerArgs += @('DeviceScope.cs', 'UiTheme.cs', 'Inventory.cs', 'DeviceMetadataReader.cs', 'DeviceClassifier.cs', 'ProcessesView.cs', 'ProcessControl.cs', 'AssemblyInfo.cs', 'DriverDiagnosticsReader.cs', 'DriverDiagnosticsView.cs', 'PerformanceReader.cs', 'PerformanceView.cs', 'DiskHealthReader.cs', 'DiskHealthView.cs', 'SystemSpecificationsReader.cs', 'SystemSpecificationsView.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
Push-Location -LiteralPath $PSScriptRoot
try {
    & (Join-Path $framework 'csc.exe') @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'DeviceScope compilation failed. Close the app first if the EXE is in use.' }
}
finally { Pop-Location }
Write-Output ('Built ' + $OutputName)
