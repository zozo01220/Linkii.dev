/* Assistant de l'écran (DEV-POC.md §126, §190) : saisie du code d'appairage. */
(function (w) {
  'use strict';
  w.LinkiiFirstScreen = {
    // Six cases, passage automatique à la suivante, retour arrière, collage du code entier ; complet -> CodeEntered.
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
