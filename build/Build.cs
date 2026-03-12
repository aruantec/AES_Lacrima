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
    bool SelfContained { get; }

    AbsolutePath SolutionFile => RootDirectory / "AES_Lacrima.sln";
    AbsolutePath AppProjectFile => RootDirectory / "AES_Lacrima" / "AES_Lacrima.csproj";
    AbsolutePath ArtifactsDirectory => RootDirectory / "output";
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

            foreach (var testProject in TestProjects)
            {
                DotNetTest(s => s
                    .SetProjectFile(testProject)
                    .SetConfiguration(Configuration)
                    .EnableNoBuild()
                    .EnableNoRestore());
            }
        });

    Target Publish => _ => _
        .DependsOn(Compile)
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
                    .SetOutput(publishDirectory)
                    .EnableNoBuild();

                if (!string.IsNullOrWhiteSpace(Runtime))
                    settings = settings.SetRuntime(Runtime);

                if (SelfContained || !string.IsNullOrWhiteSpace(Runtime))
                    settings = settings.SetSelfContained(SelfContained);

                return settings;
            });
        });
}
