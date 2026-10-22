const customer = (new URLSearchParams(location.search).get('customer') || '').trim();
const name = document.getElementById('FileNameBox');

name.value = customer ? 'invoice-' + customer.toLowerCase().replace(/ /g, '-') : 'invoice';

document.getElementById('SaveButton').addEventListener('click', () =>
  window.invoices.done({ name: name.value, format: document.getElementById('FormatBox').value }));
