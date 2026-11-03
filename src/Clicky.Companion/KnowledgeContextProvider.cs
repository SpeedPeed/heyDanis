using System.IO;
using System.Text;

namespace Clicky.Companion;

public static class KnowledgeContextProvider
{
    public const int MaxKnowledgeCharacters = 60000;

    public static string KnowledgeRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Clicky",
        "knowledge");

    public static string BuildPrompt(string basePrompt)
    {
        var knowledge = ReadKnowledgeContext();
        if (string.IsNullOrWhiteSpace(knowledge))
            return basePrompt;

        return basePrompt.TrimEnd()
            + Environment.NewLine
            + Environment.NewLine
            + "local knowledge files:"
            + Environment.NewLine
            + knowledge;
    }

    public static string ReadKnowledgeContext()
    {
        Directory.CreateDirectory(KnowledgeRoot);

        var builder = new StringBuilder();
        var remaining = MaxKnowledgeCharacters;

        foreach (var file in EnumerateKnowledgeFiles())
        {
            if (remaining <= 0) break;

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var relative = Path.GetRelativePath(KnowledgeRoot, file);
            var header = $"--- {relative} ---{Environment.NewLine}";
            if (header.Length >= remaining)
                break;

            builder.AppendLine(header.TrimEnd());
            remaining -= header.Length;

            if (text.Length > remaining)
            {
                builder.Append(text[..remaining]);
                remaining = 0;
            }
            else
            {
                builder.AppendLine(text.TrimEnd());
                builder.AppendLine();
                remaining -= text.Length;
            }
        }

        return builder.ToString().TrimEnd();
    }

    public static IReadOnlyList<string> EnumerateKnowledgeFiles()
    {
        Directory.CreateDirectory(KnowledgeRoot);

        return Directory.EnumerateFiles(KnowledgeRoot, "*.*", SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
