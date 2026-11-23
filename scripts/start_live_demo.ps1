param(
    [string]$Strategy = "known_tasks",
    [string]$ModelPath = "",
    [string]$ExePath = "",
    [string]$RepoPath = "",
    [int]$Port = 8000,
    [switch]$BackendOnly,
    [string]$ExtraUnityArgs = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-RepoRoot {
    param([string]$Requested)
    $candidates = @(
        $Requested,
        (Split-Path -Parent $scriptDir),
        $scriptDir,
        "G:\Engineering\Ethan-CQSeven-team-project"
    )
    foreach ($cand in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($cand) -and (Test-Path (Join-Path $cand "planner_service\app.py"))) {
            return (Resolve-Path $cand).Path
        }
    }
    throw "Could not find planner_service\app.py. Pass -RepoPath <repo-root>."
}

function Resolve-ModelFile {
    param([string]$Requested, [string]$Repo)
    $candidates = @(
        $Requested,
        $env:PLANNER_ONNX_MODEL,
        (Join-Path $Repo "model\classifier.onnx"),
        (Join-Path $scriptDir "model\classifier.onnx"),
        (Join-Path (Split-Path -Parent $Repo) "model\classifier.onnx"),
        "G:\Engineering\Ethan-CQSeven-team-project\model\classifier.onnx"
    )
    foreach ($cand in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($cand) -and (Test-Path $cand)) {
            return (Resolve-Path $cand).Path
        }
    }
    throw "Could not find ONNX model classifier.onnx. Pass -ModelPath or set PLANNER_ONNX_MODEL."
}

function Resolve-PythonSpec {
    param([string]$Repo)
    $checkCode = "import fastapi, uvicorn, pydantic, onnxruntime, PIL, numpy"
    $venvPy = Join-Path $Repo ".venv\Scripts\python.exe"
    $specs = New-Object System.Collections.Generic.List[object]
    if (Test-Path $venvPy) {
        $specs.Add(@{ Exe = $venvPy; Prefix = @() })
    }
    $pyLauncher = Get-Command py -ErrorAction SilentlyContinue
    if ($pyLauncher) {
        foreach ($ver in @("-3.12", "-3.11", "-3.10")) {
            $specs.Add(@{ Exe = $pyLauncher.Source; Prefix = @($ver) })
        }
    }
    $plainPy = Get-Command python -ErrorAction SilentlyContinue
    if ($plainPy) {
        $specs.Add(@{ Exe = $plainPy.Source; Prefix = @() })
    }

    foreach ($spec in $specs) {
        try {
            $argList = @() + $spec.Prefix + @("-c", $checkCode)
            & $spec.Exe @argList *> $null
            if ($LASTEXITCODE -eq 0) {
                return $spec
            }
        }
        catch {
        }
    }
    throw "No Python runtime found with fastapi, uvicorn, pydantic, onnxruntime, Pillow, numpy."
}

function Resolve-UnityExe {
    param([string]$Requested, [string]$Repo)
    $candidates = @(
        $Requested,
        (Join-Path $scriptDir "MazeRobot\MazeRobot.exe"),
        (Join-Path (Split-Path -Parent $scriptDir) "MazeRobot\MazeRobot.exe"),
        (Join-Path (Split-Path -Parent $Repo) "MazeRobot_Release\MazeRobot\MazeRobot.exe"),
        "G:\Engineering\MazeRobot_Release\MazeRobot\MazeRobot.exe"
    )
    foreach ($cand in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($cand) -and (Test-Path $cand)) {
            return (Resolve-Path $cand).Path
        }
    }
    throw "Could not find MazeRobot.exe. Pass -ExePath <path-to-MazeRobot.exe>."
}

function Test-HealthDirect {
    param([hashtable]$PySpec, [int]$TargetPort)
    $probe = "import sys, urllib.request; opener = urllib.request.build_opener(urllib.request.ProxyHandler({})); sys.exit(0 if opener.open('http://127.0.0.1:$TargetPort/health', timeout=2).status == 200 else 1)"
    $argList = @() + $PySpec.Prefix + @("-c", $probe)
    try {
        & $PySpec.Exe @argList *> $null
        return ($LASTEXITCODE -eq 0)
    }
    catch {
        return $false
    }
}

$resolvedRepo = Resolve-RepoRoot -Requested $RepoPath
$resolvedModel = Resolve-ModelFile -Requested $ModelPath -Repo $resolvedRepo
$pythonSpec = Resolve-PythonSpec -Repo $resolvedRepo

$runnerScript = Join-Path $scriptDir "run_backend.py"
if (-not (Test-Path $runnerScript)) {
    $runnerScript = Join-Path $resolvedRepo "scripts\run_backend.py"
}
if (-not (Test-Path $runnerScript)) {
    throw "Could not find run_backend.py beside start_live_demo.ps1 or under $resolvedRepo\scripts."
}

$logOut = Join-Path ([System.IO.Path]::GetTempPath()) "mazerobot_backend_stdout.log"
$logErr = Join-Path ([System.IO.Path]::GetTempPath()) "mazerobot_backend_stderr.log"

$prevExploration = $env:PLANNER_EXPLORATION
$prevModel = $env:PLANNER_ONNX_MODEL
$prevNoProxy = $env:NO_PROXY
$prevNoProxyLower = $env:no_proxy

$backendProc = $null
try {
    $env:PLANNER_EXPLORATION = $Strategy
    $env:PLANNER_ONNX_MODEL = $resolvedModel

    $backendArgs = @() + $pythonSpec.Prefix + @(
        "`"$runnerScript`"",
        "--repo", "`"$resolvedRepo`"",
        "--host", "127.0.0.1",
        "--port", "$Port"
    )

    Write-Host "[LiveDemo] Starting planner backend..."
    Write-Host "[LiveDemo]   Repo:        $resolvedRepo"
    Write-Host "[LiveDemo]   Model:       $resolvedModel"
    Write-Host "[LiveDemo]   Exploration: $Strategy"
    Write-Host "[LiveDemo]   Endpoint:    http://127.0.0.1:$Port"

    $backendProc = Start-Process -FilePath $pythonSpec.Exe -ArgumentList $backendArgs -RedirectStandardOutput $logOut -RedirectStandardError $logErr -WindowStyle Hidden -PassThru

    $ready = $false
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        if ($backendProc.HasExited) {
            $errText = if (Test-Path $logErr) { Get-Content $logErr -Raw } else { "" }
            throw "Backend process exited early with code $($backendProc.ExitCode).`n$errText"
        }
        if (Test-HealthDirect -PySpec $pythonSpec -TargetPort $Port) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 400
    }

    if (-not $ready) {
        $errText = if (Test-Path $logErr) { Get-Content $logErr -Raw } else { "" }
        throw "Timed out waiting for http://127.0.0.1:$Port/health.`n$errText"
    }

    Write-Host "[LiveDemo] Backend is READY (PID $($backendProc.Id))."

    if ($BackendOnly) {
        Write-Host "[LiveDemo] BackendOnly mode active. Press Ctrl+C or stop PID $($backendProc.Id) to exit."
        Wait-Process -Id $backendProc.Id
        exit 0
    }

    $resolvedExe = Resolve-UnityExe -Requested $ExePath -Repo $resolvedRepo
    $env:NO_PROXY = "127.0.0.1,localhost"
    $env:no_proxy = "127.0.0.1,localhost"

    Write-Host "[LiveDemo] Launching MazeRobot.exe (NO_PROXY=127.0.0.1,localhost)..."
    Write-Host "[LiveDemo]   EXE: $resolvedExe"
    if ([string]::IsNullOrWhiteSpace($ExtraUnityArgs)) {
        $unityProc = Start-Process -FilePath $resolvedExe -Wait -PassThru
    }
    else {
        $unityProc = Start-Process -FilePath $resolvedExe -ArgumentList $ExtraUnityArgs -Wait -PassThru
    }

    Write-Host "[LiveDemo] MazeRobot.exe exited with code $($unityProc.ExitCode)."
    exit $unityProc.ExitCode
}
finally {
    if ($null -ne $backendProc -and -not $backendProc.HasExited) {
        Write-Host "[LiveDemo] Stopping backend (PID $($backendProc.Id))..."
        Stop-Process -Id $backendProc.Id -Force -ErrorAction SilentlyContinue
    }
    $env:PLANNER_EXPLORATION = $prevExploration
    $env:PLANNER_ONNX_MODEL = $prevModel
    $env:NO_PROXY = $prevNoProxy
    $env:no_proxy = $prevNoProxyLower
}
