using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace OspreyTool.Core;

/// <summary>
/// Reads a Bibliospec .blib (SQLite) into <see cref="LibraryEntry"/> records. Standalone,
/// so M0 can validate the Carafe/Cadenza library independently of Osprey's BlibLoader.
///
/// Verified against Cadenza-20260622-112136.blib (docs/data-formats.md): RefSpectra holds
/// one row per precursor; RefSpectraPeaks stores peakMZ (little-endian double[]) and
/// peakIntensity (little-endian float[]) as blobs that are zlib-compressed when the stored
/// length differs from the raw numPeaks*sizeof length (otherwise stored raw).
/// </summary>
public sealed class BlibReader
{
    private readonly string _path;

    public BlibReader(string path) => _path = path;

    public IReadOnlyList<LibraryEntry> ReadEntries(bool includeFragments = true)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadOnly,
        };

        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        var peaks = includeFragments ? ReadAllPeaks(connection) : null;
        var entries = new List<LibraryEntry>();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, peptideSeq, peptideModSeq, precursorCharge, precursorMZ, numPeaks, retentionTime " +
            "FROM RefSpectra";
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            var id = row.GetInt64(0);
            var numPeaks = row.IsDBNull(5) ? 0 : row.GetInt32(5);
            entries.Add(new LibraryEntry
            {
                PeptideSequence = row.IsDBNull(1) ? "" : row.GetString(1),
                PeptideModifiedSequence = row.IsDBNull(2) ? "" : row.GetString(2),
                PrecursorCharge = row.GetInt32(3),
                PrecursorMz = row.GetDouble(4),
                NumPeaks = numPeaks,
                PredictedRetentionTime = row.IsDBNull(6) ? null : row.GetDouble(6),
                Fragments = peaks is not null && peaks.TryGetValue(id, out var f)
                    ? f
                    : Array.Empty<LibraryFragment>(),
            });
        }
        return entries;
    }

    /// <summary>
    /// Reads only the predicted retention time per precursor (peptideModSeq + charge) - no peak
    /// decoding, so it stays fast even on a large library. Feeds Osprey's RT-penalized peak selection.
    /// </summary>
    public IReadOnlyDictionary<PrecursorKey, double> ReadRetentionTimes()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadOnly,
        };

        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        var map = new Dictionary<PrecursorKey, double>();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT peptideModSeq, precursorCharge, retentionTime FROM RefSpectra WHERE retentionTime IS NOT NULL";
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            if (row.IsDBNull(0))
            {
                continue;
            }
            map[new PrecursorKey(ModifiedSequence.Normalize(row.GetString(0)), row.GetInt32(1))] = row.GetDouble(2);
        }
        return map;
    }

    private static Dictionary<long, LibraryFragment[]> ReadAllPeaks(SqliteConnection connection)
    {
        var byId = new Dictionary<long, LibraryFragment[]>();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT p.RefSpectraID, s.numPeaks, p.peakMZ, p.peakIntensity " +
            "FROM RefSpectraPeaks p JOIN RefSpectra s ON s.id = p.RefSpectraID";
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            var id = row.GetInt64(0);
            var numPeaks = row.GetInt32(1);
            if (numPeaks == 0)
            {
                byId[id] = Array.Empty<LibraryFragment>();
                continue;
            }

            var mzBlob = (byte[])row[2];
            var intBlob = (byte[])row[3];
            var mz = DecodeDoubles(mzBlob, numPeaks);
            var intensity = DecodeFloats(intBlob, numPeaks);

            var fragments = new LibraryFragment[numPeaks];
            for (var i = 0; i < numPeaks; i++)
            {
                fragments[i] = new LibraryFragment(mz[i], intensity[i]);
            }
            byId[id] = fragments;
        }
        return byId;
    }

    private static double[] DecodeDoubles(byte[] blob, int count)
    {
        var bytes = Inflate(blob, count * sizeof(double));
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(i * sizeof(double)));
        }
        return values;
    }

    private static float[] DecodeFloats(byte[] blob, int count)
    {
        var bytes = Inflate(blob, count * sizeof(float));
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }
        return values;
    }

    /// <summary>
    /// Bibliospec stores peak blobs raw when their length already equals the expected
    /// uncompressed size, otherwise zlib-compressed. Detect by length and inflate as needed.
    /// </summary>
    private static byte[] Inflate(byte[] blob, int expectedLength)
    {
        if (blob.Length == expectedLength)
        {
            return blob;
        }

        using var input = new MemoryStream(blob);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new byte[expectedLength];
        var read = 0;
        while (read < expectedLength)
        {
            var n = zlib.Read(output, read, expectedLength - read);
            if (n == 0)
            {
                throw new InvalidDataException(
                    string.Format(CultureInfo.InvariantCulture,
                        "Blib peak blob inflated to {0} bytes, expected {1}.", read, expectedLength));
            }
            read += n;
        }
        return output;
    }
}
