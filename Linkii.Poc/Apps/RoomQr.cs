using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Linkii.Poc;

/// <summary>
/// Réservation par QR : sur une tablette non tactile, la porte affiche un QR qui ouvre sur le téléphone <c>/salle/{code}</c>. On y choisit la durée (et un commentaire
/// facultatif), puis on confirme : la réservation passe par le même service que la tablette. Le code est signé avec le jeton de l'écran et change toutes les 5 minutes
/// (le précédent reste valable 5 minutes de plus) : la photo d'un QR ne sert pas longtemps. Aucune réservation n'a lieu à l'ouverture de la page, seulement à la confirmation.
/// </summary>
public static class RoomQr
{
    public const int Minutes = 5;
    public const int MaxComment = 60;

    private static long Bucket(DateTime nowUtc) => new DateTimeOffset(nowUtc, TimeSpan.Zero).ToUnixTimeSeconds() / (Minutes * 60);

    private static string Mac(Screen s, long bucket) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(s.Token ?? ""), Encoding.UTF8.GetBytes($"roomqr|{s.Id:N}|{bucket}")))[..20].ToLowerInvariant();

    /// <summary>Code de l'écran pour l'instant donné : « identifiant-signature ».</summary>
    public static string Code(Screen s, DateTime nowUtc) => $"{s.Id:N}-{Mac(s, Bucket(nowUtc))}";

    /// <summary>L'écran de salle d'un code encore valable (le courant ou le précédent), sinon null.</summary>
    public static Screen? Resolve(Db db, string? code, DateTime nowUtc)
    {
        var parts = (code ?? "").Split('-');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id)) return null;
        var s = db.Screens.FirstOrDefault(x => x.Id == id && x.Token != null && x.Room != null);
        if (s == null) return null;
        var given = Encoding.ASCII.GetBytes(parts[1].ToLowerInvariant());
        var b = Bucket(nowUtc);
        foreach (var k in new[] { b, b - 1 })
            if (CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(Mac(s, k)))) return s;
        return null;
    }

    /// <summary>Commentaire nettoyé (sans caractères de contrôle, 60 caractères) ; null s'il est vide.</summary>
    public static string? CleanComment(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return null;
        var t = new string(c.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (t.Length > MaxComment) t = t[..MaxComment].TrimEnd();
        return t.Length == 0 ? null : t;
    }

    public record Option(int Min, string Label, bool Ok, string? Why, string Span);

    /// <summary>Page du téléphone : durées (grisées avec leur raison), commentaire, confirmation. Rendue par le serveur, sans compte.</summary>
    public static string Page(string code, string roomName, string untilText, List<Option> options, string? error)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Réserver la salle</title>");
        sb.Append("<style>*{box-sizing:border-box}body{margin:0;background:#F3F6FA;color:#0B1F3A;font:16px/1.4 \"IBM Plex Sans\",\"Segoe UI\",Arial,sans-serif}");
        sb.Append("main{max-width:440px;margin:0 auto;padding:22px 18px 28px;display:flex;flex-direction:column;gap:14px;min-height:100vh}");
        sb.Append(".logo{font-weight:500;letter-spacing:-.03em;color:#5A6B80}h1{font-size:22px;font-weight:500;margin:0}.mut{color:#5A6B80}");
        sb.Append(".dur{display:grid;grid-template-columns:1fr 1fr;gap:10px}.dur button{font:inherit;font-weight:600;border:2px solid #DDE3EA;background:#fff;color:#0B1F3A;border-radius:10px;padding:14px 6px;cursor:pointer}");
        sb.Append(".dur button.on{background:#0B1F3A;border-color:#0B1F3A;color:#fff}.dur button:disabled{opacity:.45;cursor:default}.dur small{display:block;font-weight:500;font-size:12px}");
        sb.Append("label{display:block;font-size:14px;color:#5A6B80;margin-bottom:4px}input{width:100%;font:inherit;padding:12px;border:2px solid #DDE3EA;border-radius:10px;background:#fff}");
        sb.Append(".card{background:#fff;border-radius:10px;padding:14px}.card b{font-size:22px;font-weight:600}");
        sb.Append(".ok{margin-top:auto;font:inherit;font-size:17px;font-weight:600;border:0;border-radius:10px;padding:16px;background:#0B1F3A;color:#fff;cursor:pointer}.ok:disabled{opacity:.5}");
        sb.Append(".err{background:#FBE9E7;color:#8A2A1F;border-radius:10px;padding:12px}.done{background:#1E8E5A;color:#fff;text-align:center;justify-content:center}.done b{font-size:56px}</style></head><body>");
        sb.Append("<main id=\"m\"><div class=\"logo\">linkivi.</div>");
        if (error != null || options.Count == 0)
        {
            sb.Append("<h1>").Append(E(roomName)).Append("</h1><div class=\"card mut\">").Append(E(error ?? "La salle n'est pas disponible à la réservation pour le moment.")).Append("</div></main></body></html>");
            return sb.ToString();
        }
        sb.Append("<h1>Réserver la salle ").Append(E(roomName)).Append("</h1><div class=\"mut\">").Append(E(untilText)).Append(" Pour combien de temps ?</div><div class=\"dur\" id=\"dur\">");
        foreach (var o in options)
            sb.Append("<button type=\"button\" data-min=\"").Append(o.Min).Append("\" data-span=\"").Append(E(o.Span)).Append('"').Append(o.Ok ? "" : " disabled").Append('>')
              .Append(E(o.Label)).Append(o.Ok ? "" : "<small>" + E(o.Why) + "</small>").Append("</button>");
        sb.Append("</div><div><label for=\"c\">Commentaire (facultatif)</label><input id=\"c\" maxlength=\"").Append(MaxComment).Append("\" placeholder=\"Ex. : point équipe\" autocomplete=\"off\"></div>");
        sb.Append("<div class=\"card\"><div class=\"mut\">Vous réservez</div><b id=\"span\">…</b></div><div id=\"e\"></div><button class=\"ok\" id=\"go\" disabled>Confirmer la réservation</button></main>");
        sb.Append("<script>(function(){var code=").Append(System.Text.Json.JsonSerializer.Serialize(code)).Append(",sel=0,d=document.getElementById('dur'),go=document.getElementById('go'),sp=document.getElementById('span'),er=document.getElementById('e');");
        sb.Append("d.addEventListener('click',function(ev){var b=ev.target.closest('button');if(!b||b.disabled)return;sel=+b.getAttribute('data-min');[].forEach.call(d.children,function(x){x.className=x===b?'on':''});sp.textContent=b.getAttribute('data-span');go.disabled=false});");
        sb.Append("go.addEventListener('click',function(){if(!sel)return;go.disabled=true;go.textContent='Réservation…';er.innerHTML='';");
        sb.Append("fetch('/salle/'+code+'/book',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({minutes:sel,comment:document.getElementById('c').value})})");
        sb.Append(".then(function(r){return r.json().then(function(j){return{s:r.status,j:j}})}).then(function(x){");
        sb.Append("if(x.s===200){var m=document.getElementById('m'),id=x.j.id;m.className='done';m.innerHTML='<b>✓</b><div style=\"font-size:22px;font-weight:600\">Salle réservée</div><div>jusqu’à '+x.j.until+'</div><button class=\"ok\" id=\"un\" style=\"margin:18px 0 0;background:rgba(255,255,255,.2)\">Annuler la réservation</button><div id=\"ue\"></div>';var un=document.getElementById('un'),t=setTimeout(function(){un.remove()},60000);un.addEventListener('click',function(){un.disabled=true;fetch('/salle/'+code+'/cancel',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:id})}).then(function(r){return r.json().catch(function(){return{}}).then(function(j){return{s:r.status,j:j}})}).then(function(y){clearTimeout(t);if(y.s===200){m.innerHTML='<b>✓</b><div style=\"font-size:22px;font-weight:600\">Réservation annulée</div>'}else{un.disabled=false;document.getElementById('ue').textContent=(y.j&&y.j.error)||'Annulation impossible.'}})})}");
        sb.Append("else{er.innerHTML='<div class=\"err\"></div>';er.firstChild.textContent=(x.j&&x.j.error)||'Réservation impossible.';go.disabled=false;go.textContent='Confirmer la réservation';}})");
        sb.Append(".catch(function(){er.innerHTML='<div class=\"err\">Pas de connexion : réessayez.</div>';go.disabled=false;go.textContent='Confirmer la réservation'})});})();</script></body></html>");
        return sb.ToString();
    }
}

public record RoomQrBookDto(int Minutes, string? Comment);
public record RoomCancelDto(Guid Id);
