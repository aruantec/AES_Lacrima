# Artifact Generation Notes

- Running `./build.sh Publish --configuration Release` produces binaries in `output/publish/Release/` plus the platform-specific `win-x64`, `linux-x64`, etc., folders under each project.
- Coverage reports and HTML artifacts are written by the test target to `output/test-results/coverage-report/`; the repository now includes `coverage-report.zip` for quick inspection.
- CI workflows automatically run the same commands on Linux (via `act`/GitHub Actions) and will run the Windows/macOS packaging jobs on their respective runners.
