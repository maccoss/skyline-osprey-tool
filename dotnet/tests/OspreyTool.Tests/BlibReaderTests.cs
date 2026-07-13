using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using OspreyTool.Core;
using Xunit;

namespace OspreyTool.Tests;

public sealed class BlibReaderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ospreytool-test-{Guid.NewGuid():N}.blib");

    // Verifies BOTH peak-blob encodings the real Cadenza blib uses: entry 1 with
    // zlib-compressed peaks (the common case) and entry 2 with raw (uncompressed) peaks.
    [Fact]
    public void ReadEntries_decodes_metadata_and_both_peak_encodings()
    {
        var mz1 = new[] { 274.122, 345.1591, 554.782 };
        var int1 = new[] { 0.4f, 0.5f, 1.0f };
        var mz2 = new[] { 400.10, 500.20 };
        var int2 = new[] { 0.8f, 0.2f };

        WriteBlib(
            (id: 1, pep: "PEPTIDER", mod: "PEPTIDER", charge: 2, mz: 500.25, rt: 8.27, mzs: mz1, ints: int1, compress: true),
            (id: 2, pep: "OTHERK", mod: "OTHERK", charge: 3, mz: 400.10, rt: 12.5, mzs: mz2, ints: int2, compress: false));

        var entries = new BlibReader(_dbPath).ReadEntries();
        Assert.Equal(2, entries.Count);

        var e1 = entries.Single(e => e.Key == new PrecursorKey("PEPTIDER", 2));
        Assert.Equal("PEPTIDER", e1.PeptideSequence);
        Assert.Equal(500.25, e1.PrecursorMz, 6);
        Assert.Equal(8.27, e1.PredictedRetentionTime!.Value, 6);
        Assert.Equal(3, e1.NumPeaks);
        Assert.Equal(mz1, e1.Fragments.Select(f => f.Mz).ToArray());
        Assert.Equal(int1, e1.Fragments.Select(f => f.Intensity).ToArray());

        var e2 = entries.Single(e => e.Key == new PrecursorKey("OTHERK", 3));
        Assert.Equal(2, e2.NumPeaks);
        Assert.Equal(mz2, e2.Fragments.Select(f => f.Mz).ToArray());
        Assert.Equal(int2, e2.Fragments.Select(f => f.Intensity).ToArray());
    }

    [Fact]
    public void ReadEntries_can_skip_fragments()
    {
        WriteBlib((1, "PEPTIDER", "PEPTIDER", 2, 500.25, 8.27, new[] { 274.1 }, new[] { 1.0f }, true));
        var entries = new BlibReader(_dbPath).ReadEntries(includeFragments: false);
        Assert.Empty(entries.Single().Fragments);
    }

    private void WriteBlib(
        params (int id, string pep, string mod, int charge, double mz, double rt, double[] mzs, float[] ints, bool compress)[] rows)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ConnectionString;
        using var conn = new SqliteConnection(cs);
        conn.Open();

        Exec(conn,
            "CREATE TABLE RefSpectra (id INTEGER PRIMARY KEY, peptideSeq TEXT, peptideModSeq TEXT, " +
            "precursorCharge INTEGER, precursorMZ REAL, numPeaks INTEGER, retentionTime REAL);");
        Exec(conn,
            "CREATE TABLE RefSpectraPeaks (RefSpectraID INTEGER, peakMZ BLOB, peakIntensity BLOB);");

        foreach (var r in rows)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO RefSpectra VALUES ($id, $pep, $mod, $ch, $mz, $n, $rt);";
                cmd.Parameters.AddWithValue("$id", r.id);
                cmd.Parameters.AddWithValue("$pep", r.pep);
                cmd.Parameters.AddWithValue("$mod", r.mod);
                cmd.Parameters.AddWithValue("$ch", r.charge);
                cmd.Parameters.AddWithValue("$mz", r.mz);
                cmd.Parameters.AddWithValue("$n", r.mzs.Length);
                cmd.Parameters.AddWithValue("$rt", r.rt);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO RefSpectraPeaks VALUES ($id, $mz, $int);";
                cmd.Parameters.AddWithValue("$id", r.id);
                cmd.Parameters.AddWithValue("$mz", MaybeCompress(EncodeDoubles(r.mzs), r.compress));
                cmd.Parameters.AddWithValue("$int", MaybeCompress(EncodeFloats(r.ints), r.compress));
                cmd.ExecuteNonQuery();
            }
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static byte[] EncodeDoubles(double[] values)
    {
        var bytes = new byte[values.Length * sizeof(double)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * sizeof(double)), values[i]);
        }
        return bytes;
    }

    private static byte[] EncodeFloats(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        }
        return bytes;
    }

    private static byte[] MaybeCompress(byte[] raw, bool compress)
    {
        if (!compress)
        {
            return raw;
        }
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
