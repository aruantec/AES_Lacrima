using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tooling;
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

    AbsolutePath SolutionFile => RootDirectory / "AES_Lacrima.slnx";
    AbsolutePath AppProjectFile => RootDirectory / "AES_Lacrima" / "AES_Lacrima.csproj";
    AbsolutePath AndroidProjectFile => RootDirectory / "AES_Lacrima.Android" / "AES_Lacrima.Android.csproj";
    AbsolutePath ArtifactsDirectory => RootDirectory / "output";
    AbsolutePath TestResultsDirectory => ArtifactsDirectory / "test-results";
    AbsolutePath CoverageReportDirectory => TestResultsDirectory / "coverage-report";
    AbsolutePath PublishDirectory => ArtifactsDirectory / "publish" / Configuration;
    AbsolutePath TestsProjectFile => RootDirectory / "AES_Tests" / "AES_Tests.csproj";

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
            // Work around intermittent MSBuild restore graph failures on .NET 10 CI runners.
            DotNet($"restore \"{AppProjectFile}\" -m:1", RootDirectory);
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(AppProjectFile)
                .SetConfiguration(Configuration));
        });

    Target Test => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            if (!File.Exists(TestsProjectFile))
            {
                Console.WriteLine($"Tests project not found at {TestsProjectFile}. Skipping dotnet test.");
                return;
            }

            Directory.CreateDirectory(TestResultsDirectory);

            DotNet($"test \"{TestsProjectFile}\" --configuration {Configuration} -m:1 --results-directory \"{TestResultsDirectory}\" --collect:\"XPlat Code Coverage\"", RootDirectory);

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

            try
            {
                DotNet($"tool run reportgenerator -- -reports:\"{coveragePattern}\" -targetdir:\"{reportTarget}\" -reporttypes:Html", RootDirectory);
            }
            catch (ProcessException)
            {
                Console.WriteLine("reportgenerator tool is unavailable. Skipping coverage HTML report generation.");
            }
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

    Target PublishAndroidApk => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            var apkPublishDirectory = PublishDirectory / "android-apk";

            DotNetPublish(s => s
                .SetProject(AndroidProjectFile)
                .SetConfiguration(Configuration)
                .SetFramework("net10.0-android")
                .SetOutput(apkPublishDirectory)
                .SetProperty("AndroidPackageFormat", "apk"));
        });
}
