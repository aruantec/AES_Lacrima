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

Current layout:

- `AES_Controls.Tests/`
- `AES_Core.Tests/`
- `AES_Lacrima.Tests/`
- `AES_Lacrima.Headless.Tests/`

## Local Tooling

Coverage HTML reports are generated with a repo-local .NET tool manifest stored in `.config/dotnet-tools.json`.

Restore local tools with:

```bash
dotnet tool restore
```

## Running Tests

Run all tests:

```bash
dotnet test AES_Lacrima.sln
```

Run the NUKE test target, including coverage collection and report generation:

```bash
./build.sh Test --configuration Debug
```

Run a specific unit test project:

```bash
dotnet test AES_Core.Tests/AES_Core.Tests.csproj
```

Run a specific Avalonia headless test project:

```bash
dotnet test AES_Lacrima.Headless.Tests/AES_Lacrima.Headless.Tests.csproj
```

Run the converter-focused Avalonia project tests:

```bash
dotnet test AES_Lacrima.Tests/AES_Lacrima.Tests.csproj
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

Reference examples in this repository:

- `AES_Core.Tests/SettingsServiceTests.cs`
- `AES_Controls.Tests/SimpleObserverTests.cs`
- `AES_Controls.Tests/YouTubeThumbnailTests.cs`
- `AES_Lacrima.Tests/ConverterTests.cs`
- `AES_Lacrima.Headless.Tests/TextBoxInputTests.cs`
- `AES_Lacrima.Headless.Tests/ButtonInteractionTests.cs`

## Output Locations

- Raw test results: `output/test-results/`
- Coverage HTML report: `output/test-results/coverage-report/`

## NUKE Integration Notes

The NUKE build discovers test projects by searching for `*.csproj` paths containing `Tests`.

Important notes:

- test project names must include `Tests`
- test projects must also be added to `AES_Lacrima.sln`
- the current NUKE `Test` target runs tests with `--no-build --no-restore`, so solution integration is required
- the current NUKE `Test` target restores local tools, collects coverage, and generates the HTML report automatically

## Initial Rollout

1. Expand `AES_Core.Tests` with additional service and helper coverage.
2. Expand `AES_Lacrima.Headless.Tests` with control, binding, and interaction scenarios.
3. Add more test projects for other assemblies as needed.
4. Keep the NUKE and CI test flow aligned with local commands.

## Suggested Next Steps

These are the most natural follow-up areas if test coverage continues after this initial PR:

1. Add more `AES_Controls.Tests` coverage for helper edge cases such as additional `FFmpegLocator` scenarios and more `YouTubeThumbnail` parsing inputs.
2. Expand `AES_Lacrima.Headless.Tests` with more Avalonia binding, focus, and command interaction tests.
3. Add file-backed settings coverage for types such as `SettingsBase` and related persistence logic.
