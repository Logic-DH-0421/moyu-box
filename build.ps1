param([switch]$RefreshIntegrations,[switch]$SkipAdapter,[switch]$Zip,[string]$PythonPackages='',[string]$ServicePython='python')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
if($RefreshIntegrations){& $ServicePython (Join-Path $root 'tools\prepare-integrations.py');if($LASTEXITCODE -ne 0){throw 'Integration refresh failed'};$SkipAdapter=$false}
$out=Join-Path $root 'release\MoyuBox-0.8.1'
$work=Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $out,$work,(Join-Path $out 'adapters'),(Join-Path $out 'plugins') | Out-Null
Get-ChildItem (Join-Path $root 'src\Shell') -Filter '*.js' | ForEach-Object { & node --check $_.FullName; if($LASTEXITCODE -ne 0){throw 'JavaScript syntax validation failed'} }
foreach($pair in @(@('src\Shell','shell'),@('projects','projects'),@('vendor\multiai\web','plugins\multiai\web'),@('vendor\shijie','plugins\shijie'),@('vendor\bilibili\bilibili_cache_manager\web','plugins\bilibili'))){
 $dest=Join-Path $out $pair[1]
 New-Item -ItemType Directory -Force -Path $dest | Out-Null
 Copy-Item -Path (Join-Path (Join-Path $root $pair[0]) '*') -Destination $dest -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $root 'src\MultiAI\moyu-adapter.css') -Destination (Join-Path $out 'plugins\multiai\web\moyu-adapter.css') -Force
$multiHtml=Join-Path $out 'plugins\multiai\web\index.html'
$multiSource=[System.IO.File]::ReadAllText($multiHtml)
$multiSource=$multiSource.Replace('</head>','<link rel="stylesheet" href="moyu-adapter.css"></head>')
[System.IO.File]::WriteAllText($multiHtml,$multiSource,[System.Text.UTF8Encoding]::new($false))
& $ServicePython (Join-Path $root 'tools\prepare-word-reader.py') (Join-Path $out 'plugins\word-reader')
if($LASTEXITCODE -ne 0){throw 'Word reader preparation failed'}
Get-ChildItem (Join-Path $root 'src\WordReader') -Filter '*.js' | ForEach-Object { & node --check $_.FullName; if($LASTEXITCODE -ne 0){throw 'Word reader syntax validation failed'} }
Copy-Item (Join-Path $root 'vendor\webview2\*.dll') -Destination $out -Force
& (Join-Path $root 'tools\make-icon.ps1') -Destination (Join-Path $out 'app.ico')
if(!$SkipAdapter -or !(Test-Path (Join-Path $out 'adapters\MoyuBiliService.exe'))){
 $priorPath=$env:PYTHONPATH
 $priorBytecode=$env:PYTHONDONTWRITEBYTECODE
 try{
  if($PythonPackages){$env:PYTHONPATH=$PythonPackages}
  $env:PYTHONDONTWRITEBYTECODE='1'
  & $ServicePython -m PyInstaller --noconfirm --onefile --noconsole --name MoyuBiliService --paths (Join-Path $root 'vendor\bilibili') --hidden-import websocket --exclude-module webview --exclude-module tkinter --distpath (Join-Path $out 'adapters') --workpath (Join-Path $work 'pyinstaller') --specpath $work (Join-Path $root 'adapters\bilibili\service.py')
  if($LASTEXITCODE -ne 0){throw 'Bilibili service build failed'}
 }finally{$env:PYTHONPATH=$priorPath;$env:PYTHONDONTWRITEBYTECODE=$priorBytecode}
}
if(!$SkipAdapter -or !(Test-Path (Join-Path $out 'adapters\MoyuProjectsService.exe'))){
 $servicePython=$ServicePython
 & $servicePython -m PyInstaller --noconfirm --onefile --noconsole --name MoyuProjectsService --paths (Join-Path $root 'vendor\multiai') --collect-all playwright --hidden-import playwright.async_api --distpath (Join-Path $out 'adapters') --workpath (Join-Path $work 'projects-service') --specpath $work (Join-Path $root 'adapters\projects\service.py')
 if($LASTEXITCODE -ne 0){throw 'Project services build failed'}
}
$reader=Join-Path $root 'vendor\reader' 
$readerProgram=Join-Path $work 'ReaderProgram.cs'
$source=Get-Content -LiteralPath (Join-Path $reader 'Program.cs') -Raw
$source=[regex]::Replace($source,'(?m)^\[assembly:.*\]\r?\n','')
[System.IO.File]::WriteAllText($readerProgram,$source,[System.Text.UTF8Encoding]::new($false))
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$argsList=@('/nologo','/target:winexe','/platform:x64','/optimize+','/main:MoyuBox.Bootstrap',('/out:'+(Join-Path $out '摸鱼盒子.exe')),('/win32icon:'+(Join-Path $out 'app.ico')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Xml.dll','/reference:System.Xml.Linq.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll',('/reference:'+(Join-Path $out 'Microsoft.Web.WebView2.Core.dll')),('/reference:'+(Join-Path $out 'Microsoft.Web.WebView2.WinForms.dll')))
foreach($file in @('index.html','styles.css','app.js')){$argsList+=('/resource:'+(Join-Path $reader $file)+',AiNovelReader.'+$file)}
$argsList+=('/win32manifest:'+(Join-Path $root 'src\Host\app.manifest'))
$argsList+=(Get-ChildItem (Join-Path $root 'src\Host') -Filter '*.cs' | ForEach-Object FullName)
$argsList+=@(Join-Path $reader 'StructuredEpubParser.cs';Join-Path $reader 'ContentPositionMapper.cs';Join-Path $reader 'TextStructure.cs';Join-Path $root 'src\ReaderService\LegacyTextParser.cs')
$argsList+=@($readerProgram,(Join-Path $reader 'BrowserHost.cs'),(Join-Path $reader 'EpubParser.cs'),(Join-Path $reader 'RuntimeLog.cs'))
& $compiler @argsList
if($LASTEXITCODE -ne 0){throw 'Native host build failed'}
$readerArgs=@('/nologo','/target:winexe','/platform:x64','/optimize+','/main:MoyuReaderService',('/out:'+(Join-Path $out 'adapters\MoyuReaderService.exe')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Xml.dll','/reference:System.Xml.Linq.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
foreach($file in @('index.html','styles.css','app.js')){$readerArgs+=('/resource:'+(Join-Path $reader $file)+',AiNovelReader.'+$file)}
$readerArgs+=@(Join-Path $reader 'StructuredEpubParser.cs';Join-Path $reader 'ContentPositionMapper.cs';Join-Path $reader 'TextStructure.cs';Join-Path $root 'src\ReaderService\LegacyTextParser.cs')
$readerArgs+=@((Join-Path $reader 'Program.cs'),(Join-Path $reader 'BrowserHost.cs'),(Join-Path $reader 'EpubParser.cs'),(Join-Path $reader 'RuntimeLog.cs'),(Join-Path $root 'src\ReaderService\Program.cs'))
& $compiler @readerArgs
if($LASTEXITCODE -ne 0){throw 'Reader service build failed'}
Copy-Item -LiteralPath (Join-Path $root 'src\Host\App.config') -Destination (Join-Path $out 'adapters\MoyuReaderService.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $root 'src\Host\App.config') -Destination (Join-Path $out '摸鱼盒子.exe.config') -Force
foreach($file in @('README.md','THIRD-PARTY-NOTICES.md')){if(Test-Path (Join-Path $root $file)){Copy-Item -LiteralPath (Join-Path $root $file) -Destination $out -Force}}
foreach($folder in @('docs','examples','licenses')){if(Test-Path (Join-Path $root $folder)){New-Item -ItemType Directory -Force -Path (Join-Path $out $folder) | Out-Null; Copy-Item -Path (Join-Path (Join-Path $root $folder) '*') -Destination (Join-Path $out $folder) -Recurse -Force}}
if($Zip){Compress-Archive -LiteralPath $out -DestinationPath (Join-Path $root 'release\MoyuBox-0.8.1-win-x64.zip') -Force}
Write-Output (Join-Path $out '摸鱼盒子.exe')
