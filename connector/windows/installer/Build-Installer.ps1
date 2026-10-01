param(
    [Parameter(Mandatory = $true)]
    [string]$ModelsRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $repoRoot 'connector\windows\CodexVoice.csproj'
$script = Join-Path $PSScriptRoot 'CodexVoice.iss'
$models = (Resolve-Path -LiteralPath $ModelsRoot -ErrorAction Stop).Path
$whisper = Join-Path $models 'ggml-largev3turbo-q5_0.bin'
$vosk = Join-Path $models 'vosk-model-small-ru-0.22'

if (-not (Test-Path -LiteralPath $whisper -PathType Leaf) -or
    -not (Test-Path -LiteralPath $vosk -PathType Container)) {
    throw 'ModelsRoot must contain the Whisper weight file and Vosk model directory.'
}

$version = ([xml](Get-Content -LiteralPath $project -Raw -Encoding UTF8)).Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Version is missing from CodexVoice.csproj.' }

$compilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $compiler) { throw 'Inno Setup 6 compiler (ISCC.exe) was not found.' }

$dist = Join-Path $repoRoot 'dist\windows'
$publish = Join-Path $dist ('publish-' + $version + '-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $dist 'installer'
New-Item -ItemType Directory -Path $publish, $output -Force | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

& $compiler "/DPublishDir=$publish" "/DModelsRoot=$models" `
    "/DInstallerOutputDir=$output" "/DAppVersion=$version" $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }

$setup = Join-Path $output "CodexVoice-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Expected setup executable is missing.' }
$file = Get-Item -LiteralPath $setup
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
Write-Output "SETUP_PATH=$($file.FullName)"
Write-Output "SETUP_BYTES=$($file.Length)"
Write-Output "SETUP_SHA256=$hash"
