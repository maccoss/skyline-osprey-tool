using System.Xml;

namespace OspreyTool.Core;

/// <summary>
/// Which of a Skyline document's raw data files are reachable on disk. The observed apex MS2
/// spectra that the spectrum-match features need come only from the raw files (the .skyd holds
/// chromatograms, the Carafe .blib holds predicted spectra) - so when a raw file is missing,
/// those scores cannot be computed and the tool must warn rather than silently drop them.
/// </summary>
public sealed class RawFileAvailability
{
    private RawFileAvailability(
        IReadOnlyList<string> allPaths,
        IReadOnlyList<string> accessiblePaths,
        IReadOnlyList<string> missingPaths)
    {
        AllPaths = allPaths;
        AccessiblePaths = accessiblePaths;
        MissingPaths = missingPaths;
    }

    public IReadOnlyList<string> AllPaths { get; }

    public IReadOnlyList<string> AccessiblePaths { get; }

    public IReadOnlyList<string> MissingPaths { get; }

    /// <summary>True only when at least one raw file is referenced and every one is reachable.</summary>
    public bool AllAccessible => AllPaths.Count > 0 && MissingPaths.Count == 0;

    public bool AnyMissing => MissingPaths.Count > 0;

    public static RawFileAvailability FromDocument(string skylineDocumentPath) =>
        Evaluate(ReadRawPaths(skylineDocumentPath), PathExists);

    /// <summary>Evaluate a set of paths against an existence predicate (injectable for tests).</summary>
    public static RawFileAvailability Evaluate(IReadOnlyList<string> paths, Func<string, bool> exists)
    {
        var accessible = new List<string>();
        var missing = new List<string>();
        foreach (var path in paths)
        {
            (exists(path) ? accessible : missing).Add(path);
        }
        return new RawFileAvailability(paths, accessible, missing);
    }

    public static IReadOnlyList<string> ReadRawPaths(string skylineDocumentPath)
    {
        var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(skylineDocumentPath, settings);
        return ReadRawPaths(reader);
    }

    /// <summary>Reads the <c>file_path</c> of every <c>sample_file</c> element (one per imported run).</summary>
    public static IReadOnlyList<string> ReadRawPaths(XmlReader reader)
    {
        var paths = new List<string>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "sample_file")
            {
                var filePath = reader.GetAttribute("file_path");
                if (!string.IsNullOrEmpty(filePath))
                {
                    paths.Add(NormalizePath(filePath));
                }
            }
        }
        return paths;
    }

    /// <summary>Skyline may append a multi-sample selector after '|' or '?'; the on-disk item is the first segment.</summary>
    private static string NormalizePath(string filePath)
    {
        var cut = filePath.IndexOfAny(new[] { '|', '?' });
        return cut >= 0 ? filePath[..cut] : filePath;
    }

    // A raw source may be a file (Thermo .raw, mzML) or a directory bundle (.d), so check both.
    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);
}
