param([switch]$Native,[switch]$Projects)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
 $package=Join-Path $PSScriptRoot 'release\MoyuBox-0.8.0'
 if(!(Test-Path (Join-Path $package '摸鱼盒子.exe'))){throw '请先运行 build.ps1 构建 0.8.0'}
 $bin=Join-Path $PSScriptRoot 'build\diagnostics-tests'
 New-Item -ItemType Directory -Force $bin | Out-Null
 Copy-Item "$package\*.dll","$package\*.exe","$package\*.config" -Destination $bin -Force
 & 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /out:build\diagnostics-tests\DiagnosticsTests.exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/reference:$package\摸鱼盒子.exe" tests\DiagnosticsTests.cs
 if($LASTEXITCODE -ne 0){throw '日志测试编译失败'}
 $run=Join-Path $PSScriptRoot ('artifacts\logging-validation-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
 & "$bin\DiagnosticsTests.exe" $run (Join-Path $PSScriptRoot 'projects')
 if($LASTEXITCODE -ne 0){throw '日志专项检查失败'}
 if($Native -or $Projects){
  $nativeRoot=Join-Path $run 'native'
  New-Item -ItemType Directory -Force $nativeRoot | Out-Null
  $nativeArgs=@('--integration-test','--logging-test','--data-dir',('"'+$nativeRoot+'"'))
  if($Projects){$nativeArgs+=@('--projects-test','--personal-site-test')}
  $process=Start-Process -FilePath (Join-Path $package '摸鱼盒子.exe') -ArgumentList $nativeArgs -WindowStyle Hidden -PassThru
  if(!$process.WaitForExit(240000)){throw '原生集成验证超时；请检查此隔离会话日志，不自动结束未知子进程'}
  $result=Get-Content -LiteralPath (Join-Path $nativeRoot 'integration-result.json') -Raw | ConvertFrom-Json
  if(!$result.ok){throw ('原生检查失败：'+$result.error)}
  & python tools\verify-host-logs.py $nativeRoot
  if($LASTEXITCODE -ne 0){throw '原生操作日志链验证失败'}
  Write-Output ('原生检查通过：'+$result.checks.Count)
 }
 Write-Output $run
} finally { Pop-Location }
