using System.Reflection;
using System.Text.RegularExpressions;
using FlociLab.Core;
using Xunit;

namespace FlociLab.IntegrationTests;

/// <summary>
/// A finding on <c>/differences</c> has to be a sentence the sample already contains. This does
/// not start an emulator: it only reads the demo source.
/// </summary>
public sealed class ServiceFindingTests
{
    [Fact]
    public void Every_finding_is_a_sentence_in_that_sample()
    {
        foreach (AssemblyName name in typeof(ServiceFindingTests).Assembly.GetReferencedAssemblies())
        {
            Assembly.Load(name);
        }

        string root = RepoRoot();
        Dictionary<string, string> sources = SourceByTypeName(root);
        int findings = 0;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (Type type in SafeTypes(assembly))
            {
                if (type is not { IsClass: true, IsAbstract: false } || !typeof(IServiceDemo).IsAssignableFrom(type))
                {
                    continue;
                }

                foreach (ServiceFindingAttribute finding in type.GetCustomAttributes<ServiceFindingAttribute>())
                {
                    findings++;
                    Assert.True(sources.TryGetValue(type.Name, out string? source), $"No source file for {type.FullName}.");
                    Assert.Contains(finding.Detail, source, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(findings != 0, "No ServiceFinding attributes were found. The differences page would be empty.");
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FlociLab.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find FlociLab.slnx above the test output directory.");
    }

    private static Dictionary<string, string> SourceByTypeName(string root)
    {
        Dictionary<string, string> sources = [];

        foreach (string path in Directory.GetFiles(Path.Combine(root, "samples"), "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(path);

            foreach (Match match in Regex.Matches(source, @"class (\w+Demo)\b"))
            {
                sources[match.Groups[1].Value] = source;
            }
        }

        return sources;
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
