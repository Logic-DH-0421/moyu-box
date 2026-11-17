$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try{
 & node --test tests/core.test.js
 if($LASTEXITCODE -ne 0){throw 'Shell core tests failed'}
 & python -m unittest discover -s tests -p test_adapter.py -v
 if($LASTEXITCODE -ne 0){throw 'Adapter tests failed'}
 New-Item -ItemType Directory -Force build | Out-Null
 & 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /out:build\RegistryTests.exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll src\Host\Models.cs src\Host\Diagnostics.cs tests\RegistryTests.cs
 if($LASTEXITCODE -ne 0){throw 'Registry test compilation failed'}
 & .\build\RegistryTests.exe (Join-Path $PSScriptRoot 'projects') (Join-Path $PSScriptRoot 'artifacts')
 if($LASTEXITCODE -ne 0){throw 'Registry tests failed'}
 $package=Join-Path $PSScriptRoot 'release\MoyuBox-0.8.0'
 if(!(Test-Path "$package\摸鱼盒子.exe")){throw 'Build 0.8.0 before running compatibility checks'}
 & 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /out:build\CompatibilityFixTests.exe /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ("/reference:"+(Join-Path $package '摸鱼盒子.exe')) tests\CompatibilityFixTests.cs
 if($LASTEXITCODE -ne 0){throw 'Compatibility check compilation failed'}
 Copy-Item -LiteralPath (Join-Path $package '摸鱼盒子.exe') -Destination build -Force
 & .\build\CompatibilityFixTests.exe artifacts
 if($LASTEXITCODE -ne 0){throw 'Compatibility checks failed'}
}finally{Pop-Location}
