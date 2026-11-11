(function () {
  'use strict';

  const $ = id => document.getElementById(id);
  const input = $('expression');
  const result = $('result');
  const keypad = $('keypad');
  const dialog = $('shortcuts-dialog');
  const history = [];
  let answer = 0;
  let hasAnswer = false;
  let justCalculated = false;
  let historyCursor = -1;
  let historyDraft = '';
  let historyDraftAngle = 'degrees';
  let announcementTimer;

  function announce(message) {
    clearTimeout(announcementTimer);
    $('announcer').textContent = '';
    announcementTimer = setTimeout(() => { $('announcer').textContent = message; }, 40);
  }

  function clearError() {
    input.removeAttribute('aria-invalid');
    $('calculation-error').hidden = true;
    $('calculation-error').textContent = '';
    $('display').classList.remove('has-error');
  }

  function edited() {
    clearError();
    justCalculated = false;
    historyCursor = -1;
  }

  function showResult(text) {
    result.value = text;
    result.classList.toggle('long-result', text.length > 15);
  }

  function clear() {
    input.value = '';
    edited();
    showResult('0');
    $('copy-result').disabled = true;
    announce('Calculation cleared.');
  }

  function insert(text) {
    // Digits start a fresh expression after a result; operators continue that answer.
    if (justCalculated && input.selectionStart === input.selectionEnd) {
      if (/^[+−×÷^%!]/.test(text)) input.value = 'ans';
      else input.value = '';
      input.setSelectionRange(input.value.length, input.value.length);
    }
    const start = input.selectionStart;
    const end = input.selectionEnd;
    if (input.value.length - (end - start) + text.length > input.maxLength) {
      announce('The calculation has reached the 2,000 character limit.');
      return;
    }
    input.setRangeText(text, start, end, 'end');
    edited();
    input.scrollLeft = input.scrollWidth;
  }

  function backspace() {
    const start = input.selectionStart;
    const end = input.selectionEnd;
    input.setRangeText('', start === end ? Math.max(0, start - 1) : start, end, 'end');
    edited();
  }

  function parentheses() {
    const start = input.selectionStart;
    const end = input.selectionEnd;
    if (start !== end) {
      const selected = input.value.slice(start, end);
      insert(`(${selected})`);
      return;
    }
    const before = input.value.slice(0, start);
    const open = (before.match(/\(/g) || []).length - (before.match(/\)/g) || []).length;
    const endsOperand = /[\d)π%!]$/.test(before.trim()) || /\b(?:ans|pi|e)$/i.test(before.trim());
    insert(open > 0 && endsOperand ? ')' : '(');
  }

  function changeSign() {
    const start = input.selectionStart;
    const end = input.selectionEnd;
    if (!input.value.trim()) { insert('−'); return; }
    if (start !== end) {
      insert(`−(${input.value.slice(start, end)})`);
      return;
    }
    // Negate the whole editable expression, making compound calculations unambiguous.
    input.value = `−(${input.value})`;
    input.setSelectionRange(input.value.length, input.value.length);
    edited();
    announce('Changed the sign of the calculation.');
  }

  function recall(entry) {
    input.value = entry.expression;
    $('angle-mode').value = entry.angle;
    edited();
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
    announce(`Recalled ${entry.expression}. Angle unit: ${entry.angle}. Press Enter to calculate.`);
  }

  function renderHistory() {
    const list = $('history-list');
    list.replaceChildren();
    history.forEach(entry => {
      const item = document.createElement('li');
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'history-entry';
      button.setAttribute('aria-label', `Reuse ${entry.expression}, equals ${entry.result}, in ${entry.angle}`);
      const expression = document.createElement('span');
      expression.className = 'history-expression';
      expression.textContent = entry.expression;
      const value = document.createElement('span');
      value.className = 'history-answer';
      value.textContent = `= ${entry.result}`;
      button.append(expression, value);
      button.addEventListener('click', () => recall(entry));
      item.append(button);
      list.append(item);
    });
    list.hidden = history.length === 0;
    $('empty-history').hidden = history.length > 0;
    $('clear-history').disabled = history.length === 0;
    $('history-count').hidden = history.length === 0;
    $('history-count').textContent = history.length;
  }

  function calculate() {
    try {
      const angle = $('angle-mode').value;
      const calculation = QuickCalc.calculate(input.value, { angle, answer, hasAnswer });
      clearError();
      answer = calculation.value;
      hasAnswer = true;
      justCalculated = true;
      historyCursor = -1;
      showResult(calculation.result);
      $('copy-result').disabled = false;
      history.unshift({ ...calculation, angle });
      if (history.length > 100) history.pop();
      renderHistory();
      announce(`Equals ${calculation.result.replace(/^-/, 'minus ')}.`);
    } catch (error) {
      if (!(error instanceof QuickCalc.CalculationError)) throw error;
      justCalculated = false;
      input.setAttribute('aria-invalid', 'true');
      $('display').classList.add('has-error');
      $('calculation-error').hidden = false;
      $('calculation-error').textContent = error.message;
      input.setSelectionRange(error.position, Math.min(error.position + 1, input.value.length));
      announce(`Error: ${error.message} At character ${error.position + 1}.`);
    }
  }

  async function copyResult() {
    if ($('copy-result').disabled) return;
    const text = result.value;
    try {
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable');
      await navigator.clipboard.writeText(text);
      announce(`Copied ${text} to the clipboard.`);
    } catch {
      // Browsers may deny clipboard access to local files. Select the result for manual copying.
      result.focus();
      result.select();
      announce('Result selected. Press Control C, or Command C on a Mac, to copy.');
    }
  }

  function browseHistory(direction) {
    if (!history.length) return;
    if (historyCursor === -1) {
      historyDraft = input.value;
      historyDraftAngle = $('angle-mode').value;
    }
    const next = Math.max(-1, Math.min(history.length - 1, historyCursor + direction));
    if (next === historyCursor) return;
    historyCursor = next;
    input.value = next === -1 ? historyDraft : history[next].expression;
    $('angle-mode').value = next === -1 ? historyDraftAngle : history[next].angle;
    clearError();
    justCalculated = false;
    input.setSelectionRange(input.value.length, input.value.length);
    announce(next === -1 ? `Draft restored. ${input.value}` : `History ${next + 1} of ${history.length}: ${input.value}`);
  }

  function openShortcuts() {
    if (dialog.open) return;
    dialog.showModal();
  }

  $('calculator-form').addEventListener('submit', event => { event.preventDefault(); calculate(); });
  $('backspace').addEventListener('click', () => { backspace(); announce(input.value || 'Calculation empty.'); });
  $('copy-result').addEventListener('click', copyResult);
  $('clear-history').addEventListener('click', () => {
    history.length = 0;
    historyCursor = -1;
    renderHistory();
    input.focus();
    announce('History cleared. Your previous answer is still available as ans.');
  });
  $('try-example').addEventListener('click', () => { input.value = '80 + 15%'; edited(); input.focus(); calculate(); });
  input.addEventListener('input', edited);
  input.addEventListener('pointerdown', () => { justCalculated = false; });
  input.addEventListener('beforeinput', event => {
    if (!justCalculated || !event.inputType.startsWith('insert') || !event.data || input.selectionStart !== input.selectionEnd) return;
    input.value = /^[+\-−*/×÷^%!]/.test(event.data) ? 'ans' : '';
    input.setSelectionRange(input.value.length, input.value.length);
  });
  input.addEventListener('keydown', event => {
    if (event.ctrlKey || event.metaKey || event.altKey || event.isComposing) return;
    if (['ArrowLeft', 'ArrowRight', 'Home', 'End', 'Backspace', 'Delete'].includes(event.key)) justCalculated = false;
    if (event.key === '=' || event.key === 'Enter') { event.preventDefault(); calculate(); }
    else if (event.key === 'Escape') { event.preventDefault(); clear(); }
    else if (event.key === 'ArrowUp' || event.key === 'ArrowDown') { event.preventDefault(); browseHistory(event.key === 'ArrowUp' ? 1 : -1); }
  });

  // Keep native Tab navigation available to screen readers, including browse mode.
  function visibleKeys() { return [...keypad.querySelectorAll('.key')].filter(key => !key.closest('[hidden]')); }
  keypad.addEventListener('focusin', () => {
    // A pending result must not interrupt the name of the next button.
    clearTimeout(announcementTimer);
    $('announcer').textContent = '';
  });
  keypad.addEventListener('click', event => {
    const key = event.target.closest('.key');
    if (!key) return;
    if (key.dataset.insert) insert(key.dataset.insert);
    else if (key.dataset.action === 'clear') clear();
    else if (key.dataset.action === 'parentheses') parentheses();
    else if (key.dataset.action === 'sign') changeSign();
    else if (key.dataset.action === 'calculate') calculate();
    if (key.dataset.action !== 'calculate' && key.dataset.action !== 'clear' && key.dataset.action !== 'sign') announce(`${key.getAttribute('aria-label')} entered.`);
  });
  keypad.addEventListener('keydown', event => {
    if (!event.target.matches('.key') || event.altKey || event.ctrlKey || event.metaKey || event.isComposing) return;
    const keys = visibleKeys();
    const index = keys.indexOf(event.target);
    const columns = 4;
    let next;
    if (event.key === 'ArrowRight') next = Math.floor(index / columns) * columns + (index + 1) % columns;
    else if (event.key === 'ArrowLeft') next = Math.floor(index / columns) * columns + (index + columns - 1) % columns;
    else if (event.key === 'ArrowDown') next = (index + columns) % keys.length;
    else if (event.key === 'ArrowUp') next = (index + keys.length - columns) % keys.length;
    else if (event.key === 'Home') next = event.ctrlKey ? 0 : Math.floor(index / columns) * columns;
    else if (event.key === 'End') next = Math.floor(index / columns) * columns + columns - 1;
    if (next !== undefined) { event.preventDefault(); keys[next].focus(); return; }
    if (event.key === 'Escape') { event.preventDefault(); clear(); return; }
    if (event.key === 'Backspace' || event.key === 'Delete') { event.preventDefault(); backspace(); announce(input.value || 'Calculation empty.'); return; }
    if (event.key === '=') { event.preventDefault(); calculate(); return; }
    if (/^[\d.+\-*/^%!()]$/.test(event.key)) { event.preventDefault(); insert(event.key); announce(input.value); }
  });
  $('scientific-toggle').addEventListener('click', () => {
    const expanded = $('scientific-toggle').getAttribute('aria-expanded') !== 'true';
    $('scientific-toggle').setAttribute('aria-expanded', String(expanded));
    $('scientific-keys').hidden = !expanded;
  });
  $('angle-mode').addEventListener('change', () => { justCalculated = false; announce(`Angle unit: ${$('angle-mode').value}.`); });

  $('open-shortcuts').addEventListener('click', openShortcuts);
  $('close-shortcuts').addEventListener('click', () => dialog.close());
  dialog.addEventListener('keydown', event => {
    if (event.key !== 'Tab') return;
    const controls = [...dialog.querySelectorAll('button, a[href], input, select, textarea, [tabindex="0"]')].filter(control => !control.disabled && !control.closest('[hidden]'));
    const first = controls[0];
    const last = controls[controls.length - 1];
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });
  dialog.addEventListener('click', event => {
    if (event.target !== dialog) return;
    const rect = dialog.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'F1' || (event.key === '?' && !event.ctrlKey && !event.metaKey && !event.altKey && !event.isComposing)) { event.preventDefault(); openShortcuts(); }
    if ((event.ctrlKey || event.metaKey) && event.shiftKey && event.key.toLowerCase() === 'c' && !dialog.open) { event.preventDefault(); copyResult(); }
  });

  const darkPreference = window.matchMedia('(prefers-color-scheme: dark)');
  let explicitTheme = false;
  function applyTheme(dark) {
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    $('theme-toggle').setAttribute('aria-label', `Switch to ${dark ? 'light' : 'dark'} theme`);
    $('theme-toggle').querySelector('use').setAttribute('href', dark ? '#icon-sun' : '#icon-moon');
  }
  applyTheme(darkPreference.matches);
  darkPreference.addEventListener('change', event => { if (!explicitTheme) applyTheme(event.matches); });
  $('theme-toggle').addEventListener('click', () => {
    explicitTheme = true;
    const dark = document.documentElement.dataset.theme !== 'dark';
    applyTheme(dark);
    announce(`${dark ? 'Dark' : 'Light'} theme enabled.`);
  });
})();
