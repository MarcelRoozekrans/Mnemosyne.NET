using System.Xml.Linq;
using Xunit;

namespace Mnemosyne.NET.Tests;

public sealed class RepoConventionsTests
{
    [Fact]
    public void EverySourceProjectIsAotCompatible()
    {
        foreach (var project in SourceProjects())
        {
            Assert.Equal("true", Property(project, "IsAotCompatible"));
        }
    }

    [Fact]
    public void EverySourceProjectHasItsOwnDescription()
    {
        var descriptions = new List<string>();
        foreach (var project in SourceProjects())
        {
            var description = Property(project, "Description");
            Assert.False(string.IsNullOrWhiteSpace(description), $"{project} has no Description");
            descriptions.Add(description!);
        }

        Assert.Equal(descriptions.Count, descriptions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EverySourceProjectIsInTheSolution()
    {
        var root = RepoRoot();
        var slnx = XDocument.Load(Path.Combine(root, "Mnemosyne.NET.slnx"));
        var listed = slnx.Descendants("Project")
            .Select(e => (e.Attribute("Path")?.Value ?? string.Empty).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var project in SourceProjects())
        {
            var relative = Path.GetRelativePath(root, project).Replace('\\', '/');
            Assert.True(listed.Contains(relative), $"{relative} is not a Project in Mnemosyne.NET.slnx");
        }
    }

    [Fact]
    public void AotSmokeRootsEverySourceProject()
    {
        var root = RepoRoot();
        var smoke = Path.Combine(root, "tests", "Mnemosyne.NET.AotSmoke", "Mnemosyne.NET.AotSmoke.csproj");
        Assert.True(File.Exists(smoke), $"{smoke} not found");

        var doc = XDocument.Load(smoke);
        var references = doc.Descendants("ProjectReference")
            .Select(e => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(smoke)!, (e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/'))))
            .ToHashSet(StringComparer.Ordinal);
        var rooted = doc.Descendants("TrimmerRootAssembly")
            .Select(e => e.Attribute("Include")?.Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var project in SourceProjects())
        {
            var assemblyName = Property(project, "AssemblyName") ?? Path.GetFileNameWithoutExtension(project);
            Assert.True(references.Contains(project), $"AotSmoke has no ProjectReference to {project}");
            Assert.True(rooted.Contains(assemblyName), $"AotSmoke does not root assembly {assemblyName}");
        }
    }

    [Fact]
    public void CiRunsTheAotGate()
    {
        var root = RepoRoot();
        var ci = Path.Combine(root, ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(ci), $"{ci} not found");

        var job = JobBody(StripComments(File.ReadAllText(ci)), "aot-publish");
        Assert.Contains("-p:PublishAot=true", job, StringComparison.Ordinal);
        Assert.Contains("-warnaserror", job, StringComparison.Ordinal);

        // GitHub counts a skipped job as passing a required check, so the gate must always run
        // and must never be allowed to fail quietly.
        Assert.DoesNotContain("if:", job, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error:", job, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTestProjectUsingTestcontainersRequiresDocker()
    {
        var root = RepoRoot();
        var testProjects = Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => Path.GetDirectoryName(Path.GetDirectoryName(p)) == Path.Combine(root, "tests"))
            .ToArray();
        var usingTestcontainers = testProjects
            .Where(p => XDocument.Load(p).Descendants("PackageReference")
                .Any(e => (e.Attribute("Include")?.Value ?? string.Empty).StartsWith("Testcontainers", StringComparison.Ordinal)))
            .ToArray();

        // Scanning nothing would pass for the wrong reason; the PostgreSQL suite uses Testcontainers.
        Assert.NotEmpty(usingTestcontainers);
        foreach (var project in usingTestcontainers)
        {
            Assert.True(
                Property(project, "RequiresDocker") == "true",
                $"{project} starts containers but does not declare <RequiresDocker>true</RequiresDocker>, so the windows leg of build-test, which cannot host Linux containers, would run it.");
        }

        // The property is worth nothing unless build-test selects on it: comment lines are stripped,
        // so only the command itself satisfies this.
        var job = JobBody(StripComments(File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))), "build-test");
        Assert.Contains("if [ \"$RUNNER_OS\" = \"Windows\" ]; then", job, StringComparison.Ordinal);
        Assert.Contains("xargs -r grep -L '<RequiresDocker>true</RequiresDocker>'", job, StringComparison.Ordinal);
    }

    private static string StripComments(string yaml) =>
        string.Join('\n', yaml.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    // The lines of one top-level job: from "  name:" up to the next two-space-indented key.
    private static string JobBody(string yaml, string name)
    {
        var lines = yaml.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var start = Array.FindIndex(lines, l => l == $"  {name}:");
        Assert.True(start >= 0, $"ci.yml has no job {name}");
        var body = lines.Skip(start + 1).TakeWhile(l => l.Length == 0 || l.StartsWith("   ", StringComparison.Ordinal));
        return string.Join('\n', body);
    }

    private static string[] SourceProjects()
    {
        var srcDir = Path.Combine(RepoRoot(), "src");
        var projects = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
            .Where(p => Path.GetDirectoryName(Path.GetDirectoryName(p)) == srcDir)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(projects);
        return projects;
    }

    private static string? Property(string project, string name) =>
        XDocument.Load(project).Descendants(name).FirstOrDefault()?.Value.Trim();

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mnemosyne.NET.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Mnemosyne.NET.slnx not found above " + AppContext.BaseDirectory);
    }
}
