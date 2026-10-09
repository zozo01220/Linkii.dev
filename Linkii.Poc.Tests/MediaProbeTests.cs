using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Dimensions affichées dans la médiathèque : lecture des en-têtes d'images et libellé « 1080p » / « 1920×1080 ».</summary>
public class MediaProbeTests
{
    static (int, int)? Probe(string ext, byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        File.WriteAllBytes(path, data);
        try { return MediaProbe.Size(path); } finally { File.Delete(path); }
    }

    [Fact]
    public void Png()
    {
        var b = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), 1920);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), 1080);
        Assert.Equal((1920, 1080), Probe(".png", b));
    }

    [Fact]
    public void Gif()
    {
        var b = new byte[13];
        Encoding.ASCII.GetBytes("GIF89a").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 640);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), 360);
        Assert.Equal((640, 360), Probe(".gif", b));
    }

    [Fact]
    public void Jpeg_apres_un_segment_applicatif()
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x06, 1, 2, 3, 4 };   // APP0 de 4 octets de données
        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x0B, 8, 0x04, 0x38, 0x07, 0x80, 1, 0 });   // SOF0 : hauteur 1080, largeur 1920
        Assert.Equal((1920, 1080), Probe(".jpg", b.ToArray()));
    }

    [Fact]
    public void Webp_etendu()
    {
        var b = new byte[32];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        Encoding.ASCII.GetBytes("WEBPVP8X").CopyTo(b, 8);
        var w = 1919; var h = 1079;   // stockés moins 1, sur 3 octets
        b[24] = (byte)w; b[25] = (byte)(w >> 8); b[26] = (byte)(w >> 16);
        b[27] = (byte)h; b[28] = (byte)(h >> 8); b[29] = (byte)(h >> 16);
        Assert.Equal((1920, 1080), Probe(".webp", b));
    }

    [Fact]
    public void Svg_taille_ou_viewBox()
    {
        Assert.Equal((300, 200), Probe(".svg", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"300px\" height=\"200\"></svg>")));
        Assert.Equal((800, 600), Probe(".svg", Encoding.UTF8.GetBytes("<svg viewBox=\"0 0 800 600\" xmlns=\"x\"></svg>")));
    }

    [Fact]
    public void Fichier_illisible_ou_inconnu() => Assert.Null(Probe(".png", new byte[] { 1, 2, 3 }));

    [Theory]
    [InlineData("video", 1920, 1080, "1080p")]
    [InlineData("video", 1080, 1920, "1080p")]
    [InlineData("video", 1280, 720, "720p")]
    [InlineData("video", 3840, 2160, "4K")]
    [InlineData("image", 1920, 1080, "1920×1080")]
    public void Libelle(string type, int w, int h, string attendu) =>
        Assert.Equal(attendu, MediaProbe.Label(new MediaItem { Type = type, Width = w, Height = h }));

    [Fact]
    public void Libelle_absent_sans_dimensions() => Assert.Null(MediaProbe.Label(new MediaItem { Type = "image" }));
}
