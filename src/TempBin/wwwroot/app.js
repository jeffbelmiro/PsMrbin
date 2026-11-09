'use strict';
const messages = JSON.parse(document.querySelector('#i18n').dataset.messages);
const locale = document.documentElement.lang || 'en';
const t = (key, value) => messages[key].replace('{0}', value ?? '');
class RequestError extends Error {}
const numbers = new Intl.NumberFormat(locale);
const form = document.querySelector('#paste-form');
const encoder = new TextEncoder();
const confirmAction = (message, destructive = false) => new Promise(resolve => {
  const dialog = document.querySelector('#confirm-dialog');
  if (dialog.open) { resolve(false); return; }
  dialog.returnValue = 'cancel';
  document.querySelector('#confirm-message').textContent = message;
  const action = document.querySelector('#confirm-action');
  action.textContent = t(destructive ? 'deleteAction' : 'continue');
  action.classList.toggle('confirm-danger', destructive);
  dialog.addEventListener('close', () => resolve(dialog.returnValue === 'confirm'), { once: true });
  dialog.showModal();
});
document.querySelector('#language-form').addEventListener('submit', async event => {
  const languageForm = event.currentTarget;
  if (document.querySelector('#language').value === locale) { event.preventDefault(); return; }
  if (form && document.querySelector('#content').value) {
    event.preventDefault();
    if (await confirmAction(t('unsaved'))) languageForm.submit();
  }
});
if (form) {
  const input = document.querySelector('#content');
  const counter = document.querySelector('#byte-count');
  const button = document.querySelector('#save');
  const saveLabel = button.textContent;
  const error = document.querySelector('#form-error');
  let saving = false;
  const update = () => {
    const size = encoder.encode(input.value).length;
    counter.textContent = `${numbers.format(size)} / ${numbers.format(262144)} ${t('bytes')}`;
    counter.classList.toggle('over-limit', size > 262144);
    button.disabled = saving || size > 262144 || !input.value.trim();
  };
  input.addEventListener('input', update);
  update();
  form.addEventListener('submit', async event => {
    event.preventDefault();
    if (saving || button.disabled) return;
    saving = true;
    error.hidden = true;
    button.textContent = t('saving');
    input.readOnly = true;
    update();
    try {
      const response = await fetch(`/api/v1/pastes?lang=${encodeURIComponent(locale)}`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ content: input.value })
      });
      const result = await response.json();
      if (!response.ok) throw new RequestError(result.error || t('saveError'));
      window.location.assign(`/${encodeURIComponent(result.slug)}?lang=${encodeURIComponent(locale)}`);
    } catch (problem) {
      error.textContent = problem instanceof RequestError ? problem.message : t('network');
      error.hidden = false;
      button.textContent = saveLabel;
      saving = false;
      input.readOnly = false;
      update();
    }
  });
}
document.querySelectorAll('[data-copy]').forEach(button => button.addEventListener('click', async () => {
  const text = document.getElementById(button.dataset.copy).textContent;
  const previous = button.textContent;
  try {
    if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(text);
    else {
      const field = document.createElement('textarea');
      field.value = text; field.readOnly = true; field.className = 'clipboard-fallback';
      document.body.appendChild(field); field.select();
      const copied = document.execCommand('copy'); field.remove();
      if (!copied) throw new Error('copy');
    }
    button.textContent = t('copied');
  } catch { button.textContent = t('copyFallback'); }
  setTimeout(() => { button.textContent = previous; }, 2200);
}));
const timer = document.querySelector('#countdown');
if (timer) {
  const deadline = Date.parse(timer.dataset.expires);
  document.querySelector('#expires-at').textContent = new Date(deadline).toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'long' });
  const tick = () => {
    const remaining = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
    const clock = `${Math.floor(remaining / 60).toString().padStart(2, '0')}:${(remaining % 60).toString().padStart(2, '0')}`;
    timer.textContent = remaining ? t('remaining', clock) : t('expired');
    if (!remaining) {
      const pre = document.querySelector('#paste-content');
      pre.classList.remove('numbered'); pre.textContent = t('expiredText');
      document.querySelectorAll('[data-copy], #toggle-lines, #delete-paste').forEach(button => { button.disabled = true; });
      clearInterval(interval);
    }
  };
  const interval = setInterval(tick, 1000); tick();
  document.addEventListener('visibilitychange', tick);
}
const linesButton = document.querySelector('#toggle-lines');
if (linesButton) linesButton.addEventListener('click', () => {
  const pre = document.querySelector('#paste-content');
  const numbered = pre.classList.toggle('numbered');
  const text = pre.textContent;
  pre.replaceChildren();
  if (numbered) {
    const lines = text.split('\n');
    if (lines.length > 10000) {
      pre.classList.remove('numbered'); pre.textContent = text;
      linesButton.textContent = t('lineLimit'); return;
    }
    const fragment = document.createDocumentFragment();
    lines.forEach((line, index) => {
      const span = document.createElement('span');
      span.className = 'code-line'; span.dataset.line = numbers.format(index + 1);
      span.textContent = line + (index < lines.length - 1 ? '\n' : '');
      fragment.appendChild(span);
    });
    pre.appendChild(fragment);
  } else pre.textContent = text;
  linesButton.setAttribute('aria-pressed', String(numbered));
});
const deleteButton = document.querySelector('#delete-paste');
if (deleteButton) deleteButton.addEventListener('click', async () => {
  if (!await confirmAction(t('deleteConfirm'), true)) return;
  deleteButton.disabled = true;
  const previous = deleteButton.textContent;
  deleteButton.textContent = t('deleting');
  const status = document.querySelector('#delete-status');
  try {
    const response = await fetch(`/api/v1/pastes/${encodeURIComponent(deleteButton.dataset.slug)}?lang=${encodeURIComponent(locale)}`, { method: 'DELETE' });
    if (response.status !== 204) {
      const problem = await response.json(); throw new RequestError(problem.error || t('deleteError'));
    }
    window.location.reload();
  } catch (problem) {
    status.textContent = problem instanceof RequestError ? problem.message : t('network'); status.hidden = false;
    deleteButton.disabled = false; deleteButton.textContent = previous;
  }
});
