window.briefcaseTheme = {
  focus(id) { document.getElementById(id)?.focus(); },
  restore() { let dark = false; try { dark = localStorage.getItem('briefcase-theme') === 'dark'; } catch {} document.documentElement.classList.toggle('dark', dark); return dark; },
  set(dark) { document.documentElement.classList.toggle('dark', dark); try { localStorage.setItem('briefcase-theme', dark ? 'dark' : 'light'); } catch {} }
};
window.briefcaseTheme.restore();
document.addEventListener('keydown', event => {
  if (event.target?.getAttribute('role') === 'tab' && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) event.preventDefault();
});
