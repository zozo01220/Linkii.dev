namespace Linkii.Poc;

/// <summary>Renvoi des invitations : un nouveau mot de passe provisoire est généré et envoyé par e-mail (comme l'invitation d'origine).</summary>
public static class InviteOps
{
    /// <summary>Résultat par personne : erreur d'envoi éventuelle, et le mot de passe provisoire à communiquer soi-même dans ce cas.</summary>
    public record Result(User User, string? MailError, string Password);

    /// <summary>Personnes invitées (compte jamais connecté, non désactivé) parmi celles données.</summary>
    public static List<Guid> Pending(IEnumerable<User> users) => users.Where(u => u.IsInvited && !u.Disabled).Select(u => u.Id).ToList();

    public static async Task<List<Result>> ResendAsync(TenantStore store, Mailer mail, ResellerResolver resolver, string baseUri, string orgName, IEnumerable<Guid> userIds)
    {
        var results = new List<Result>();
        foreach (var id in userIds.Distinct())
        {
            var pwd = Passwords.Generate();
            var (user, reseller) = store.Write(d =>
            {
                if (!d.Access.IsOrgAdmin) return ((User?)null, (Reseller?)null);
                var u = d.Users.FirstOrDefault(x => x.Id == id);
                if (u == null || !u.IsInvited || u.Disabled) return (null, null);
                u.PasswordHash = Passwords.Hash(pwd);
                u.MustChangePassword = true;
                return (u, d.Reseller);
            });
            if (user == null || reseller == null) continue;
            var err = await mail.SendWelcome(reseller, resolver.LoginUrl(reseller, baseUri), $"Vous avez été invité à gérer les écrans de « {orgName} ».", user.Name, user.Email, pwd);
            results.Add(new Result(user, err, pwd));
        }
        return results;
    }
}
