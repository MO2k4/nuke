﻿// Copyright Sebastian Karasek, Matthias Koch 2018.
// Distributed under the MIT License.
// https://github.com/nuke-build/nswag/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NuGet.Resolver;
using Nuke.CodeGeneration;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Nuke.NSwag.Generator;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using static GitHubTasks;
using static RegenerateTasks;
using Release = Octokit.Release;
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Pack);

    const string c_nSwagRepoOwner = "RSuter";
    const string c_nSwagRepoName = "NSwag";

    public Build()
    {
        LatestNSwagReleases = new Lazy<IReadOnlyList<Release>>(() => GetLatestReleases(c_nSwagRepoOwner, c_nSwagRepoName, numberOfReleases: 2, token: GitHubApiKey));
    }

    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion] readonly GitVersion GitVersion;
    [Solution] readonly Solution Solution;

    [Parameter("ApiKey for the specified source.")] readonly string ApiKey;
    [Parameter("Indicates to push to nuget.org feed.")] readonly bool NuGet;
    [Parameter("Api key to access the github.com api. Must have permissions to create pull-requests.")] readonly string GitHubApiKey;

    readonly Lazy<IReadOnlyList<Release>> LatestNSwagReleases;
    Release LatestNSwagRelease => LatestNSwagReleases.Value[index: 0];
    Project NSwagProject => Solution.GetProject("Nuke.NSwag");

    AbsolutePath SpecificationDirectory => NSwagProject.Directory / "specifications";
    string GenerationBaseDirectory => NSwagProject.Directory / "Generated";
    string PackageDirectory => TemporaryDirectory / "packages";
    string ChangelogFile => RootDirectory / "CHANGELOG.md";

    string Source => NuGet
        ? "https://api.nuget.org/v3/index.json"
        : "https://www.myget.org/F/nukebuild/api/v2/package";

    string SymbolSource => NuGet
        ? "https://nuget.smbsrc.net"
        : "https://www.myget.org/F/nukebuild/symbols/api/v2/package";

    Target Clean => _ => _
        .Executes(() =>
        {
            DeleteDirectories(GlobDirectories(SourceDirectory, "**/bin", "**/obj"));
            EnsureCleanDirectory(OutputDirectory);
            DeleteDirectory(PackageDirectory);
        });

    Target DownloadPackages => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            NugetPackageLoader.InstallPackage("Nswag.Commands", PackageDirectory, dependencyBehavior: DependencyBehavior.Highest);
        });

    Target Generate => _ => _
        .DependsOn(DownloadPackages)
        .Executes(() =>
        {
            DeleteDirectory(GenerationBaseDirectory);
            DeleteDirectory(SpecificationDirectory);

            NSwagSpecificationGenerator.WriteSpecifications(x => x
                .SetGitReference(LatestNSwagReleases.Value[index: 0].TargetCommitish.Substring(startIndex: 0, length: 10))
                .SetOutputFolder(SpecificationDirectory)
                .SetPackageFolder(PackageDirectory));

            CodeGenerator.GenerateCode(SpecificationDirectory, GenerationBaseDirectory, baseNamespace: "Nuke.NSwag",
                gitRepository: GitRepository);
        });

    Target CompilePlugin => _ => _
        .DependsOn(Generate)
        .Executes(() =>
        {
            DotNetRestore(x => DefaultDotNetRestore
                .SetProjectFile(NSwagProject));
            DotNetBuild(x => DefaultDotNetBuild
                .SetProjectFile(NSwagProject));
        });

    Target Pack => _ => _
        .DependsOn(CompilePlugin)
        .Executes(() =>
        {
            var releaseNotes = ExtractChangelogSectionNotes(ChangelogFile)
                .Select(x => x.Replace("- ", "\u2022 ").Replace("`", string.Empty).Replace(",", "%2C"))
                .Concat(string.Empty)
                .Concat($"Full changelog at {GitRepository.GetGitHubBrowseUrl(ChangelogFile)}")
                .JoinNewLine();

            DotNetPack(s => DefaultDotNetPack
                .SetProject(NSwagProject)
                .SetPackageReleaseNotes(releaseNotes));
        });

    Target Changelog => _ => _
        .OnlyWhen(() => InvokedTargets.Contains(nameof(Changelog)))
        .Executes(() =>
        {
            FinalizeChangelog(ChangelogFile, GitVersion.SemVer, GitRepository);

            Git($"add {ChangelogFile}");
            Git($"commit -m \"Finalize {Path.GetFileName(ChangelogFile)} for {GitVersion.SemVer}.\"");
            Git($"tag -f {GitVersion.SemVer}");
        });

    Target Push => _ => _
        .DependsOn(Pack)
        .Requires(() => ApiKey)
        .Requires(() => !GitHasUncommitedChanges())
        .Requires(() => !NuGet || GitVersionAttribute.Bump.HasValue)
        .Requires(() => !NuGet || Configuration.EqualsOrdinalIgnoreCase("release"))
        .Requires(() => !NuGet || GitVersion.BranchName.Equals("master"))
        .Executes(() =>
        {
            GlobFiles(OutputDirectory, "*.nupkg").NotEmpty()
                .Where(f => !f.EndsWith("symbols.nupkg"))
                .ForEach(p => DotNetNuGetPush(c => c
                    .SetSource(Source)
                    .SetSymbolSource(SymbolSource)
                    .SetApiKey(ApiKey)
                    .SetTargetPath(p)));
        });

    Target Regenerate => _ => _
        .Requires(() => GitHubApiKey)
        .Requires(() => LatestNSwagRelease)
        .DependsOn(CompilePlugin)
        .Executes(() =>
        {
            const string branch = "nswag-update";
            const string message = "Regenerate for NSwag";

            var (latestVersion, oldVersion) = LatestNSwagReleases.Value
                .Select(x => GetVersion(x, c_nSwagRepoOwner, c_nSwagRepoName, GitHubApiKey))
                .ToArray();

            var bump = GetBump(latestVersion, oldVersion);

            CommitAndPushChanges(latestVersion, NSwagProject.Directory, bump, branch);
            CreatePullRequestIfNeeded(GitRepository.Identifier, branch, $"{message} update.", $"{message} v{latestVersion}.", GitHubApiKey);
        });

    bool ShouldRegenerate() => IsUpdateAvailable(LatestNSwagRelease, c_nSwagRepoOwner, c_nSwagRepoName, SpecificationDirectory / "NSwag.json");
}