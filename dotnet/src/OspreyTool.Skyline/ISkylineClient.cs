#nullable enable

using System;
using System.Collections.Generic;

namespace OspreyTool.Skyline;

/// <summary>Inline rows read from a Skyline report/document-grid view (column headers + string cells).</summary>
public sealed record ReportRows(IReadOnlyList<string> Columns, IReadOnlyList<string[]> Rows);

/// <summary>
/// The subset of Skyline's JSON-RPC client the tool needs: identify the document, run silent commands
/// (chromatogram export, peak-boundary import), and pull a report by column definition (the doc RTs).
/// Extracting it as an interface lets callers be tested with a fake instead of a live Skyline pipe.
/// </summary>
public interface ISkylineClient
{
    string GetDocumentPath();
    string GetVersion();

    /// <summary>Run a SkylineCmd-style command against the running instance; returns its output text.</summary>
    string RunCommandSilent(string[] args);

    /// <summary>Read a report for the given document-grid columns (row source inferred), or null if unavailable.</summary>
    ReportRows? GetReport(IReadOnlyList<string> selectColumns);

    /// <summary>All configured item names in a settings list (e.g. "Spectral Libraries").</summary>
    string[] GetSettingsListNames(string listType);

    /// <summary>The items of a settings list currently active in the document (e.g. the doc's libraries).</summary>
    string[] GetSettingsListSelectedItems(string listType);

    /// <summary>The XML definition of one settings-list item (e.g. a "Color Schemes" entry).</summary>
    string? GetSettingsListItem(string listType, string itemName);

    /// <summary>
    /// ElementLocator of the selected element at the given level ("Molecule", "Precursor", ...), or null if
    /// nothing of that type is selected. Skyline walks UP the tree, so selecting a transition still yields
    /// its Molecule. A peptide locator looks like <c>Molecule:/sp|P36222|CH3L1_HUMAN/ILGQQVPYATK</c> - the
    /// last path segment is the modified sequence.
    /// </summary>
    string? GetSelectedElementLocator(string elementType);

    /// <summary>Name of the replicate currently active in Skyline, or null/empty if none.</summary>
    string? GetReplicateName();
}

/// <summary>What the user currently has selected in Skyline's Targets tree + replicate selector.</summary>
public sealed record SkylineSelection(string? PeptideModifiedSequence, int? PrecursorCharge, string? Replicate);

/// <summary>
/// Opens a Skyline connection per call and hands the caller an <see cref="ISkylineClient"/>.
/// <see cref="SkylineSession"/> is the production implementation (a JSON-RPC pipe); tests supply a fake.
/// </summary>
public interface ISkylineExecutor
{
    T Execute<T>(Func<ISkylineClient, T> action);
    void Execute(Action<ISkylineClient> action);
}
