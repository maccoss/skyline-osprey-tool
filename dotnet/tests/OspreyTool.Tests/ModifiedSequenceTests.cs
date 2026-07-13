using OspreyTool.Core;
using Xunit;

namespace OspreyTool.Tests;

public sealed class ModifiedSequenceTests
{
    [Fact]
    public void StripMods_removes_every_bracketed_modification()
    {
        Assert.Equal("CLAVYQAGAR", ModifiedSequence.StripMods("C[+57]LAVYQAGAR"));
        Assert.Equal("CLAVYQAGAR", ModifiedSequence.StripMods("C[57.02146372057]LAVYQAGAR"));
        Assert.Equal("PEPMTIDEK", ModifiedSequence.StripMods("PEPM[+16]TIDEK"));
        Assert.Equal("PEPTIDEK", ModifiedSequence.StripMods("PEPTIDEK")); // no mods -> unchanged
    }

    [Fact]
    public void StripMods_makes_the_same_peptide_from_different_mod_notations_compare_equal()
    {
        // Skyline exports C[+57]; the blib / a report may write C[57.021...] - both are the same peptide.
        Assert.Equal(
            ModifiedSequence.StripMods("C[+57]LAVYQAGAR"),
            ModifiedSequence.StripMods("C[57.02146372057]LAVYQAGAR"));
    }

    [Fact]
    public void Normalize_rounds_bracket_masses_so_the_same_peptide_matches_across_sources()
    {
        Assert.Equal("C[+57]LAVYQAGAR", ModifiedSequence.Normalize("C[57.02146372057]LAVYQAGAR"));
        Assert.Equal(
            ModifiedSequence.Normalize("C[+57]LAVYQAGAR"),
            ModifiedSequence.Normalize("C[57.02146372057]LAVYQAGAR"));
    }

    [Fact]
    public void Normalize_keeps_distinct_variable_mods_distinct()
    {
        // Oxidation (+16) must not collapse onto phospho (+80) etc.
        Assert.NotEqual(ModifiedSequence.Normalize("PEPM[+16]K"), ModifiedSequence.Normalize("PEPM[+80]K"));
    }
}
