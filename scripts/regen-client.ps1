#!/usr/bin/env pwsh
# Regenerates the Kiota ClickUp API client from the curated OpenAPI spec.
# Run from the repo root. Generated code lands in src/ClickUpTodo/ClickUp/Generated and must NOT be
# hand-edited — change src/ClickUpTodo/ClickUp/clickup-openapi.json (or this script) and regenerate.
#
# Requires the local Kiota tool: run `dotnet tool restore` first.

$ErrorActionPreference = 'Stop'

$spec = 'src/ClickUpTodo/ClickUp/clickup-openapi.json'
$out  = 'src/ClickUpTodo/ClickUp/Generated'

dotnet kiota generate `
    --language CSharp `
    --openapi $spec `
    --class-name ClickUpApiClient `
    --namespace-name ClickUpTodo.ClickUp.Generated `
    --output $out `
    --clean-output `
    --exclude-backward-compatible

Write-Host 'Kiota client regenerated.' -ForegroundColor Green
