using System.Text.RegularExpressions;
using Xunit;

namespace MyLog.Tests;

public class ComplexityTests
{
    private static readonly Regex MethodDeclarationRegex = new(
        @"(public|private|protected|internal)\s+(?:async\s+)?(?:static\s+)?(?:sealed\s+)?(?:override\s+)?(?:virtual\s+)?[\w\<\>\[\]\?,\s]+\s+([A-Za-z0-9_]+)\s*\([^\)]*\)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex IfRegex = new(@"\bif\b", RegexOptions.Compiled);
    private static readonly Regex BranchRegex = new(@"\b(if|case|catch|while|for|foreach)\b", RegexOptions.Compiled);

    [Fact]
    public void AllMethods_ShouldNotExceedFifteenBranchesOrIfStatements()
    {
        var currentDir = AppContext.BaseDirectory;
        // Search upwards to find MyLog directory
        var dir = new DirectoryInfo(currentDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MyLog.csproj")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var targetDirs = new[]
        {
            Path.Combine(dir.FullName, "Controllers"),
            Path.Combine(dir.FullName, "Services")
        };

        var violations = new List<string>();

        foreach (var targetDir in targetDirs)
        {
            if (!Directory.Exists(targetDir)) continue;

            foreach (var file in Directory.EnumerateFiles(targetDir, "*.cs", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(file);
                var matches = MethodDeclarationRegex.Matches(content);

                foreach (Match match in matches)
                {
                    var methodName = match.Groups[2].Value;
                    var startIndex = match.Index + match.Length - 1;

                    var depth = 0;
                    var endIndex = -1;
                    for (var i = startIndex; i < content.Length; i++)
                    {
                        if (content[i] == '{') depth++;
                        else if (content[i] == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                endIndex = i;
                                break;
                            }
                        }
                    }

                    if (endIndex > startIndex)
                    {
                        var body = content.Substring(startIndex, endIndex - startIndex + 1);
                        var ifCount = IfRegex.Matches(body).Count;
                        var branchCount = BranchRegex.Matches(body).Count;

                        if (ifCount > 15 || branchCount > 15)
                        {
                            violations.Add($"{Path.GetFileName(file)} -> {methodName} (ifs={ifCount}, branches={branchCount})");
                        }
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found methods exceeding SonarQube complexity limit (15):\n{string.Join("\n", violations)}");
    }
}
