param(
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot "artifacts\local-deploy"
}

$ApiProject = Join-Path $RepoRoot "src\Tym.Api\Tym.Api.csproj"
$TestProject = Join-Path $RepoRoot "tests\Tym.Api.Tests\Tym.Api.Tests.csproj"
$UiRoot = Join-Path $RepoRoot "src\Tym.Ui"
$ApiOutput = Join-Path $OutputRoot "Tym.Api"
$UiOutput = Join-Path $OutputRoot "Tym.Ui"

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

Write-Host "Restoring and testing API..."
Invoke-Native "dotnet" @("restore", $TestProject)
Invoke-Native "dotnet" @("build", $TestProject, "--configuration", $Configuration, "--no-restore")
Invoke-Native "dotnet" @("run", "--project", $TestProject, "--configuration", $Configuration, "--no-build")

Write-Host "Building React UI..."
Push-Location $UiRoot
try {
    Invoke-Native "npm.cmd" @("install")
    Invoke-Native "npm.cmd" @("run", "build")
}
finally {
    Pop-Location
}

Write-Host "Publishing API..."
New-Item -ItemType Directory -Force -Path $ApiOutput, $UiOutput | Out-Null
Invoke-Native "dotnet" @("publish", $ApiProject, "--configuration", $Configuration, "--output", $ApiOutput)

Write-Host "Copying UI dist..."
Copy-Item -LiteralPath (Join-Path $UiRoot "dist\*") -Destination $UiOutput -Recurse -Force

Write-Host ""
Write-Host "Local deployment written to:"
Write-Host "  API: $ApiOutput"
Write-Host "  UI:  $UiOutput"
Write-Host ""
Write-Host "Run API with:"
Write-Host "  dotnet `"$ApiOutput\Tym.Api.dll`""
