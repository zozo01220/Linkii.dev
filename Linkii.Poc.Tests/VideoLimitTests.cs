using System.Buffers.Binary;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Limite 1080p des vidéos importées : lecture de la taille d'un MP4 et règle de dépassement.</summary>
public class VideoLimitTests
{
    [Theory]
    [InlineData(1920, 1080, true)]
    [InlineData(1280, 720, true)]
    [InlineData(1080, 1920, true)]    // portrait
    [InlineData(2560, 1440, false)]
    [InlineData(3840, 2160, false)]
    [InlineData(1440, 2560, false)]   // portrait 2K
    [InlineData(1920, 1200, false)]   // plus haut que 1080
    public void Limite_1080p(int w, int h, bool attendu) => Assert.Equal(attendu, VideoConverter.FitsLimit(w, h));

    // Boîte MP4 minimale : moov > trak > tkhd (piste audio vide d'abord, puis piste vidéo)
    static byte[] Box(string type, params byte[][] content)
    {
        var body = content.SelectMany(c => c).ToArray();
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    static byte[] Tkhd(int w, int h, bool rotated)
    {
        var d = new byte[84];                                   // version 0 : 4 + 80 octets
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(40), rotated ? 0 : 0x10000);      // matrice a
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(44), rotated ? 0x10000 : 0);      // matrice b
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(76), (uint)w << 16);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(80), (uint)h << 16);
        return Box("tkhd", d);
    }

    static (int, int)? Probe(byte[] file)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, file);
        try { return Mp4Probe.Size(path); } finally { File.Delete(path); }
    }

    [Fact]
    public void Lit_la_taille_de_la_piste_video()
    {
        var mp4 = Box("moov", Box("trak", Tkhd(0, 0, false)), Box("trak", Tkhd(2560, 1440, false)));
        Assert.Equal((2560, 1440), Probe(Box("ftyp", new byte[8]).Concat(Box("mdat", new byte[100])).Concat(mp4).ToArray()));
    }

    [Fact]
    public void Echange_largeur_et_hauteur_si_la_video_est_tournee()
    {
        var mp4 = Box("moov", Box("trak", Tkhd(1920, 1080, true)));
        Assert.Equal((1080, 1920), Probe(mp4));
    }

    [Fact]
    public void Fichier_illisible_donne_null() => Assert.Null(Probe(new byte[] { 1, 2, 3, 4, 5 }));
}
