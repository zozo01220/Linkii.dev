/* Aperçu des bandeaux (éditeur) : une case dont le contenu dépasse défile, comme sur l'écran. Seule la classe « run » est posée par ce script. */
window.bdFit = function () {
  var cs = document.querySelectorAll('.bd-band .bd-cell');
  for (var i = 0; i < cs.length; i++) {
    var mq = cs[i].querySelector('.bd-mq'), mi = mq && mq.firstElementChild;
    if (!mq) continue;
    mq.classList.remove('run');
    if (cs[i].scrollWidth > cs[i].clientWidth + 1) {
      mq.classList.add('run');
      mi.style.setProperty('--dur', Math.round(8 + mi.scrollWidth / 50) + 's');
    }
  }
};
