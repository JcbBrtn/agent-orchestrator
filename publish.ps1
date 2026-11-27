$solutionFile = "AgentOrchestrator.sln"
$destination = "C:\Program Files\AgentOrchestrator"

Write-Host "Starting build for $solutionFile..." -ForegroundColor Cyan

# Run dotnet build
dotnet build $solutionFile
$exitCode = $LAST_EXIT_CODE

if ($exitCode -gt 0) {
    Write-Error "Dotnet build failed with exit code $exitCode."
    exit $exitCode
}

Write-Host "Build successful. Copying output to $destination..." -ForegroundColor Cyan

# Find the output directory. We'll look for bin directories that contain executables.
# This should handle different configurations (Debug/Release) and frameworks (netX.X).
$binPaths = Get-ChildItem -Path "src\AgentOrchestrator.App\bin" -Recurse -Directory | Where-Object { $_.Name -like "*Debug*" -or $_.Name -like "*Release*" }

if ($null -eq $binPaths -or $binPaths.Count -eq 0) {
    Write-Host "No specific bin directories found in src\AgentOrchestrator.App\bin. Falling back to the base bin folder." -ForegroundColor Yellow
    $source = "src\AgentOrchestrator.App\bin"
} else {
    # For simplicity, we take the first bin directory found.
    $source = $binPaths[0].FullName
}

# Ensure destination directory exists
if (-not (Test-Path $destination)) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
}

try {
    Copy-Item -Path "$source\*" -Destination $destination -Recurse -Force -ErrorAction Stop
    Write-Host "Successfully copied output from $source to $destination." -ForegroundColor Green
}
catch {
    Write-Error "Failed to copy files from $source to $destination. Ensure you are running as Administrator."
    exit 1
}

Write-Host "Done!" -ForegroundColor Green
