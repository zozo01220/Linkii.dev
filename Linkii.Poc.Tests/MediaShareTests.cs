using System.Security.Claims;
using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Partage de médias en lecture seule : visible à la racine du destinataire, ni modifiable ni supprimable par lui, retirable par les deux.</summary>
[Collection("platform")]
public class MediaShareTests
{
    readonly Platform p;
    readonly Tenant client;
    readonly User alice, marc, sophie;
    readonly MediaItem photo;

    public MediaShareTests(Platform p)
    {
        this.p = p;
        var store = p.Services.GetRequiredService<JsonStore>();
        (client, alice, marc, sophie, photo) = store.Write(db =>
        {
            var c = new Tenant { ResellerId = p.R1.Id, Name = "Partage SA", FoldersOn = true };
            var area = new Area { ClientId = c.Id, Name = "Siège" };
            AreaOps.EnsureDefaultZone(area);
            db.Clients.Add(c); db.Areas.Add(area);
            var editeur = new AccessLevel { Name = "Éditeur", Pages = [LevelPages.Screens, LevelPages.Playlists, LevelPages.Media], AllAreas = true };
            var caisse = new AccessLevel { Name = "Caisse", Pages = [LevelPages.Screens], AllAreas = true };
            c.Levels.AddRange([editeur, caisse]);
            User Member(string name, AccessLevel l) { var u = new User { Email = name + "@partage.ch", Name = name, Role = Roles.ClientMember, ClientId = c.Id, LevelId = l.Id }; db.Users.Add(u); return u; }
            var a = Member("alice", editeur); var m = Member("marc", editeur); var s = Member("sophie", caisse);
            var item = new MediaItem { ClientId = c.Id, AreaId = area.Id, Name = "Sortie", Type = "image", FileName = "sortie.png", OwnerId = a.Id, AddedUtc = DateTime.UtcNow };
            db.Media.Add(item);
            return (c, a, m, s, item);
        });
    }

    MediaLibrary As(User u)
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(Claims.Client, client.Id.ToString()), new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Role, Roles.ClientMember)
        }, "test"));
        return new MediaLibrary(new TenantStore(store, new TenantContext(new FixedAuth(principal), store)), p.Services.GetRequiredService<AppCatalog>());
    }

    [Fact]
    public void A_member_does_not_see_the_root_media_of_another_member_until_it_is_shared()
    {
        Assert.Contains(photo.Id, As(alice).Files().Select(m => m.Id));
        Assert.DoesNotContain(photo.Id, As(marc).Files().Select(m => m.Id));

        var (n, err) = As(alice).Share([photo.Id], marc.Id);
        Assert.Null(err); Assert.Equal(1, n);

        var marcLib = As(marc);
        Assert.Contains(photo.Id, marcLib.Files().Select(m => m.Id));
        Assert.Equal("alice", marcLib.SharedWithMe()[photo.Id].By);
        Assert.Contains(photo.Id, As(alice).SharedOut());
        Assert.Single(As(alice).SharesOf(photo.Id));
    }

    [Fact]
    public void The_recipient_cannot_rename_or_delete_a_shared_media_but_can_stop_seeing_it()
    {
        As(alice).Share([photo.Id], marc.Id);
        var marcLib = As(marc);
        marcLib.Rename(photo.Id, "Piraté");
        Assert.Equal(0, marcLib.Delete([photo.Id]));
        Assert.Equal("Sortie", As(alice).Files().First(m => m.Id == photo.Id).Name);

        Assert.Null(marcLib.Leave(photo.Id));
        Assert.DoesNotContain(photo.Id, marcLib.Files().Select(m => m.Id));
        Assert.Contains(photo.Id, As(alice).Files().Select(m => m.Id));   // reste chez l'émetteur
        Assert.NotNull(marcLib.Leave(photo.Id));                           // plus rien à quitter
    }

    [Fact]
    public void The_sender_can_revoke_and_nobody_else_can_share_it()
    {
        As(alice).Share([photo.Id], marc.Id);
        Assert.NotNull(As(marc).Unshare(photo.Id, marc.Id));              // le destinataire passe par « Ne plus voir », pas par la gestion du partage
        var (n, err) = As(marc).Share([photo.Id], alice.Id);              // et ne repartage pas un média reçu
        Assert.Equal(0, n);

        Assert.Null(As(alice).Unshare(photo.Id, marc.Id));
        Assert.DoesNotContain(photo.Id, As(marc).Files().Select(m => m.Id));
    }

    [Fact]
    public void A_member_without_the_media_page_cannot_receive_a_share()
    {
        var (n, err) = As(alice).Share([photo.Id], sophie.Id);
        Assert.Equal(0, n); Assert.NotNull(err);
        Assert.Contains(As(alice).ShareCandidates([photo.Id]), c => c.Id == sophie.Id && c.Reason != null);
    }

    [Fact]
    public void Deleting_the_media_removes_its_shares()
    {
        As(alice).Share([photo.Id], marc.Id);
        Assert.Equal(1, As(alice).Delete([photo.Id]));
        var store = p.Services.GetRequiredService<JsonStore>();
        Assert.False(store.Read(db => db.MediaShares.Any(s => s.MediaId == photo.Id)));
    }
}
