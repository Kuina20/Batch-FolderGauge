param([string]$OutputDirectory = (Join-Path $PSScriptRoot "../artifacts/windows-x64"))
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "../src/BatchFolderGauge.App/BatchFolderGauge.App.csproj"
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码：$LASTEXITCODE" }
Copy-Item (Join-Path $PSScriptRoot "../README.md") (Join-Path $OutputDirectory "README.md")
$docsDirectory = Join-Path $OutputDirectory "docs"
New-Item -ItemType Directory -Path $docsDirectory -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot "../docs/WINDOWS-ACCEPTANCE.md") (Join-Path $docsDirectory "WINDOWS-ACCEPTANCE.md")
$archivePath = Join-Path (Split-Path $OutputDirectory -Parent) "BatchFolderGauge-windows-x64.zip"
Compress-Archive -Path (Join-Path $OutputDirectory "*") -DestinationPath $archivePath -Force
Write-Host "发布完成：$archivePath"
