/* Assistant du premier écran (DEV-POC.md §126) : textes modifiés directement sur la TV, photo déposée sur la TV, saisie du code d'appairage. */
(function (w) {
  'use strict';
  w.LinkiiFirstScreen = {
    // Étape 1 : chaque texte [data-f] de la TV est modifiable ; la valeur part au serveur à chaque frappe (sans re-rendu).
    // Pendant un glisser de fichier, la TV prend la classe « drag » : le champ de fichier transparent qui la couvre reçoit le dépôt.
    tv(root, ref) {
      if (!root || root._fs) return;
      root._fs = 1;
      root.addEventListener('input', e => {
        const f = e.target.closest && e.target.closest('[data-f]');
        if (f) ref.invokeMethodAsync('SetField', f.dataset.f, f.innerText.replace(/\n+$/, ''));
      });
      root.addEventListener('keydown', e => {
        if (e.key === 'Enter' && e.target.dataset && e.target.dataset.f && e.target.dataset.f !== 'title') { e.preventDefault(); e.target.blur(); }
      });
      root.addEventListener('paste', e => {   // texte brut seulement
        if (!e.target.closest || !e.target.closest('[data-f]')) return;
        e.preventDefault();
        document.execCommand('insertText', false, (e.clipboardData || w.clipboardData).getData('text/plain'));
      });
      let n = 0;
      root.addEventListener('dragenter', e => { if (e.dataTransfer && [...e.dataTransfer.types].includes('Files')) { n++; root.classList.add('drag'); } });
      root.addEventListener('dragleave', () => { if (--n <= 0) { n = 0; root.classList.remove('drag'); } });
      root.addEventListener('drop', () => { n = 0; setTimeout(() => root.classList.remove('drag'), 0); });
    },

    // Étape 2 : six cases, passage automatique à la suivante, retour arrière, collage du code entier ; complet -> CodeEntered.
    code(root, ref) {
      if (!root || root._fs) return;
      root._fs = 1;
      const ins = [...root.querySelectorAll('input')];
      const done = () => { const v = ins.map(x => x.value).join(''); if (v.length === ins.length) ref.invokeMethodAsync('CodeEntered', v); };
      const fill = (from, text) => {
        const digits = (text || '').replace(/\D/g, '').slice(0, ins.length - from).split('');
        digits.forEach((c, j) => { ins[from + j].value = c; });
        const next = ins[Math.min(from + digits.length, ins.length - 1)];
        if (next) next.focus();
        done();
      };
      ins.forEach((inp, i) => {
        inp.addEventListener('input', () => {
          const v = inp.value.replace(/\D/g, '');
          if (v.length > 1) { fill(i, v); return; }
          inp.value = v;
          if (v && ins[i + 1]) ins[i + 1].focus();
          done();
        });
        inp.addEventListener('keydown', e => {
          if (e.key === 'Backspace' && !inp.value && ins[i - 1]) { ins[i - 1].value = ''; ins[i - 1].focus(); e.preventDefault(); }
          else if (e.key === 'ArrowLeft' && ins[i - 1]) ins[i - 1].focus();
          else if (e.key === 'ArrowRight' && ins[i + 1]) ins[i + 1].focus();
        });
        inp.addEventListener('paste', e => { e.preventDefault(); fill(i, (e.clipboardData || w.clipboardData).getData('text')); });
        inp.addEventListener('focus', () => inp.select());
      });
      setTimeout(() => ins[0] && ins[0].focus(), 50);
    },

    clearCode(root) {
      if (!root) return;
      const ins = [...root.querySelectorAll('input')];
      ins.forEach(x => { x.value = ''; });
      if (ins[0]) ins[0].focus();
    }
  };
})(window);
