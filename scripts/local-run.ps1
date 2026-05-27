param(
    [string]$ApiUrl = "http://localhost:5000",
    [int]$UiPort = 5173
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ApiProject = Join-Path $RepoRoot "src\Tym.Api\Tym.Api.csproj"
$UiRoot = Join-Path $RepoRoot "src\Tym.Ui"

function Invoke-Native {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Starting TYM API on $ApiUrl..."
$apiArgs = @("run", "--project", $ApiProject, "--urls", $ApiUrl)
$apiProcess = Start-Process -FilePath "dotnet" -ArgumentList $apiArgs -PassThru

try {
    Push-Location $UiRoot
    try {
        if (!(Test-Path -LiteralPath "node_modules")) {
            Invoke-Native "npm.cmd" @("install")
        }

        $env:VITE_TYM_API_URL = $ApiUrl
        Write-Host "Starting React UI on http://127.0.0.1:$UiPort..."
        Invoke-Native "npm.cmd" @("run", "dev", "--", "--host", "127.0.0.1", "--port", $UiPort.ToString())
    }
    finally {
        Pop-Location
    }
}
finally {
    if ($apiProcess -and !$apiProcess.HasExited) {
        Write-Host "Stopping TYM API..."
        Stop-Process -Id $apiProcess.Id
    }
}
