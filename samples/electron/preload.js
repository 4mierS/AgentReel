const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('invoices', {
  exportInvoice: customer => ipcRenderer.invoke('export', customer),
  done: result => ipcRenderer.send('export-done', result)
});
