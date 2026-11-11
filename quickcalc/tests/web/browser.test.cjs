/* Browser checks using Chrome's DevTools protocol and Node 22+; no npm packages. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { pathToFileURL } = require('node:url');

const root = path.resolve(__dirname, '../..');
const artifacts = path.join(root, '.web-test');
fs.mkdirSync(artifacts, { recursive: true });
const candidates = [
  process.env.QUICKCALC_BROWSER,
  ...(process.argv.includes('--edge') ? ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'] : []),
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  '/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome'
].filter(Boolean);
const executable = candidates.find(candidate => fs.existsSync(candidate));
assert.ok(executable, 'Install Chrome or Edge, or set QUICKCALC_BROWSER to its executable.');
const profile = fs.mkdtempSync(path.join(artifacts, 'profile-'));
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' };
const server = http.createServer((request, response) => {
  const name = request.url === '/' ? 'index.html' : request.url.slice(1);
  if (!['index.html', 'calculator.js', 'app.js', 'styles.css'].includes(name)) { response.writeHead(404); response.end(); return; }
  response.setHeader('Content-Type', mime[path.extname(name)] + '; charset=utf-8');
  response.end(fs.readFileSync(path.join(root, name)));
});

class Connection {
  constructor(url) {
    this.ws = new WebSocket(url);
    this.nextId = 0;
    this.pending = new Map();
    this.exceptions = [];
    this.ready = new Promise((resolve, reject) => { this.ws.onopen = resolve; this.ws.onerror = reject; });
    this.ws.onmessage = event => {
      const message = JSON.parse(event.data);
      if (message.method === 'Runtime.exceptionThrown') this.exceptions.push(message.params.exceptionDetails);
      if (!message.id) return;
      const pending = this.pending.get(message.id);
      if (!pending) return;
      clearTimeout(pending.timer);
      this.pending.delete(message.id);
      if (message.error) pending.reject(new Error(JSON.stringify(message.error)));
      else pending.resolve(message.result);
    };
  }
  async send(method, params = {}, sessionId) {
    await this.ready;
    const id = ++this.nextId;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(`Timed out: ${method}`)); }, 10000);
      this.pending.set(id, { resolve, reject, timer });
      this.ws.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
  }
}

let browser;
let connection;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}/`;
  browser = spawn(executable, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  const endpoint = await new Promise((resolve, reject) => {
    let stderr = '';
    const timer = setTimeout(() => reject(new Error(`Browser did not start: ${stderr.slice(-1500)}`)), 20000);
    browser.once('error', error => { clearTimeout(timer); reject(error); });
    browser.stderr.on('data', chunk => {
      stderr += chunk;
      const match = stderr.match(/DevTools listening on (ws:\/\/\S+)/);
      if (match) { clearTimeout(timer); resolve(match[1]); }
    });
    browser.once('exit', code => { clearTimeout(timer); reject(new Error(`Browser exited ${code}: ${stderr.slice(-1500)}`)); });
  });
  connection = new Connection(endpoint);
  const { targetId } = await connection.send('Target.createTarget', { url: 'about:blank' });
  const { sessionId } = await connection.send('Target.attachToTarget', { targetId, flatten: true });
  const send = (method, params) => connection.send(method, params, sessionId);
  await send('Page.enable');
  await send('Runtime.enable');
  await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'light' }] });
  const evaluate = async expression => {
    const response = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (response.exceptionDetails) throw new Error(response.exceptionDetails.exception?.description || response.exceptionDetails.text);
    return response.result.value;
  };
  async function navigate(destination) {
    await send('Page.navigate', { url: destination });
    for (let i = 0; i < 100; i++) {
      if (await evaluate(`document.readyState === 'complete' && !!window.QuickCalc && document.getElementById('expression') !== null`)) return;
      await delay(50);
    }
    throw new Error('Page did not load');
  }
  const click = id => evaluate(`document.getElementById(${JSON.stringify(id)}).click()`);
  const focus = id => evaluate(`document.getElementById(${JSON.stringify(id)}).focus()`);
  const key = async (value, code = value, modifiers = 0) => {
    const codes = { Enter: 13, ' ': 32, Tab: 9, Escape: 27, ArrowLeft: 37, ArrowUp: 38, ArrowRight: 39, ArrowDown: 40, Home: 36, End: 35, Backspace: 8 };
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: value, code, windowsVirtualKeyCode: codes[value] || 0, modifiers, ...(value === 'Enter' ? { text: '\r' } : {}) });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: value, code, windowsVirtualKeyCode: codes[value] || 0, modifiers });
  };
  const type = text => send('Input.insertText', { text });
  async function expression(text) {
    await focus('expression');
    await evaluate(`document.getElementById('expression').select()`);
    await type(text);
  }
  const result = () => evaluate(`document.getElementById('result').value`);
  let checks = 0;
  async function check(name, action) { await action(); checks++; console.log(`PASS ${name}`); }
  async function checkContrast() {
    const failures = await evaluate(`(() => {
      const style = getComputedStyle(document.documentElement);
      const luminance = name => {
        const color = style.getPropertyValue('--' + name).trim().slice(1);
        const channels = color.match(/../g).map(value => parseInt(value, 16) / 255).map(value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4);
        return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
      };
      const pairs = [['text', 'page'], ['text', 'surface'], ['text', 'key'], ['muted', 'page'], ['muted', 'surface'], ['muted', 'subtle'], ['green', 'page'], ['green', 'surface'], ['surface', 'green'], ['green-ink', 'green-light'], ['clear-ink', 'clear-bg'], ['error', 'subtle']];
      return pairs.map(([foreground, background]) => ({ foreground, background, ratio: (Math.max(luminance(foreground), luminance(background)) + .05) / (Math.min(luminance(foreground), luminance(background)) + .05) })).filter(pair => pair.ratio < 4.5);
    })()`);
    assert.deepEqual(failures, [], 'Normal text contrast must be at least 4.5:1');
  }

  await navigate(url);
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1050, deviceScaleFactor: 1, mobile: false });
  const standardNames = ['All clear', 'Insert parenthesis', 'Percent', 'Divide', '7', '8', '9', 'Multiply', '4', '5', '6', 'Subtract', '1', '2', '3', 'Add', 'Change sign', '0', 'Decimal point', 'Equals'];
  async function assertKeyNames(selector, expected) {
    const { root: documentNode } = await send('DOM.getDocument');
    const { nodeIds } = await send('DOM.querySelectorAll', { nodeId: documentNode.nodeId, selector });
    const names = [];
    for (const nodeId of nodeIds) {
      const { nodes } = await send('Accessibility.getPartialAXTree', { nodeId, fetchRelatives: false });
      assert.equal(nodes[0].role.value, 'button');
      names.push(nodes[0].name.value);
    }
    assert.deepEqual(names, expected);
  }
  await check('every standard keypad button exposes its individual accessible name', async () => {
    assert.equal(await result(), '0');
    await assertKeyNames('.standard-grid .key', standardNames);
    assert.deepEqual(await evaluate(`Array.from(document.querySelectorAll('button,input,select')).filter(el => !el.closest('[hidden]') && !el.getAttribute('aria-label') && !el.textContent.trim() && !document.querySelector('label[for="' + el.id + '"]')).map(el => el.id)`), []);
    const tree = await send('Accessibility.getFullAXTree');
    assert.ok(tree.nodes.some(node => node.role?.value === 'textbox' && node.name?.value === 'Your calculation'));
    assert.ok(tree.nodes.some(node => node.role?.value === 'button' && node.name?.value === 'Equals'));
    const resultNode = tree.nodes.find(node => node.role?.value === 'textbox' && node.name?.value === 'Result');
    assert.equal(resultNode.value.value, '0');
    assert.equal(tree.nodes.filter(node => node.role?.value === 'status').length, 1, 'Only the dedicated announcer should be a live status region');
    assert.equal(tree.nodes.find(node => node.role?.value === 'RootWebArea')?.name?.value, 'QuickCalc — Accessible calculator');
  });
  await check('Tab and Shift Tab reach every key with the correct focused accessibility name', async () => {
    await focus('result');
    for (const expected of standardNames) {
      await key('Tab');
      assert.equal(await evaluate(`document.activeElement.getAttribute('aria-label')`), expected);
      const tree = await send('Accessibility.getFullAXTree');
      const focused = tree.nodes.find(node => node.role?.value === 'button' && node.properties?.some(property => property.name === 'focused' && property.value.value === true));
      assert.ok(focused, 'A keypad button must have accessibility focus');
      assert.equal(focused.name.value, expected);
    }
    for (const expected of standardNames.slice(0, -1).reverse()) {
      await key('Tab', 'Tab', 8);
      assert.equal(await evaluate(`document.activeElement.getAttribute('aria-label')`), expected);
    }
  });
  await check('all ten digits work with Enter, Space and screen reader style button activation', async () => {
    for (const digit of '0123456789') {
      for (const activation of ['Enter', ' ', 'click']) {
        await evaluate(`document.querySelector('[data-action="clear"]').click(); document.querySelector('[data-insert="${digit}"]').focus()`);
        if (activation === 'click') await evaluate(`document.activeElement.click()`);
        else await key(activation);
        assert.equal(await evaluate(`document.getElementById('expression').value`), digit);
        assert.equal(await evaluate(`document.activeElement.getAttribute('aria-label')`), digit);
        await delay(60);
        assert.equal(await evaluate(`document.getElementById('announcer').textContent`), `${digit} entered.`);
        assert.equal(await result(), '0');
      }
    }
    await evaluate(`document.getElementById('expression').value = '2+3'; document.querySelector('[data-action="calculate"]').click(); document.querySelector('[data-insert="7"]').focus()`);
    await delay(60);
    assert.equal(await result(), '5');
    assert.equal(await evaluate(`document.getElementById('announcer').textContent`), '', 'A pending answer must not interrupt the next button name');
    await navigate(url);
  });
  await check('actual typing, Enter, history and result announcement', async () => {
    await expression('80 + 15%'); await key('Enter');
    assert.equal(await result(), '92');
    assert.equal(await evaluate(`document.activeElement.id`), 'expression');
    assert.equal(await evaluate(`document.querySelectorAll('.history-entry').length`), 1);
    await delay(70);
    assert.equal(await evaluate(`document.getElementById('announcer').textContent`), 'Equals 92.');
  });
  await check('typing an operator continues the answer; typing digits starts fresh', async () => {
    await type('*2'); await key('Enter'); assert.equal(await result(), '184');
    await type('7'); await key('Enter'); assert.equal(await result(), '7');
  });
  await check('errors identify the input and preserve previous answers and history', async () => {
    await expression('1/0'); await key('Enter');
    assert.equal(await evaluate(`document.getElementById('expression').getAttribute('aria-invalid')`), 'true');
    assert.match(await evaluate(`document.getElementById('calculation-error').textContent`), /divide by zero/);
    assert.equal(await evaluate(`document.querySelectorAll('.history-entry').length`), 3);
    await key('Escape');
    assert.equal(await evaluate(`document.getElementById('expression').value`), '');
    assert.equal(await evaluate(`document.getElementById('expression').hasAttribute('aria-invalid')`), false);
  });
  await check('history arrows restore the draft; history buttons return focus to input', async () => {
    await type('draft'); await key('ArrowUp');
    assert.equal(await evaluate(`document.getElementById('expression').value`), '7');
    await key('ArrowDown'); assert.equal(await evaluate(`document.getElementById('expression').value`), 'draft');
    await evaluate(`document.querySelectorAll('.history-entry')[2].click()`);
    assert.equal(await evaluate(`document.activeElement.id`), 'expression');
    await key('Enter'); assert.equal(await result(), '92');
  });
  await check('keypad arrow navigation, native Enter activation and Tab exit', async () => {
    await key('Escape');
    await evaluate(`document.querySelector('[data-action="clear"]').focus()`);
    await key('ArrowDown');
    assert.equal(await evaluate(`document.activeElement.textContent`), '7');
    await key('Enter');
    assert.equal(await evaluate(`document.getElementById('expression').value`), '7');
    await key('+'); await key('3');
    await key('='); assert.equal(await result(), '10');
    await evaluate(`document.querySelector('[data-action="calculate"]').focus()`);
    await key('Tab'); assert.equal(await evaluate(`document.activeElement.id`), 'copy-result');
  });
  await check('science expansion, degrees/radians and history retain angle unit', async () => {
    await click('scientific-toggle');
    assert.equal(await evaluate(`document.getElementById('scientific-toggle').getAttribute('aria-expanded')`), 'true');
    await assertKeyNames('.scientific-grid .key', ['Sine', 'Cosine', 'Tangent', 'Pi', 'Square root', 'Squared', 'Raise to a power', 'Base ten logarithm']);
    await expression('sin(30)'); await key('Enter'); assert.equal(await result(), '0.5');
    await evaluate(`document.getElementById('angle-mode').value = 'radians'; document.getElementById('angle-mode').dispatchEvent(new Event('change'))`);
    await expression('sin(pi/2)'); await key('Enter'); assert.equal(await result(), '1');
    await evaluate(`document.querySelectorAll('.history-entry')[1].click()`);
    assert.equal(await evaluate(`document.getElementById('angle-mode').value`), 'degrees');
    await click('scientific-toggle');
    assert.equal(await evaluate(`Array.from(document.querySelectorAll('.standard-grid .key')).every(key => key.tabIndex === 0)`), true);
  });
  await check('shortcuts dialog keeps focus inside, closes with Escape and restores focus', async () => {
    await focus('open-shortcuts'); await key('Enter');
    assert.equal(await evaluate(`document.getElementById('shortcuts-dialog').open`), true);
    assert.equal(await evaluate(`document.activeElement.id`), 'close-shortcuts');
    await key('Tab'); assert.equal(await evaluate(`document.activeElement.id`), 'close-shortcuts');
    await key('Escape');
    assert.equal(await evaluate(`document.activeElement.id`), 'open-shortcuts');
  });
  await check('copy uses selection fallback when clipboard is denied', async () => {
    await evaluate(`Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: () => Promise.reject(new Error('Denied for test')) } })`);
    await click('copy-result');
    await delay(70);
    assert.equal(await evaluate(`document.activeElement.id`), 'result');
    assert.equal(await evaluate(`document.getElementById('result').value.slice(document.getElementById('result').selectionStart, document.getElementById('result').selectionEnd)`), await result());
  });
  await check('clearing history returns focus and preserves ans', async () => {
    await click('clear-history');
    assert.equal(await evaluate(`document.activeElement.id`), 'expression');
    assert.equal(await evaluate(`document.querySelectorAll('.history-entry').length`), 0);
    await expression('ans*2'); await key('Enter'); assert.equal(await result(), '2');
    await click('clear-history'); await key('Escape');
  });

  async function screenshot(name) {
    const { data } = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
    fs.writeFileSync(path.join(artifacts, name), Buffer.from(data, 'base64'));
  }
  await evaluate(`document.activeElement.blur()`);
  await screenshot('desktop.png');
  await check('theme switch updates the accessible name and colors', async () => {
    await checkContrast();
    await click('theme-toggle');
    assert.equal(await evaluate(`document.documentElement.dataset.theme`), 'dark');
    assert.equal(await evaluate(`document.getElementById('theme-toggle').getAttribute('aria-label')`), 'Switch to light theme');
    await checkContrast();
    await screenshot('dark.png');
    await click('theme-toggle');
  });
  await check('320px reflow and long results do not overflow', async () => {
    for (const width of [375, 320]) {
      await send('Emulation.setDeviceMetricsOverride', { width, height: 900, deviceScaleFactor: 1, mobile: false });
      assert.ok(await evaluate(`document.documentElement.scrollWidth <= window.innerWidth`), `Page overflows at ${width}px`);
      const tree = await send('Accessibility.getFullAXTree');
      assert.ok(tree.nodes.some(node => node.role?.value === 'button' && node.name?.value === 'Keyboard shortcuts'));
    }
    await expression('123456789012345^2'); await key('Enter');
    assert.ok(await evaluate(`document.documentElement.scrollWidth <= window.innerWidth`));
    await screenshot('mobile.png');
    await click('scientific-toggle');
    assert.ok(await evaluate(`document.documentElement.scrollWidth <= window.innerWidth`));
    await focus('open-shortcuts'); await key('Enter');
    assert.ok(await evaluate(`document.getElementById('shortcuts-dialog').scrollWidth <= document.getElementById('shortcuts-dialog').clientWidth`));
    await key('Escape');
  });
  await check('forced colors and reduced motion remain usable', async () => {
    await send('Emulation.setEmulatedMedia', { features: [{ name: 'forced-colors', value: 'active' }, { name: 'prefers-reduced-motion', value: 'reduce' }] });
    await evaluate(`document.querySelector('[data-action="clear"]').focus()`);
    assert.equal(await evaluate(`getComputedStyle(document.activeElement).outlineStyle`), 'solid');
    assert.equal(await evaluate(`getComputedStyle(document.activeElement).transitionDuration`), '0s');
    await screenshot('forced-colors.png');
    await send('Emulation.setEmulatedMedia', { features: [] });
  });
  await check('runs directly from the HTML file with no server', async () => {
    await navigate(pathToFileURL(path.join(root, 'index.html')).href);
    await assertKeyNames('.standard-grid .key', standardNames);
    await expression('12*(3-1)'); await key('Enter'); assert.equal(await result(), '24');
  });
  assert.deepEqual(connection.exceptions, [], 'Uncaught browser errors');
  console.log(`${checks} browser checks passed. Screenshots: ${artifacts}`);
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (connection) {
    try { await connection.send('Browser.close'); } catch { /* The socket may close first. */ }
    connection.ws.close();
  }
  if (browser && browser.exitCode === null) browser.kill();
  server.close();
  // Profiles are kept in the ignored artifacts directory for easy diagnosis.
});
