$ErrorActionPreference = 'Stop'
# A mandatory fixture must be a failing acceptance gate, not a silently passing optional test.
$previous = $env:HSK_REQUIRE_FINAL_SAMPLE
try {
    $env:HSK_REQUIRE_FINAL_SAMPLE = 'true'
    dotnet test (Join-Path $PSScriptRoot '../HousekeepingParser.sln') --filter 'FullyQualifiedName~Required_october_sample' --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'Final Opera sample acceptance has not passed.' }
} finally {
    $env:HSK_REQUIRE_FINAL_SAMPLE = $previous
}
