$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$voiceRoot = Join-Path $repoRoot "lib\voice"
$pythonExe = Join-Path $voiceRoot "python-venv\Scripts\python.exe"
$whisperRoot = Join-Path $voiceRoot "whisper"
$piperVoice = Join-Path $voiceRoot "piper\en_US-hfc_female-medium.onnx"
$whisperWrapper = Join-Path $repoRoot "tools\voice\local_whisper_stt.py"
$piperWrapper = Join-Path $repoRoot "tools\voice\local_piper_tts.py"

$env:ALI_WHISPER_EXE = $pythonExe
$env:ALI_WHISPER_MODEL = $whisperRoot
# Push-to-talk clips currently default to no VAD, with no-speech confidence filtering.
# Add --vad-filter only after live tuning.
$env:ALI_WHISPER_ARGS = "`"$whisperWrapper`" --audio `"{audio}`" --model-root `"{model}`" --model-id small.en --output-base `"{outputBase}`""
$env:ALI_PIPER_EXE = $pythonExe
$env:ALI_PIPER_MODEL = $piperVoice
$env:ALI_PIPER_VOICE = "en_US-hfc_female-medium"
$env:ALI_PIPER_ARGS = "`"$piperWrapper`" --model `"{model}`" --output `"{output}`" --rate `"{rate}`""

Write-Host "Ali local voice environment configured for this PowerShell session."
