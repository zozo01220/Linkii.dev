/* Modèles de la galerie de compositions, par secteur (DEV-POC.md §114). Chargé avant compo-editor.js, qui les ajoute à ses modèles de base :
   window.LinkiiCompoTemplates({ T, R, I, Q, W }) -> liste de { name, cat, w, h, bg, make }. Les fabriques de calques (texte, forme, image, QR, widget)
   viennent de l'éditeur : mêmes réglages par défaut, ids tirés au moment de l'ouverture. Sans photo : les images se choisissent dans la médiathèque. */
(function (w) {
  'use strict';
  w.LinkiiCompoTemplates = function (f) {
    const { T, R, I, Q, W } = f;
    const ticker = (text, bg, color, font) => W('ticker', { x: 0, y: 980, w: 1920, h: 100, bg, color, font: font || 'Poppins', text });

    return [
      // ---------- Restauration ----------
      { name: 'Menu du jour', cat: 'Restauration', w: 1920, h: 1080, bg: '#FBF6EC', make: () => {
        const red = '#8C2F1B', gold = '#B0802F', els = [
          I({ x: 0, y: 0, w: 640, h: 1080 }),
          T({ x: 720, y: 80, w: 1100, text: 'Menu du jour', size: 110, weight: 800, color: red, font: 'Playfair Display' }),
          W('date', { x: 720, y: 225, w: 800, size: 36, weight: 500, color: '#7A6A55', font: 'Poppins' }),
          R({ x: 720, y: 290, w: 1100, h: 4, fill: '#C9A66B' })];
        [['ENTRÉE', 'Velouté de saison', 'Crème légère et croûtons dorés'], ['PLAT', 'Filet de poisson, risotto', 'Légumes croquants et sauce au citron'], ['DESSERT', 'Tarte du chef', 'Fruits frais et crème vanillée']].forEach(([l, d, s], i) => {
          const y = 340 + i * 220;
          els.push(T({ x: 720, y, w: 600, text: l, size: 30, weight: 700, ls: .2, color: gold, font: 'Poppins' }),
            T({ x: 720, y: y + 42, w: 1100, text: d, size: 56, weight: 600, color: '#2B2118', font: 'Playfair Display' }),
            T({ x: 720, y: y + 118, w: 1100, text: s, size: 32, color: '#6B5B48', font: 'Poppins' }));
        });
        els.push(R({ x: 1280, y: 960, w: 540, h: 90, fill: red, radius: 45 }),
          T({ x: 1280, y: 978, w: 540, text: 'Menu complet 24.–', size: 40, weight: 600, color: '#ffffff', align: 'center', font: 'Poppins' }));
        return els;
      } },
      { name: 'Happy hour', cat: 'Restauration', w: 1080, h: 1920, bg: '#1A1033', make: () => {
        const pk = '#FF3D81', els = [
          R({ kind: 'circle', x: 560, y: -240, w: 860, h: 860, fill: '#2B1A55' }),
          T({ x: 80, y: 150, w: 920, text: 'HAPPY\nHOUR', size: 230, weight: 700, lh: .9, color: pk, font: 'Oswald' }),
          T({ x: 80, y: 690, w: 920, text: 'Tous les jours · 17 h – 19 h', size: 48, weight: 600, color: '#FFD166', font: 'Poppins' })];
        [['Cocktail maison', '8.–'], ['Bière pression 3 dl', '4.–'], ['Verre de vin', '5.–'], ['Planche à partager', '12.–']].forEach(([n, p], i) => {
          const y = 810 + i * 170;
          els.push(R({ x: 80, y, w: 920, h: 140, fill: '#2B1A55', radius: 24 }), T({ x: 120, y: y + 38, w: 640, text: n, size: 48, weight: 600, color: '#ffffff', font: 'Poppins' }),
            T({ x: 760, y: y + 32, w: 200, text: p, size: 58, weight: 800, color: pk, align: 'right', font: 'Poppins' }));
        });
        els.push(T({ x: 80, y: 1520, w: 920, text: 'Offre valable sur place, jusqu\'à épuisement du stock.', size: 34, color: '#B9A7E8', font: 'Poppins' }),
          Q({ x: 80, y: 1610, w: 220, h: 220 }), T({ x: 340, y: 1680, w: 640, text: 'Réservez\nvotre table', size: 54, weight: 600, color: '#ffffff', font: 'Poppins' }));
        return els;
      } },

      // ---------- Hôtellerie ----------
      { name: "Bienvenue à l'hôtel", cat: 'Hôtellerie', w: 1920, h: 1080, bg: '#F4EFE6', make: () => {
        const navy = '#1B2A41', gold = '#B08D57', els = [
          I({ x: 1100, y: 0, w: 820, h: 1080 }),
          T({ x: 100, y: 130, w: 960, text: 'Bienvenue', size: 150, weight: 700, color: navy, font: 'Playfair Display' }),
          T({ x: 100, y: 350, w: 900, text: 'Nous vous souhaitons un agréable séjour.', size: 48, weight: 300, color: '#5B6573', font: 'Poppins' }),
          R({ x: 100, y: 500, w: 160, h: 6, fill: gold })];
        [['Petit-déjeuner', '07:00 – 10:30'], ['Départ (check-out)', 'avant 11:00'], ['Wi-Fi', 'Hotel-Invite']].forEach(([l, v], i) => {
          const y = 570 + i * 130;
          els.push(R({ x: 100, y, w: 900, h: 100, fill: '#ffffff', radius: 16 }), T({ x: 130, y: y + 27, w: 440, text: l, size: 38, weight: 600, color: navy, font: 'Poppins' }),
            T({ x: 570, y: y + 27, w: 400, text: v, size: 38, weight: 500, color: '#8A6A35', align: 'right', font: 'Poppins' }));
        });
        els.push(W('clock', { x: 100, y: 965, w: 300, size: 60, weight: 600, color: navy, font: 'Poppins' }), W('date', { x: 440, y: 985, w: 560, size: 34, weight: 400, color: '#5B6573', align: 'right', font: 'Poppins' }));
        return els;
      } },
      { name: 'Nos services', cat: 'Hôtellerie', w: 1080, h: 1920, bg: '#1B2A41', make: () => {
        const gold = '#C9A96B', els = [
          T({ x: 80, y: 130, w: 920, text: 'Nos services', size: 120, weight: 700, color: '#ffffff', font: 'Playfair Display' }),
          R({ x: 80, y: 300, w: 160, h: 6, fill: gold }),
          T({ x: 80, y: 330, w: 920, text: 'À votre disposition tout au long du séjour', size: 38, weight: 300, color: '#B9C4D4', font: 'Poppins' })];
        [['☕', 'Petit-déjeuner', '07:00 – 10:30'], ['♨', 'Spa et piscine', '09:00 – 21:00'], ['♥', 'Restaurant', '19:00 – 22:00'], ['★', 'Bar du hall', '17:00 – 00:00'], ['☎', 'Réception', '24 h / 24']].forEach(([ic, n, h], i) => {
          const y = 440 + i * 215;
          els.push(R({ x: 80, y, w: 920, h: 180, fill: '#24385A', radius: 24 }), R({ kind: 'circle', x: 120, y: y + 40, w: 100, h: 100, fill: gold }),
            T({ x: 120, y: y + 56, w: 100, text: ic, size: 54, color: '#1B2A41', align: 'center', font: 'Arial' }),
            T({ x: 260, y: y + 34, w: 700, text: n, size: 50, weight: 600, color: '#ffffff', font: 'Poppins' }), T({ x: 260, y: y + 102, w: 700, text: h, size: 38, color: '#E3C48D', font: 'Poppins' }));
        });
        els.push(T({ x: 80, y: 1560, w: 920, text: 'Composez le 9 depuis votre chambre pour joindre la réception.', size: 38, color: '#B9C4D4', align: 'center', font: 'Poppins' }),
          W('clock', { x: 80, y: 1720, w: 920, size: 100, weight: 700, color: gold, align: 'center', font: 'Poppins' }));
        return els;
      } },

      // ---------- Sport ----------
      { name: 'Résultat du match', cat: 'Sport', w: 1920, h: 1080, bg: '#0B3D2E', make: () => [
        R({ x: 0, y: 0, w: 1920, h: 140, fill: '#082A20' }),
        T({ x: 80, y: 44, w: 900, text: 'CHAMPIONNAT · JOURNÉE 12', size: 40, weight: 700, ls: .12, color: '#F2C94C', font: 'Montserrat' }),
        W('date', { x: 1060, y: 50, w: 780, size: 34, weight: 500, color: '#ffffff', align: 'right', font: 'Montserrat' }),
        I({ x: 200, y: 250, w: 320, h: 320, fit: 'contain' }), I({ x: 1400, y: 250, w: 320, h: 320, fit: 'contain' }),
        T({ x: 100, y: 610, w: 520, text: 'Équipe locale', size: 58, weight: 800, color: '#ffffff', align: 'center', font: 'Montserrat' }),
        T({ x: 1300, y: 610, w: 520, text: 'Visiteurs', size: 58, weight: 800, color: '#ffffff', align: 'center', font: 'Montserrat' }),
        T({ x: 620, y: 260, w: 300, text: '3', size: 340, weight: 900, color: '#ffffff', align: 'center', font: 'Montserrat' }),
        T({ x: 1000, y: 260, w: 300, text: '1', size: 340, weight: 900, color: '#ffffff', align: 'center', font: 'Montserrat' }),
        T({ x: 880, y: 330, w: 160, text: '–', size: 200, weight: 700, color: '#F2C94C', align: 'center', font: 'Montserrat' }),
        R({ x: 760, y: 770, w: 400, h: 80, fill: '#F2C94C', radius: 40 }),
        T({ x: 760, y: 788, w: 400, text: 'FIN DU MATCH', size: 36, weight: 700, ls: .1, color: '#082A20', align: 'center', font: 'Montserrat' }),
        ticker("Prochain match : samedi à 15 h · Merci à nos supporters · Buvette ouverte jusqu'à 18 h", '#082A20', '#ffffff', 'Montserrat')] },
      { name: 'Cours collectifs', cat: 'Sport', w: 1920, h: 1080, bg: '#111111', make: () => {
        const lime = '#C6FF00', els = [
          T({ x: 100, y: 60, w: 1100, text: 'COURS DU JOUR', size: 130, weight: 700, color: lime, font: 'Oswald' }),
          W('date', { x: 1200, y: 120, w: 620, size: 38, weight: 500, color: '#ffffff', align: 'right', font: 'Montserrat' })];
        [['07:00', 'Cardio Boost', 'Coach Julie · Salle 1'], ['09:00', 'Yoga Flow', 'Coach Marc · Salle 2'], ['12:15', 'Cross Training', 'Coach Sam · Salle 1'], ['18:00', 'Body Pump', 'Coach Léa · Salle 1'], ['19:30', 'Zumba', 'Coach Ana · Salle 2']].forEach(([h, c, s], i) => {
          const y = 270 + i * 135;
          els.push(R({ x: 100, y, w: 1720, h: 110, fill: '#1E1E1E', radius: 16 }), T({ x: 140, y: y + 20, w: 260, text: h, size: 60, weight: 700, color: lime, font: 'Oswald' }),
            T({ x: 440, y: y + 26, w: 780, text: c, size: 54, weight: 600, color: '#ffffff', font: 'Montserrat' }), T({ x: 1250, y: y + 36, w: 530, text: s, size: 36, color: '#A3A3A3', align: 'right', font: 'Montserrat' }));
        });
        els.push(ticker("Réservation obligatoire via l'application · Pensez à apporter votre serviette", lime, '#111111', 'Montserrat'));
        return els;
      } },

      // ---------- Entreprise ----------
      { name: 'Réunions du jour', cat: 'Entreprise', w: 1920, h: 1080, bg: '#F3F5F9', make: () => {
        const navy = '#14284B', blue = '#2F6FED', els = [
          R({ x: 0, y: 0, w: 1920, h: 180, fill: navy }),
          T({ x: 100, y: 48, w: 900, text: 'Réunions du jour', size: 84, weight: 800, color: '#ffffff', font: 'Montserrat' }),
          W('date', { x: 1000, y: 78, w: 430, size: 34, weight: 400, color: '#A9BBD9', align: 'right', font: 'Montserrat' }),
          W('clock', { x: 1500, y: 48, w: 320, size: 84, weight: 700, color: '#ffffff', align: 'right', font: 'Montserrat' })];
        [['HEURE', 140], ['SALLE', 440], ['RÉUNION', 800], ['ORGANISATEUR', 1450]].forEach(([l, x]) => els.push(T({ x, y: 214, w: 330, text: l, size: 26, weight: 700, ls: .15, color: '#6B7A93', font: 'Montserrat' })));
        [['08:30', 'Salle A', "Point d'équipe", 'M. Dupont'], ['10:00', 'Salle B', 'Comité de pilotage', 'Mme Martin'], ['11:30', 'Salle C', 'Entretien candidat', 'RH'], ['14:00', 'Salle A', 'Revue budgétaire', 'Finances'], ['16:00', 'Salle B', 'Formation sécurité', 'M. Rochat']].forEach(([h, s, t, o], i) => {
          const y = 270 + i * 150;
          els.push(R({ x: 100, y, w: 1720, h: 120, fill: '#ffffff', radius: 16 }), T({ x: 140, y: y + 28, w: 260, text: h, size: 54, weight: 700, color: navy, font: 'Montserrat' }),
            R({ x: 440, y: y + 30, w: 280, h: 60, fill: '#E3ECFF', radius: 30 }), T({ x: 440, y: y + 39, w: 280, text: s, size: 34, weight: 600, color: blue, align: 'center', font: 'Montserrat' }),
            T({ x: 800, y: y + 33, w: 620, text: t, size: 48, weight: 600, color: navy, font: 'Montserrat' }), T({ x: 1450, y: y + 41, w: 330, text: o, size: 36, color: '#6B7A93', font: 'Montserrat' }));
        });
        return els;
      } },
      { name: 'Employé du mois', cat: 'Entreprise', w: 1920, h: 1080, bg: '#FFF7E6', make: () => [
        R({ kind: 'circle', x: 100, y: 170, w: 700, h: 700, fill: '#FFD8A8' }),
        I({ x: 130, y: 200, w: 640, h: 640, radius: 320 }),
        T({ x: 900, y: 190, w: 920, text: 'EMPLOYÉ·E DU MOIS', size: 40, weight: 700, ls: .2, color: '#E8590C', font: 'Montserrat' }),
        T({ x: 900, y: 260, w: 940, text: 'Prénom Nom', size: 120, weight: 800, lh: 1, color: '#14284B', font: 'Playfair Display' }),
        R({ x: 905, y: 460, w: 140, h: 8, fill: '#E8590C' }),
        T({ x: 900, y: 510, w: 900, text: '« Un grand merci pour votre engagement et votre bonne humeur au quotidien. »', size: 48, italic: true, lh: 1.3, color: '#5A4630', font: 'Poppins' }),
        T({ x: 900, y: 800, w: 900, text: 'Service clientèle · Octobre', size: 40, weight: 600, color: '#E8590C', font: 'Poppins' }),
        T({ x: 900, y: 880, w: 900, text: '★★★★★', size: 64, color: '#F2B134', font: 'Arial' })] },
      { name: 'Jours sans accident', cat: 'Entreprise', w: 1920, h: 1080, bg: '#0F5132', make: () => {
        const els = [
          T({ x: 100, y: 90, w: 1000, text: 'SÉCURITÉ AU TRAVAIL', size: 44, weight: 700, ls: .2, color: '#BFE8CF', font: 'Montserrat' }),
          T({ x: 100, y: 170, w: 1100, text: '127', size: 560, weight: 700, lh: 1, color: '#ffffff', font: 'Oswald' }),
          T({ x: 100, y: 790, w: 1100, text: 'jours sans accident', size: 90, weight: 700, color: '#BFE8CF', font: 'Montserrat' }),
          R({ x: 1300, y: 160, w: 520, h: 760, fill: '#14693F', radius: 32 }),
          T({ x: 1340, y: 205, w: 440, text: "Les 3 règles d'or", size: 46, weight: 700, color: '#ffffff', font: 'Montserrat' })];
        ['1. Portez toujours\nvos équipements', '2. Signalez tout\nrisque ou incident', '3. Ne courez pas,\nrestez attentif'].forEach((t, i) => els.push(
          R({ x: 1340, y: 320 + i * 190, w: 440, h: 4, fill: '#2E8B5E' }), T({ x: 1340, y: 345 + i * 190, w: 440, text: t, size: 38, weight: 500, lh: 1.3, color: '#ffffff', font: 'Montserrat' })));
        els.push(ticker("Un doute ? Arrêtez et demandez à votre responsable · Numéro d'urgence interne : 112", '#082E1C', '#ffffff', 'Montserrat'));
        return els;
      } },

      // ---------- Commerce ----------
      { name: 'Soldes', cat: 'Commerce', w: 1920, h: 1080, bg: '#E3173E', make: () => [
        T({ x: 0, y: 60, w: 1920, text: 'SOLDES', size: 300, weight: 900, color: '#ffffff', align: 'center', font: 'Montserrat' }),
        R({ x: 560, y: 430, w: 800, h: 340, fill: '#ffffff', radius: 40 }),
        T({ x: 560, y: 450, w: 800, text: "jusqu'à", size: 50, weight: 600, color: '#E3173E', align: 'center', font: 'Montserrat' }),
        T({ x: 560, y: 500, w: 800, text: '-50 %', size: 220, weight: 900, color: '#E3173E', align: 'center', font: 'Montserrat' }),
        T({ x: 160, y: 830, w: 1600, text: 'Du 1er au 31 janvier · En magasin et en ligne', size: 52, weight: 500, color: '#ffffff', align: 'center', font: 'Poppins' }),
        T({ x: 480, y: 940, w: 500, text: 'Plus que', size: 46, weight: 500, color: '#ffffff', align: 'right', font: 'Poppins' }),
        W('countdown', { x: 1010, y: 915, w: 330, size: 96, weight: 800, color: '#ffffff', font: 'Montserrat' })] },
      { name: 'Produit vedette', cat: 'Commerce', w: 1920, h: 1080, bg: '#F6F6F4', make: () => [
        I({ x: 0, y: 0, w: 1100, h: 1080 }),
        R({ x: 1180, y: 130, w: 280, h: 64, fill: '#FF5A36', radius: 32 }),
        T({ x: 1180, y: 145, w: 280, text: 'NOUVEAUTÉ', size: 30, weight: 700, ls: .15, color: '#ffffff', align: 'center', font: 'Montserrat' }),
        T({ x: 1180, y: 250, w: 660, text: 'Nom du\nproduit', size: 110, weight: 800, lh: 1, color: '#111111', font: 'Montserrat' }),
        T({ x: 1180, y: 510, w: 640, text: 'Une courte description qui met en avant ses points forts.', size: 38, color: '#5A5A55', font: 'IBM Plex Sans' }),
        T({ x: 1180, y: 700, w: 640, text: '89.–', size: 200, weight: 900, color: '#FF5A36', font: 'Montserrat' }),
        T({ x: 1180, y: 940, w: 440, text: 'au lieu de 119.–', size: 38, color: '#8A8A85', font: 'IBM Plex Sans' }),
        Q({ x: 1660, y: 880, w: 160, h: 160 })] },

      // ---------- Santé ----------
      { name: "Salle d'attente", cat: 'Santé', w: 1920, h: 1080, bg: '#EAF5F4', make: () => [
        R({ x: 0, y: 0, w: 1920, h: 170, fill: '#0E7C7B' }),
        T({ x: 100, y: 42, w: 1200, text: 'Bienvenue au cabinet', size: 80, weight: 800, color: '#ffffff', font: 'Montserrat' }),
        W('clock', { x: 1500, y: 42, w: 320, size: 80, weight: 700, color: '#ffffff', align: 'right', font: 'Montserrat' }),
        R({ x: 100, y: 230, w: 860, h: 380, fill: '#ffffff', radius: 28 }),
        T({ x: 140, y: 270, w: 780, text: "Temps d'attente estimé", size: 36, weight: 600, color: '#5B7B7A', font: 'Montserrat' }),
        T({ x: 140, y: 340, w: 780, text: '~ 15 min', size: 150, weight: 900, color: '#0E7C7B', font: 'Montserrat' }),
        T({ x: 140, y: 530, w: 780, text: 'Merci de votre patience.', size: 38, color: '#5B7B7A', font: 'Montserrat' }),
        R({ x: 1000, y: 230, w: 820, h: 380, fill: '#ffffff', radius: 28 }),
        T({ x: 1040, y: 270, w: 740, text: 'Horaires', size: 36, weight: 600, color: '#5B7B7A', font: 'Montserrat' }),
        T({ x: 1040, y: 340, w: 740, text: 'Lun – Ven   08:00 – 18:00\nSamedi   08:00 – 12:00\nUrgences   079 000 00 00', size: 38, weight: 500, lh: 1.55, color: '#16403F', font: 'Montserrat' }),
        R({ x: 100, y: 650, w: 1720, h: 280, fill: '#CFE9E7', radius: 28 }),
        Q({ x: 140, y: 680, w: 220, h: 220 }),
        T({ x: 400, y: 690, w: 1300, text: 'Prenez votre prochain rendez-vous en ligne', size: 56, weight: 700, color: '#0B4F4E', font: 'Montserrat' }),
        T({ x: 400, y: 830, w: 1300, text: "Scannez le code · Pensez à votre carte d'assuré", size: 38, color: '#16403F', font: 'Montserrat' }),
        ticker("Merci d'éteindre votre téléphone portable · Désinfectez-vous les mains à l'entrée", '#0B4F4E', '#ffffff', 'Montserrat')] },
      { name: "Gestes d'hygiène", cat: 'Santé', w: 1920, h: 1080, bg: '#FFFFFF', make: () => {
        const blue = '#1565C0', els = [
          T({ x: 100, y: 80, w: 1600, text: 'Les bons gestes', size: 100, weight: 800, color: '#0D3B75', font: 'Montserrat' }),
          T({ x: 100, y: 210, w: 1600, text: 'Pour votre santé et celle des autres', size: 42, color: '#5A6B80', font: 'IBM Plex Sans' })];
        [['Se laver\nles mains', 'Pendant au moins\n30 secondes'], ['Tousser dans\nson coude', 'Plutôt que\ndans la main'], ['Aérer\nrégulièrement', 'Plusieurs fois\npar jour'], ['Garder\nses distances', 'En cas de\nsymptômes']].forEach(([t, d], i) => {
          const x = 100 + i * 445;
          els.push(R({ x, y: 330, w: 405, h: 570, fill: '#EAF2FC', radius: 28 }), R({ kind: 'circle', x: x + 117, y: 370, w: 170, h: 170, fill: blue }),
            T({ x: x + 117, y: 395, w: 170, text: String(i + 1), size: 100, weight: 800, color: '#ffffff', align: 'center', font: 'Montserrat' }),
            T({ x: x + 20, y: 580, w: 365, text: t, size: 44, weight: 700, lh: 1.1, color: '#0D3B75', align: 'center', font: 'Montserrat' }),
            T({ x: x + 20, y: 740, w: 365, text: d, size: 32, lh: 1.25, color: '#5A6B80', align: 'center', font: 'IBM Plex Sans' }));
        });
        els.push(ticker('Merci de votre collaboration · Prenez soin de vous', blue, '#ffffff', 'Montserrat'));
        return els;
      } },

      // ---------- Éducation ----------
      { name: 'Menu de la cantine', cat: 'Éducation', w: 1920, h: 1080, bg: '#FFF9E8', make: () => {
        const els = [
          R({ x: 0, y: 0, w: 1920, h: 170, fill: '#2E7D32' }),
          T({ x: 100, y: 42, w: 1100, text: 'Menu de la semaine', size: 80, weight: 800, color: '#ffffff', font: 'Montserrat' }),
          W('date', { x: 1200, y: 64, w: 620, size: 38, weight: 500, color: '#ffffff', align: 'right', font: 'Montserrat' })];
        [['LUNDI', 'Salade de carottes', 'Poulet rôti\nRiz pilaf', 'Yaourt aux fruits'], ['MARDI', 'Soupe de légumes', 'Spaghetti\nbolognaise', 'Pomme'], ['MERCREDI', 'Concombre\nà la crème', 'Filet de poisson\nPommes vapeur', 'Compote maison'],
          ['JEUDI', 'Salade verte', 'Émincé de dinde\nPurée', 'Fromage blanc'], ['VENDREDI', 'Taboulé', 'Pizza margherita\nSalade', 'Glace vanille']].forEach(([d, e, p, s], i) => {
          const x = 100 + i * 348;
          els.push(R({ x, y: 220, w: 330, h: 700, fill: '#ffffff', radius: 24 }), R({ x, y: 220, w: 330, h: 90, fill: '#F2B134', radius: 24 }),
            T({ x, y: 245, w: 330, text: d, size: 40, weight: 800, color: '#3B2A00', align: 'center', font: 'Montserrat' }),
            T({ x: x + 20, y: 340, w: 290, text: e, size: 34, weight: 500, lh: 1.25, color: '#2B2B2B', align: 'center', font: 'Montserrat' }),
            R({ x: x + 125, y: 470, w: 80, h: 4, fill: '#C8E6C9' }),
            T({ x: x + 20, y: 510, w: 290, text: p, size: 36, weight: 700, lh: 1.25, color: '#1B5E20', align: 'center', font: 'Montserrat' }),
            R({ x: x + 125, y: 690, w: 80, h: 4, fill: '#C8E6C9' }),
            T({ x: x + 20, y: 730, w: 290, text: s, size: 34, weight: 500, lh: 1.25, color: '#2B2B2B', align: 'center', font: 'Montserrat' }));
        });
        els.push(T({ x: 100, y: 965, w: 1720, text: 'Menu végétarien disponible sur demande · Allergènes : voir le secrétariat', size: 34, color: '#5A6B80', align: 'center', font: 'Montserrat' }));
        return els;
      } },

      // ---------- Culture ----------
      { name: 'Exposition', cat: 'Culture', w: 1080, h: 1920, bg: '#1C1C1C', make: () => [
        I({ x: 0, y: 0, w: 1080, h: 1100 }),
        R({ x: 0, y: 1100, w: 1080, h: 820, fill: '#1C1C1C' }),
        T({ x: 80, y: 1165, w: 920, text: 'EXPOSITION', size: 36, weight: 700, ls: .3, color: '#E8C26B', font: 'Montserrat' }),
        T({ x: 80, y: 1230, w: 920, text: "Titre de\nl'exposition", size: 120, weight: 800, lh: .95, color: '#ffffff', font: 'Playfair Display' }),
        T({ x: 80, y: 1500, w: 920, text: 'Du 5 mars au 28 juin\nMardi – dimanche, 10 h – 18 h', size: 42, lh: 1.4, color: '#D6D6D6', font: 'IBM Plex Sans' }),
        Q({ x: 80, y: 1670, w: 190, h: 190 }),
        T({ x: 310, y: 1715, w: 680, text: 'Billets et visites guidées', size: 46, weight: 600, color: '#ffffff', font: 'IBM Plex Sans' })] },
      { name: 'À l\'affiche', cat: 'Culture', w: 1920, h: 1080, bg: '#0D0D12', make: () => {
        const els = [
          T({ x: 100, y: 70, w: 1100, text: "À L'AFFICHE", size: 120, weight: 700, color: '#ffffff', font: 'Oswald' }),
          W('clock', { x: 1500, y: 90, w: 320, size: 84, weight: 600, color: '#E50914', align: 'right', font: 'Oswald' })];
        [['Titre du film', 'VF · 2 h 05', '14:00 · 16:30 · 19:00'], ['Un autre titre', 'VO st. · 1 h 48', '15:15 · 20:30'], ['Film pour enfants', 'VF · 1 h 30', '13:30 · 15:45'], ['Soirée spéciale', 'VO st. · 2 h 20', '21:00']].forEach(([t, m, h], i) => {
          const y = 260 + i * 170;
          els.push(R({ x: 100, y, w: 1720, h: 140, fill: '#1A1A22', radius: 20 }), R({ x: 100, y, w: 14, h: 140, fill: '#E50914' }),
            T({ x: 160, y: y + 38, w: 760, text: t, size: 56, weight: 700, color: '#ffffff', font: 'Montserrat' }), T({ x: 940, y: y + 52, w: 380, text: m, size: 34, color: '#9A9AA5', font: 'Montserrat' }),
            T({ x: 1300, y: y + 46, w: 480, text: h, size: 44, weight: 700, color: '#ffffff', align: 'right', font: 'Oswald' }));
        });
        els.push(ticker('Tarif réduit pour les moins de 16 ans · Le café du cinéma est ouvert dès 13 h', '#E50914', '#ffffff', 'Montserrat'));
        return els;
      } },

      // ---------- Immobilier ----------
      { name: 'Bien à vendre', cat: 'Immobilier', w: 1920, h: 1080, bg: '#12263F', make: () => {
        const gold = '#C9A66B', els = [
          I({ x: 0, y: 0, w: 1200, h: 1080 }),
          T({ x: 1260, y: 90, w: 600, text: 'À VENDRE', size: 36, weight: 700, ls: .25, color: gold, font: 'Montserrat' }),
          T({ x: 1260, y: 150, w: 620, text: 'Appartement\n4,5 pièces', size: 72, weight: 700, lh: 1.05, color: '#ffffff', font: 'Playfair Display' }),
          T({ x: 1260, y: 330, w: 620, text: 'Lausanne · Quartier calme', size: 36, color: '#B9C6D8', font: 'IBM Plex Sans' })];
        [['120 m²', 'surface habitable'], ['4,5', 'pièces'], ['Balcon', 'plein sud']].forEach(([v, l], i) => els.push(
          R({ x: 1260, y: 420 + i * 120, w: 580, h: 2, fill: '#2C4666' }), T({ x: 1260, y: 445 + i * 120, w: 280, text: v, size: 56, weight: 700, color: '#ffffff', font: 'IBM Plex Sans' }),
          T({ x: 1550, y: 462 + i * 120, w: 290, text: l, size: 34, color: '#B9C6D8', font: 'IBM Plex Sans' })));
        els.push(T({ x: 1260, y: 790, w: 620, text: 'CHF 995 000.–', size: 68, weight: 800, color: gold, font: 'Montserrat' }),
          Q({ x: 1260, y: 900, w: 140, h: 140 }), T({ x: 1430, y: 940, w: 440, text: 'Scannez pour\nvisiter en ligne', size: 34, lh: 1.25, color: '#ffffff', font: 'IBM Plex Sans' }));
        return els;
      } },

      // ---------- Collectivité ----------
      { name: 'Avis à la population', cat: 'Collectivité', w: 1920, h: 1080, bg: '#F1F4F8', make: () => [
        R({ x: 0, y: 0, w: 1920, h: 200, fill: '#1F4E8C' }),
        I({ x: 80, y: 30, w: 140, h: 140, fit: 'contain' }),
        T({ x: 260, y: 48, w: 1000, text: 'COMMUNE DE VOTRE VILLE', size: 54, weight: 800, color: '#ffffff', font: 'Montserrat' }),
        T({ x: 260, y: 125, w: 1000, text: 'Avis à la population', size: 36, color: '#BBD0EC', font: 'Montserrat' }),
        W('date', { x: 1300, y: 80, w: 520, size: 36, weight: 500, color: '#ffffff', align: 'right', font: 'Montserrat' }),
        T({ x: 140, y: 270, w: 1640, text: 'Travaux de voirie', size: 100, weight: 800, color: '#0F2A4D', font: 'Montserrat' }),
        R({ x: 145, y: 410, w: 200, h: 10, fill: '#E8A200' }),
        T({ x: 140, y: 460, w: 1000, text: 'La rue de la Gare sera fermée à la circulation du 3 au 14 mars. Une déviation est mise en place. Merci de votre compréhension.', size: 46, lh: 1.3, color: '#2B3A4F', font: 'IBM Plex Sans' }),
        R({ x: 1240, y: 460, w: 580, h: 300, fill: '#ffffff', radius: 24 }),
        T({ x: 1280, y: 495, w: 500, text: 'Informations', size: 36, weight: 700, color: '#1F4E8C', font: 'Montserrat' }),
        T({ x: 1280, y: 560, w: 500, text: 'Service des travaux\n021 000 00 00\ninfo@commune.ch', size: 36, lh: 1.4, color: '#2B3A4F', font: 'IBM Plex Sans' }),
        Q({ x: 140, y: 790, w: 170, h: 170 }),
        T({ x: 350, y: 830, w: 800, text: 'Plan des déviations\nsur notre site', size: 42, weight: 600, lh: 1.2, color: '#0F2A4D', font: 'Montserrat' }),
        ticker('Service de piquet 24 h / 24 · Informations sur www.votreville.ch', '#1F4E8C', '#ffffff', 'Montserrat')] },

      // ---------- Transport ----------
      { name: 'Prochains départs', cat: 'Transport', w: 1920, h: 1080, bg: '#0A1A2F', make: () => {
        const y0 = '#FFD100', els = [
          T({ x: 100, y: 50, w: 900, text: 'DÉPARTS', size: 110, weight: 700, color: y0, font: 'Oswald' }),
          W('clock', { x: 1300, y: 56, w: 520, size: 110, weight: 700, color: '#ffffff', align: 'right', font: 'Oswald' })];
        [['LIGNE', 120], ['DESTINATION', 320], ['QUAI', 1250], ['DÉPART', 1480]].forEach(([l, x]) => els.push(T({ x, y: 222, w: 400, text: l, size: 26, weight: 700, ls: .15, color: '#7F93AD', font: 'Montserrat' })));
        [['12', 'Gare centrale', '1', '14:32'], ['3', 'Aéroport', '2', '14:40'], ['7', 'Université', '1', '14:48'], ['12', 'Port', '3', '14:55'], ['21', 'Zone industrielle', '2', '15:02'], ['3', 'Centre-ville', '1', '15:10']].forEach(([n, d, q, h], i) => {
          const y = 270 + i * 112;
          els.push(R({ x: 100, y, w: 1720, h: 92, fill: '#12294A', radius: 12 }), R({ x: 120, y: y + 16, w: 150, h: 60, fill: y0, radius: 12 }),
            T({ x: 120, y: y + 18, w: 150, text: n, size: 40, weight: 800, color: '#0A1A2F', align: 'center', font: 'Montserrat' }),
            T({ x: 320, y: y + 20, w: 880, text: d, size: 46, weight: 600, color: '#ffffff', font: 'Montserrat' }), T({ x: 1250, y: y + 20, w: 180, text: q, size: 46, weight: 700, color: y0, font: 'Montserrat' }),
            T({ x: 1480, y: y + 20, w: 310, text: h, size: 46, weight: 600, color: '#ffffff', align: 'right', font: 'Montserrat' }));
        });
        els.push(ticker('Informations voyageurs : en cas de perturbation, consultez notre site ou notre application', y0, '#0A1A2F', 'Montserrat'));
        return els;
      } },

      // ---------- Beauté et bien-être ----------
      { name: 'Carte des soins', cat: 'Beauté', w: 1920, h: 1080, bg: '#F7EFE9', make: () => {
        const rose = '#B76E79', els = [
          I({ x: 0, y: 0, w: 700, h: 1080 }),
          T({ x: 780, y: 80, w: 1050, text: 'Carte des soins', size: 100, weight: 800, color: '#3B2A2A', font: 'Playfair Display' }),
          T({ x: 780, y: 215, w: 1050, text: 'Institut de beauté · Sur rendez-vous', size: 36, color: rose, font: 'Poppins' })];
        [['Soin du visage', '60 min', '95.–'], ['Massage relaxant', '50 min', '110.–'], ['Manucure', '45 min', '55.–'], ['Soin du corps', '75 min', '140.–'], ['Maquillage', '30 min', '60.–']].forEach(([n, d, p], i) => {
          const y = 300 + i * 125;
          els.push(T({ x: 780, y, w: 760, text: n, size: 46, weight: 600, color: '#3B2A2A', font: 'Playfair Display' }), T({ x: 780, y: y + 60, w: 600, text: d, size: 30, color: '#8A7570', font: 'Poppins' }),
            T({ x: 1560, y: y + 10, w: 260, text: p, size: 50, weight: 700, color: rose, align: 'right', font: 'Poppins' }), R({ x: 780, y: y + 106, w: 1040, h: 2, fill: '#E5D3CB' }));
        });
        els.push(T({ x: 780, y: 975, w: 800, text: 'Réservation : 000 000 00 00', size: 38, weight: 500, color: '#3B2A2A', font: 'Poppins' }), Q({ x: 1700, y: 940, w: 120, h: 120 }));
        return els;
      } }
    ];
  };
})(window);
