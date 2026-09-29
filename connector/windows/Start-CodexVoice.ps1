param(
    [string]$ModelsRoot = (Join-Path $env:LOCALAPPDATA 'CodexVoice\models')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\CodexVoice.exe'
$modelDirectory = (Resolve-Path -LiteralPath $ModelsRoot -ErrorAction Stop).Path

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Codex Voice is not built. Run dotnet build connector\windows\CodexVoice.csproj -c Release first.'
}
if (-not (Test-Path -LiteralPath (Join-Path $modelDirectory 'vosk-model-small-ru-0.22') -PathType Container)) {
    throw 'The Vosk model directory is missing from the selected models root.'
}
if (-not (Test-Path -LiteralPath (Join-Path $modelDirectory 'ggml-largev3turbo-q5_0.bin') -PathType Leaf)) {
    throw 'The Whisper model file is missing from the selected models root.'
}

$existing = @(Get-CimInstance Win32_Process -Filter "Name='CodexVoice.exe'" |
    Where-Object { $_.ExecutablePath -eq $exe })
if ($existing.Count -gt 0) {
    throw ('Codex Voice is already running from this build: PID ' +
        (($existing | ForEach-Object ProcessId) -join ', '))
}

$started = Start-Process -FilePath $exe -WorkingDirectory $modelDirectory -WindowStyle Hidden -PassThru
Write-Output ('CODEX_VOICE_STARTED PID=' + $started.Id)
