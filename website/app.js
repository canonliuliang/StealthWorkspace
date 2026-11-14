// Progressive enhancement only. Navigation, downloads and FAQs work without JS.
document.querySelectorAll('details').forEach(details => {
  details.addEventListener('toggle', () => {
    if (!details.open) return;
    document.querySelectorAll('details[open]').forEach(other => {
      if (other !== details) other.open = false;
    });
  });
});
