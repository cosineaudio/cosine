using System.Text;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class TagEncodingRepairTests
{
    [Fact]
    public void RepairC1Controls_MapsCp1252Punctuation()
    {
        Assert.Equal("When It’s All Gone", TagEncodingRepair.RepairC1Controls("When It\u0092s All Gone"));
        Assert.Equal("“Quoted”", TagEncodingRepair.RepairC1Controls("\u0093Quoted\u0094"));
        Assert.Equal("Rock – Pop", TagEncodingRepair.RepairC1Controls("Rock \u0096 Pop"));
    }

    [Fact]
    public void RepairC1Controls_LeavesCleanStringsAlone()
    {
        Assert.Equal("Terror Reid", TagEncodingRepair.RepairC1Controls("Terror Reid"));
        Assert.Equal("Café Tacvba", TagEncodingRepair.RepairC1Controls("Café Tacvba"));
        Assert.Null(TagEncodingRepair.RepairC1Controls(null));
    }

    [Fact]
    public void RepairC1Controls_DropsUndefinedControlChars()
    {
        Assert.Equal("AB", TagEncodingRepair.RepairC1Controls("A\u0081B"));
    }

    [Fact]
    public void DecodeLenient_ValidUtf8_PassesThrough()
    {
        var text = "Björk – Jóga";
        Assert.Equal(text, TagEncodingRepair.DecodeLenient(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void DecodeLenient_Cp1252Bytes_AreRecovered()
    {
        // "When It’s" with CP-1252 0x92 apostrophe - invalid as UTF-8.
        var bytes = new byte[] { 0x57, 0x68, 0x65, 0x6E, 0x20, 0x49, 0x74, 0x92, 0x73 };
        Assert.Equal("When It’s", TagEncodingRepair.DecodeLenient(bytes));
    }

    [Fact]
    public void TryReadFlacVorbisComments_ReadsMisEncodedTags()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cosine-test-{Guid.NewGuid():N}.flac");
        try
        {
            File.WriteAllBytes(path, BuildFlacWithComments(new Dictionary<string, byte[]>
            {
                ["Title"] = new byte[] { 0x49, 0x74, 0x92, 0x73 }, // "It’s" in CP-1252
                ["Artist"] = Encoding.UTF8.GetBytes("Terror Reid"),
            }));

            var comments = TagEncodingRepair.TryReadFlacVorbisComments(path);

            Assert.NotNull(comments);
            Assert.Equal("It’s", comments!["TITLE"]);
            Assert.Equal("Terror Reid", comments["ARTIST"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadFlacVorbisComments_NonFlac_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cosine-test-{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllBytes(path, [0xFF, 0xFB, 0x90, 0x00]);
            Assert.Null(TagEncodingRepair.TryReadFlacVorbisComments(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildFlacWithComments(Dictionary<string, byte[]> comments)
    {
        using var ms = new MemoryStream();
        ms.Write("fLaC"u8);

        // Minimal STREAMINFO block (34 bytes of zeros)
        ms.WriteByte(0x00);
        WriteUInt24Be(ms, 34);
        ms.Write(new byte[34]);

        // VORBIS_COMMENT block, marked last
        using var vc = new MemoryStream();
        var vendor = "test"u8.ToArray();
        vc.Write(BitConverter.GetBytes((uint)vendor.Length));
        vc.Write(vendor);
        vc.Write(BitConverter.GetBytes((uint)comments.Count));
        foreach (var (key, valueBytes) in comments)
        {
            var keyBytes = Encoding.ASCII.GetBytes(key + "=");
            vc.Write(BitConverter.GetBytes((uint)(keyBytes.Length + valueBytes.Length)));
            vc.Write(keyBytes);
            vc.Write(valueBytes);
        }

        ms.WriteByte(0x80 | 0x04);
        WriteUInt24Be(ms, (int)vc.Length);
        vc.WriteTo(ms);

        return ms.ToArray();
    }

    private static void WriteUInt24Be(Stream s, int value)
    {
        s.WriteByte((byte)((value >> 16) & 0xFF));
        s.WriteByte((byte)((value >> 8) & 0xFF));
        s.WriteByte((byte)(value & 0xFF));
    }
}
