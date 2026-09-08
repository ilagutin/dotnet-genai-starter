param(
    [int] $MaxProductionFileLines = 400,
    [int] $MaxTestFileLines = 800,
    [int] $MaxLogicalTypeLines = 400,
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path -LiteralPath $RepositoryRoot
$sourceRoots = @(
    [pscustomobject]@{ Path = Join-Path $repoRoot "src"; Kind = "production" },
    [pscustomobject]@{ Path = Join-Path $repoRoot "tests"; Kind = "test" }
)
$transitionalTestLineLimits = @{
    "tests/GenAIPlatform.UnitTests/DocumentIngestionTests.cs" = 2566
    "tests/GenAIPlatform.IntegrationTests/PostgresRagVectorSearchStoreTests.cs" = 1128
    "tests/GenAIPlatform.IntegrationTests/ApiV1EndpointTests.cs" = 1110
    "tests/GenAIPlatform.UnitTests/AgenticChatHandlerTests.cs" = 1063
    "tests/GenAIPlatform.UnitTests/RagChatHandlerTests.cs" = 1026
    "tests/GenAIPlatform.UnitTests/EvaluationRunHandlerTests.cs" = 956
    "tests/GenAIPlatform.UnitTests/ExternalAgenticToolGovernanceTests.cs" = 928
    "tests/GenAIPlatform.IntegrationTests/DocumentEndpointTests.cs" = 906
    "tests/GenAIPlatform.IntegrationTests/PostgresDocumentIngestionRepositoryTests.cs" = 833
}
$generatedFilePattern = '(?i)(\.g|\.g\.i|\.designer|\.generated)\.cs$'
$statusStringPattern = '"(Passed|Succeeded|Failed|Running|Canceled|TimedOut|Rejected|ValidationFailed|ApprovalRequired|NotExecuted|NotRequired|SimulatedApproved|Valid|Invalid)"'
$typePattern = "(?m)^\s*(?:(?:public|internal|private|protected)\s+)?(?:(?:sealed|abstract|static|partial|readonly)\s+)*(?:class|record|struct|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)"

function Get-RelativePath {
    param([string] $Path)

    $rootPath = $repoRoot.Path.TrimEnd('\', '/')
    return $Path.Substring($rootPath.Length).TrimStart('\', '/').Replace('\', '/')
}

function Get-FileLineCount {
    param([string] $Path)

    return (Get-Content -LiteralPath $Path).Count
}

function Get-FileNamespace {
    param([string] $Text)

    $namespaceMatch = [regex]::Match($Text, "(?m)^\s*namespace\s+([^;\r\n{]+)")
    if ($namespaceMatch.Success) {
        return $namespaceMatch.Groups[1].Value.Trim()
    }

    return "<global>"
}

function Get-AuthoredCSharpFiles {
    param([string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        return @()
    }

    return Get-ChildItem -LiteralPath $Root -Recurse -Filter "*.cs" |
        Where-Object {
            $_.FullName -notmatch "[/\\]bin[/\\]" -and
            $_.FullName -notmatch "[/\\]obj[/\\]" -and
            $_.FullName -notmatch "[/\\]TestResults[/\\]" -and
            $_.Name -notmatch $generatedFilePattern
        }
}

$findings = New-Object System.Collections.Generic.List[object]
$typeFiles = @{}
$scannedFiles = New-Object System.Collections.Generic.List[object]

foreach ($sourceRoot in $sourceRoots) {
    foreach ($file in Get-AuthoredCSharpFiles $sourceRoot.Path) {
        $text = Get-Content -Raw -LiteralPath $file.FullName
        $lineCount = Get-FileLineCount $file.FullName
        $relativePath = Get-RelativePath $file.FullName
        $scannedFiles.Add([pscustomobject]@{
            File = $relativePath
            Kind = $sourceRoot.Kind
            Lines = $lineCount
        })

        $limit = if ($sourceRoot.Kind -eq "production") {
            $MaxProductionFileLines
        }
        elseif ($transitionalTestLineLimits.ContainsKey($relativePath)) {
            $transitionalTestLineLimits[$relativePath]
        }
        else {
            $MaxTestFileLines
        }

        if ($lineCount -gt $limit) {
            $findings.Add([pscustomobject]@{
                Rule = "file-lines"
                File = $relativePath
                Type = ""
                Line = ""
                Detail = "$lineCount lines; limit $limit"
            })
        }

        if ($sourceRoot.Kind -ne "production") {
            continue
        }

        $namespace = Get-FileNamespace $text
        foreach ($match in [regex]::Matches($text, $typePattern)) {
            $fullTypeName = "$namespace.$($match.Groups[1].Value)"
            if (-not $typeFiles.ContainsKey($fullTypeName)) {
                $typeFiles[$fullTypeName] = New-Object System.Collections.Generic.List[object]
            }

            $typeFiles[$fullTypeName].Add([pscustomobject]@{
                File = $relativePath
                Lines = $lineCount
            })
        }

        $statusMatches = Select-String -LiteralPath $file.FullName -Pattern $statusStringPattern -AllMatches
        foreach ($statusMatch in $statusMatches) {
            foreach ($match in $statusMatch.Matches) {
                $findings.Add([pscustomobject]@{
                    Rule = "status-string-candidate"
                    File = $relativePath
                    Type = ""
                    Line = $statusMatch.LineNumber
                    Detail = $match.Value
                })
            }
        }
    }
}

foreach ($entry in $typeFiles.GetEnumerator()) {
    $totalLines = ($entry.Value | Measure-Object Lines -Sum).Sum
    if ($totalLines -gt $MaxLogicalTypeLines) {
        $findings.Add([pscustomobject]@{
            Rule = "logical-type-lines"
            File = ($entry.Value.File -join "; ")
            Type = $entry.Key
            Line = ""
            Detail = "$totalLines aggregate lines; limit $MaxLogicalTypeLines"
        })
    }
}

Write-Output "Code organization gate: scanned $($scannedFiles.Count) authored C# file(s): $(@($scannedFiles | Where-Object Kind -eq 'production').Count) production, $(@($scannedFiles | Where-Object Kind -eq 'test').Count) test."
Write-Output "Code organization gate: transitional test debt (remove in BL-028):"
foreach ($path in $transitionalTestLineLimits.Keys | Sort-Object) {
    Write-Output "  ${path}: ceiling $($transitionalTestLineLimits[$path]) lines"
}

if ($findings.Count -eq 0) {
    Write-Output "Code organization gate: no findings."
    exit 0
}

Write-Output "Code organization gate: $($findings.Count) finding(s)."
$findings |
    Sort-Object Rule, File, Type, Line |
    Format-Table Rule, File, Type, Line, Detail -AutoSize -Wrap

exit 1
