$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '未找到 Windows .NET Framework C# 编译器。' }
Push-Location -LiteralPath $PSScriptRoot
try {
    & $compiler /nologo /target:winexe /optimize+ /out:VaultPicker.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll VaultPicker.cs
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
} finally { Pop-Location }
Write-Output "已构建 $PSScriptRoot/VaultPicker.exe"
