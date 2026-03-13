using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

sealed class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter]
    string Configuration { get; } = IsLocalBuild ? "Debug" : "Release";

    [Parameter("Optional runtime identifier for dotnet publish")]
    string? Runtime { get; }

    [Parameter("Publish as a self-contained app")]
    bool? SelfContained { get; }

    AbsolutePath SolutionFile => RootDirectory / "AES_Lacrima.sln";
    AbsolutePath AppProjectFile => RootDirectory / "AES_Lacrima" / "AES_Lacrima.csproj";
    AbsolutePath ArtifactsDirectory => RootDirectory / "output";
    AbsolutePath TestResultsDirectory => ArtifactsDirectory / "test-results";
    AbsolutePath CoverageReportDirectory => TestResultsDirectory / "coverage-report";
    AbsolutePath PublishDirectory => ArtifactsDirectory / "publish" / Configuration;
    string[] TestProjects => Directory
        .EnumerateFiles(RootDirectory, "*.csproj", SearchOption.AllDirectories)
        .Where(x => x.Contains("Tests", StringComparison.OrdinalIgnoreCase))
        .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    Target Clean => _ => _
        .Executes(() =>
        {
            if (Directory.Exists(ArtifactsDirectory))
                Directory.Delete(ArtifactsDirectory, recursive: true);

            Directory.CreateDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(SolutionFile));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(SolutionFile)
                .SetConfiguration(Configuration));
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            if (TestProjects.Length == 0)
            {
                Console.WriteLine("No test projects found. Skipping dotnet test.");
                return;
            }

            Directory.CreateDirectory(TestResultsDirectory);

            DotNet("tool restore", RootDirectory);

            foreach (var testProject in TestProjects)
            {
                DotNet($"test \"{testProject}\" --configuration {Configuration} --no-build --no-restore --results-directory \"{TestResultsDirectory}\" --collect:\"XPlat Code Coverage\"", RootDirectory);
            }

            var coverageFiles = Directory
                .EnumerateFiles(TestResultsDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories)
                .ToArray();

            if (coverageFiles.Length == 0)
            {
                Console.WriteLine("No coverage files were generated. Skipping report generation.");
                return;
            }

            var coveragePattern = Path.Combine(TestResultsDirectory, "**", "coverage.cobertura.xml")
                .Replace(Path.DirectorySeparatorChar, '/');
            var reportTarget = CoverageReportDirectory.ToString().Replace(Path.DirectorySeparatorChar, '/');

            DotNet($"tool run reportgenerator -- -reports:\"{coveragePattern}\" -targetdir:\"{reportTarget}\" -reporttypes:Html", RootDirectory);
        });

    Target Run => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetRun(s => s
                .SetProjectFile(AppProjectFile)
                .SetConfiguration(Configuration)
                .EnableNoBuild());
        });

    Target Publish => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var publishDirectory = string.IsNullOrWhiteSpace(Runtime)
                ? PublishDirectory
                : PublishDirectory / Runtime;

            DotNetPublish(s =>
            {
                var settings = s
                    .SetProject(AppProjectFile)
                    .SetConfiguration(Configuration)
                    .SetOutput(publishDirectory);

                if (!string.IsNullOrWhiteSpace(Runtime))
                    settings = settings.SetRuntime(Runtime);

                if (SelfContained.HasValue)
                    settings = settings.SetSelfContained(SelfContained.Value);

                return settings;
            });
        });
}
