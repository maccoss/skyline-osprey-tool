using OspreyTool.Skyline;
using Xunit;

namespace OspreyTool.Tests;

public sealed class SkylineSelectionReaderTests
{
    [Fact]
    public void Peptide_is_the_last_segment_of_a_Molecule_locator()
    {
        // The real shape, read back from a live Skyline document.
        Assert.Equal("ILGQQVPYATK",
            SkylineSelectionReader.LastSegment("Molecule:/sp|P36222|CH3L1_HUMAN/ILGQQVPYATK"));
        // A modified sequence keeps its bracket annotation.
        Assert.Equal("C[+57.0]LAVYQAGAR",
            SkylineSelectionReader.LastSegment("Molecule:/sp|P00001|TEST/C[+57.0]LAVYQAGAR"));
    }

    [Fact]
    public void Quoted_segments_are_unescaped_and_their_separators_ignored()
    {
        // ElementLocator quotes a segment containing '/', doubling any inner quote.
        Assert.Equal("PEPTIDEK",
            SkylineSelectionReader.LastSegment("Molecule:/\"protein/with/slashes\"/PEPTIDEK"));
        Assert.Equal("odd/name",
            SkylineSelectionReader.LastSegment("Molecule:/prot/\"odd/name\""));
        Assert.Equal("say \"hi\"",
            SkylineSelectionReader.LastSegment("Molecule:/prot/\"say \"\"hi\"\"\""));
    }

    [Fact]
    public void Charge_comes_from_the_precursor_locator_sign_run()
    {
        Assert.Equal(2, SkylineSelectionReader.ChargeFromPrecursorLocator(
            "Precursor:/sp|P36222|CH3L1_HUMAN/ILGQQVPYATK/light++"));
        Assert.Equal(3, SkylineSelectionReader.ChargeFromPrecursorLocator(
            "Precursor:/sp|P36222|CH3L1_HUMAN/ILGQQVPYATK/heavy+++"));
        Assert.Equal(-1, SkylineSelectionReader.ChargeFromPrecursorLocator("Precursor:/list/molecule/light-"));
    }

    [Fact]
    public void Missing_or_higher_level_selections_yield_nulls_not_exceptions()
    {
        // A protein is selected: there is no Molecule/Precursor locator at all.
        Assert.Null(SkylineSelectionReader.LastSegment(null));
        Assert.Null(SkylineSelectionReader.LastSegment(""));
        Assert.Null(SkylineSelectionReader.ChargeFromPrecursorLocator(null));
        // A peptide is selected but no precursor beneath it: no trailing sign run to read.
        Assert.Null(SkylineSelectionReader.ChargeFromPrecursorLocator("Molecule:/prot/PEPTIDEK"));
    }

    [Fact]
    public void Read_pulls_peptide_charge_and_replicate_from_the_client()
    {
        var sel = SkylineSelectionReader.Read(new FakeClient(
            molecule: "Molecule:/sp|P36222|CH3L1_HUMAN/ILGQQVPYATK",
            precursor: "Precursor:/sp|P36222|CH3L1_HUMAN/ILGQQVPYATK/light++",
            replicate: "MMCC-2-001"));

        Assert.Equal("ILGQQVPYATK", sel.PeptideModifiedSequence);
        Assert.Equal(2, sel.PrecursorCharge);
        Assert.Equal("MMCC-2-001", sel.Replicate);
    }

    [Fact]
    public void Read_reports_no_charge_when_the_selection_is_above_the_precursor()
    {
        var sel = SkylineSelectionReader.Read(new FakeClient(
            molecule: "Molecule:/sp|P36222|CH3L1_HUMAN/ILGQQVPYATK", precursor: null, replicate: ""));

        Assert.Equal("ILGQQVPYATK", sel.PeptideModifiedSequence);
        Assert.Null(sel.PrecursorCharge);
        Assert.Null(sel.Replicate); // empty replicate name normalizes to null
    }

    private sealed class FakeClient : ISkylineClient
    {
        private readonly string? _molecule;
        private readonly string? _precursor;
        private readonly string? _replicate;

        public FakeClient(string? molecule, string? precursor, string? replicate)
        {
            _molecule = molecule;
            _precursor = precursor;
            _replicate = replicate;
        }

        public string? GetSelectedElementLocator(string elementType) => elementType switch
        {
            "Molecule" => _molecule,
            "Precursor" => _precursor,
            _ => null,
        };

        public string? GetReplicateName() => _replicate;

        public string GetDocumentPath() => throw new NotSupportedException();
        public string GetVersion() => throw new NotSupportedException();
        public string RunCommandSilent(string[] args) => throw new NotSupportedException();
        public ReportRows? GetReport(IReadOnlyList<string> selectColumns) => throw new NotSupportedException();
        public string[] GetSettingsListNames(string listType) => throw new NotSupportedException();
        public string[] GetSettingsListSelectedItems(string listType) => throw new NotSupportedException();
        public string? GetSettingsListItem(string listType, string itemName) => throw new NotSupportedException();
    }
}
