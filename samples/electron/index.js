const v2 = new URLSearchParams(location.search).get('v2') === '1';
const status = document.getElementById('status');

if (v2) {
  //The next release: Export on the right, Refresh gone, "Customer" renamed.
  document.getElementById('toolbar').classList.add('v2');
  document.getElementById('RefreshButton').remove();
  document.getElementById('CustomerLabel').textContent = 'Client';
} else {
  document.getElementById('RefreshButton').addEventListener('click', () => status.textContent = 'Refreshed');
}

document.getElementById('ExportButton').addEventListener('click', async () => {
  const result = await window.invoices.exportInvoice(document.getElementById('CustomerBox').value);

  if (result)
    status.textContent = `Saved ${result.name}.${result.format.toLowerCase()}`;
});
