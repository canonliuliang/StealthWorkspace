// Progressive enhancement only: downloads, anchors and native details work without JavaScript.
const questions = [...document.querySelectorAll('.faq details')];
questions.forEach(question => {
  question.addEventListener('toggle', () => {
    if (question.open) questions.forEach(other => { if (other !== question) other.open = false; });
  });
});

const menu = document.querySelector('.mobile-menu');
if (menu) {
  menu.querySelectorAll('a').forEach(link => {
    link.addEventListener('click', () => { menu.open = false; });
  });
  menu.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || !menu.open) return;
    menu.open = false;
    menu.querySelector('summary').focus();
    event.preventDefault();
  });
}