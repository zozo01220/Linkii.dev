#!/usr/bin/env bash
# Remet Linkii à zéro : garde Platform (réglages) et le superadmin admin@linkii.local.
# Usage :  sudo ./reset-donnees.sh /chemin/du/dossier/de/donnees [nom-du-service]
# Le dossier de données est celui qui contient data.json (Linkii:DataDir, sinon le dossier de l'appli).
set -euo pipefail

DIR="${1:?Indiquez le dossier qui contient data.json}"
SERVICE="${2:-linkii}"
KEEP_EMAIL="admin@linkii.local"
STAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP="$DIR/../linkii-backup-$STAMP.tar.gz"

[ -f "$DIR/data.json" ] || { echo "data.json introuvable dans $DIR"; exit 1; }
command -v python3 >/dev/null || { echo "python3 requis"; exit 1; }

echo "== Contenu actuel =="
python3 - "$DIR/data.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding="utf-8"))
for k, v in d.items():
    if isinstance(v, list): print(f"  {k}: {len(v)}")
PY

echo
echo "Sera GARDÉ : Platform (réglages), $KEEP_EMAIL, dossiers keys/ brand/ scans/salt.bin"
echo "Sera SUPPRIMÉ : tout le reste (clients, revendeurs, utilisateurs, écrans, médias, listes, journaux...)"
echo "Sauvegarde complète : $BACKUP"
read -r -p "Tapez OUI pour continuer : " ok
[ "$ok" = "OUI" ] || { echo "Annulé."; exit 1; }

echo "== Arrêt du service $SERVICE =="
systemctl stop "$SERVICE"

echo "== Sauvegarde =="
tar -czf "$BACKUP" -C "$DIR" .
chmod 600 "$BACKUP"
ls -lh "$BACKUP"

echo "== Nettoyage de data.json =="
python3 - "$DIR/data.json" "$KEEP_EMAIL" <<'PY'
import json, sys
path, keep = sys.argv[1], sys.argv[2].lower()
d = json.load(open(path, encoding="utf-8"))
users = [u for u in d.get("Users", []) if (u.get("Email") or "").lower() == keep]
if len(users) != 1:
    sys.exit(f"ERREUR : {len(users)} utilisateur(s) {keep} trouvé(s), abandon (data.json non modifié)")
for k, v in list(d.items()):
    if isinstance(v, list): d[k] = []
d["Users"] = users
json.dump(d, open(path + ".new", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
PY
chown --reference="$DIR/data.json" "$DIR/data.json.new"
chmod --reference="$DIR/data.json" "$DIR/data.json.new"
mv "$DIR/data.json.new" "$DIR/data.json"
rm -f "$DIR"/data.json.pre-*.bak

echo "== Suppression des fichiers (médias, vignettes, PDF, captures, journaux de lecture) =="
for sub in media thumbs pdf captures plays; do
  [ -d "$DIR/$sub" ] && find "$DIR/$sub" -mindepth 1 -delete
done

echo "== Redémarrage =="
systemctl start "$SERVICE"
sleep 2
systemctl --no-pager status "$SERVICE" | head -5
echo "Terminé. Retour arrière : systemctl stop $SERVICE && tar -xzf $BACKUP -C $DIR && systemctl start $SERVICE"
