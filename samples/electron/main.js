// "Invoices" in Electron: the same walkthrough as the WPF and WinForms samples, for checking shot lists against
// Chromium's UI Automation tree. With --v2 it plays the next release: Export sits on the right, "Customer" reads
// "Client", Refresh is gone. The Export dialog is a modal window of its own, titled "Export".
// Set GLASSTAKE_FORCE_A11Y=1 to switch Chromium's accessibility tree on at startup instead of on first request.
const { app, BrowserWindow, ipcMain } = require('electron');
const path = require('path');

const v2 = process.argv.includes('--v2');

if (process.env.GLASSTAKE_FORCE_A11Y === '1')
  app.commandLine.appendSwitch('force-renderer-accessibility');

let main;

function open() {
  main = new BrowserWindow({
    width: 640,
    height: 380,
    title: 'Glasstake Sample – Invoices (Electron)' + (v2 ? ' (v2)' : ''),
    backgroundColor: '#f5f5f5',
    webPreferences: { preload: path.join(__dirname, 'preload.js') }
  });

  main.setMenuBarVisibility(false);
  main.loadFile('index.html', { query: { v2: v2 ? '1' : '0' } });

  //The page title would replace the window title; the window keeps its own.
  main.on('page-title-updated', event => event.preventDefault());
}

ipcMain.handle('export', async (_, customer) => {
  const dialog = new BrowserWindow({
    parent: main,
    modal: true,
    width: 360,
    height: 260,
    resizable: false,
    minimizable: false,
    title: 'Export',
    webPreferences: { preload: path.join(__dirname, 'preload.js') }
  });

  dialog.setMenuBarVisibility(false);
  dialog.on('page-title-updated', event => event.preventDefault());
  await dialog.loadFile('export.html', { query: { customer } });

  return new Promise(resolve => {
    ipcMain.once('export-done', (_, result) => {
      resolve(result);
      dialog.close();
    });
    dialog.on('closed', () => resolve(null));
  });
});

app.whenReady().then(open);
app.on('window-all-closed', () => app.quit());
