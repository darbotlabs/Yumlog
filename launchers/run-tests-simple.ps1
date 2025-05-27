# Simple test script that uses the original Pester 3.4 format without configurable verbosity

# Get the path to the tests
$testPath = Join-Path $PSScriptRoot '..\Skills\Test-ScreenSkills.Tests.ps1'

# Display information about what we're doing
Write-Host "Running tests from: $testPath" 
Write-Host "Using Pester version: $((Get-Module Pester -ListAvailable)[0].Version)"

# Actually run the tests using a simpler command that's compatible with Pester 3.x
Invoke-Pester -Path $testPath
