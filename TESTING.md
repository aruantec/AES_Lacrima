# Testing

This repository uses xUnit for unit tests, `coverlet.collector` for code coverage collection, and `ReportGenerator` for HTML coverage reports.

## Prerequisites

- .NET 10 SDK installed
- Run commands from the repository root unless noted otherwise

## Test Strategy

Use plain xUnit projects for pure logic such as services, helpers, models, and converters.

Use Avalonia headless tests for controls, bindings, focus/input behavior, and other UI scenarios that need an Avalonia application and UI thread.

## Test Project Layout

Test projects live at the repository root and must include `Tests` in the project name so the NUKE build can discover them automatically.

Planned initial layout:

- `AES_Core.Tests/`
- `AES_Lacrima.Headless.Tests/`

## Local Tooling

Coverage HTML reports should be generated with a repo-local .NET tool manifest.

Restore local tools with:

```bash
dotnet tool restore
```

## Running Tests

Run all tests:

```bash
dotnet test AES_Lacrima.sln
```

Run a specific unit test project:

```bash
dotnet test AES_Core.Tests/AES_Core.Tests.csproj
```

Run a specific Avalonia headless test project:

```bash
dotnet test AES_Lacrima.Headless.Tests/AES_Lacrima.Headless.Tests.csproj
```

## Collecting Coverage

Run tests with XPlat code coverage enabled and store results under `output/test-results`:

```bash
dotnet test AES_Lacrima.sln --collect:"XPlat Code Coverage" --results-directory output/test-results
```

## Generating HTML Coverage Reports

Generate the HTML report from collected Cobertura output:

```bash
dotnet tool run reportgenerator -reports:"output/test-results/**/coverage.cobertura.xml" -targetdir:"output/test-results/coverage-report" -reporttypes:"Html"
```

Open the generated report at:

- `output/test-results/coverage-report/index.html`

## Avalonia Headless Testing

For Avalonia-specific tests, prefer `Avalonia.Headless.XUnit` and use `[AvaloniaFact]` or `[AvaloniaTheory]` instead of plain `[Fact]` or `[Theory]`.

Headless tests should use a small test-only `Application` with a theme such as `FluentTheme`. Avoid using the production app startup path when a lightweight harness is enough.

Good example scenarios:

- typing into a `TextBox` with `window.KeyTextInput(...)`
- clicking a `Button` with `MouseDown(...)` and `MouseUp(...)`
- testing bindings, focus, and command execution with a shown `Window`

## Output Locations

- Raw test results: `output/test-results/`
- Coverage HTML report: `output/test-results/coverage-report/`

## NUKE Integration Notes

The NUKE build discovers test projects by searching for `*.csproj` paths containing `Tests`.

Important notes:

- test project names must include `Tests`
- test projects must also be added to `AES_Lacrima.sln`
- the current NUKE `Test` target runs tests with `--no-build --no-restore`, so solution integration is required

## Initial Rollout

1. Add `AES_Core.Tests` for plain xUnit coverage.
2. Add a repo-local `ReportGenerator` tool manifest.
3. Add `AES_Lacrima.Headless.Tests` with one or two basic Avalonia examples.
4. Update the NUKE `Test` target to emit coverage and HTML reports.
5. Wire the same flow into CI.
