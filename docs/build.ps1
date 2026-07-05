[CmdletBinding()]
param(
    [string] $HighlighterProject,
    [switch] $SkipToolRestore,
    [switch] $SkipHighlighter
)

$ErrorActionPreference = "Stop"

$DocsRoot = $PSScriptRoot
$RepoRoot = Resolve-Path -LiteralPath (Join-Path $DocsRoot "..")
$SitePath = Join-Path $DocsRoot "_site"
$ProjectPath = Join-Path $RepoRoot "Plank/Plank.csproj"
$ReportPath = Join-Path $RepoRoot "artifacts/docs/semantic-highlight-report.json"

function Invoke-DotNet {
    dotnet @args

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $args failed with exit code $LASTEXITCODE."
    }
}

function Resolve-HighlighterProject {
    $Candidates = @()

    if (-not [string]::IsNullOrWhiteSpace($HighlighterProject)) {
        $Candidates += $HighlighterProject
    }

    if (-not [string]::IsNullOrWhiteSpace($env:DOCFX_ROSLYN_HIGHLIGHT_PROJECT)) {
        $Candidates += $env:DOCFX_ROSLYN_HIGHLIGHT_PROJECT
    }

    $Candidates += Join-Path $RepoRoot "../../docfx-roslyn-highlight/src/Docfx.RoslynHighlight/Docfx.RoslynHighlight.csproj"
    $Candidates += Join-Path $RepoRoot ".tools/docfx-roslyn-highlight/src/Docfx.RoslynHighlight/Docfx.RoslynHighlight.csproj"

    foreach ($Candidate in $Candidates) {
        if (Test-Path -LiteralPath $Candidate) {
            return (Resolve-Path -LiteralPath $Candidate).Path
        }
    }

    return $null
}

Push-Location $RepoRoot

try {
    if (-not $SkipToolRestore) {
        Invoke-DotNet tool restore
    }

    Invoke-DotNet docfx docs/docfx.json

    if ($SkipHighlighter) {
        return
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null

    $ResolvedHighlighterProject = Resolve-HighlighterProject

    if ($ResolvedHighlighterProject) {
        dotnet run --project $ResolvedHighlighterProject -- html `
            --site $SitePath `
            --project $ProjectPath `
            --css-mode external `
            --theme auto `
            --report $ReportPath

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet run --project $ResolvedHighlighterProject failed with exit code $LASTEXITCODE."
        }

        return
    }

    $HighlighterCommand = Get-Command docfx-roslyn-highlight -ErrorAction SilentlyContinue

    if ($HighlighterCommand) {
        & $HighlighterCommand.Source html `
            --site $SitePath `
            --project $ProjectPath `
            --css-mode external `
            --theme auto `
            --report $ReportPath

        if ($LASTEXITCODE -ne 0) {
            throw "docfx-roslyn-highlight failed with exit code $LASTEXITCODE."
        }

        return
    }

    throw "Docfx.RoslynHighlight was not found. Clone https://github.com/Kuinox/docfx-roslyn-highlight into .tools/docfx-roslyn-highlight, set DOCFX_ROSLYN_HIGHLIGHT_PROJECT, or install the docfx-roslyn-highlight tool."
}
finally {
    Pop-Location
}
