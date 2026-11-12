// Assemble la maquette « Organisation, Statistiques, Historique allégées (mode Expert) » : app.css du jour (nouveau menu, nouvelle barre) + expert-part.src.
// node expert-build.js
const fs = require('fs');
const ROOT = 'C:/DEV/En-cours/Linkii', MQ = ROOT + '/maquettes/';
const ref = fs.readFileSync(MQ + 'liste-page-epuree.html', 'utf8').split('\n');
const css = fs.readFileSync(ROOT + '/Linkii.Poc/wwwroot/app.css', 'utf8');
const brand = ref.slice(9674, 9679).join('\n');
if (!brand.includes('class="brand"')) throw new Error('bloc du logo introuvable');
const part = fs.readFileSync(__dirname + '/expert-part.src', 'utf8');
if (!part.includes('<!--BRAND-->')) throw new Error('repère du logo absent');
const head = ref.slice(0, 5).join('\n') + '\n<title>Organisation, Statistiques, Historique allégées (mode Expert)</title>\n' + ref[6] + "\n<style>/* app.css intégré pour que la maquette s'ouvre seule */\n";
const file = 'expert-pages-allegees.html';
fs.writeFileSync(MQ + file, head + css + '\n' + part.replace('<!--BRAND-->', () => brand));
console.log(file, Math.round(fs.statSync(MQ + file).size / 1024) + ' Ko');
