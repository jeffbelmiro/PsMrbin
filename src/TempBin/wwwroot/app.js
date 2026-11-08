'use strict';
const form = document.querySelector('#paste-form');
const encoder = new TextEncoder();
if (form) {
  const input = document.querySelector('#content');
  const counter = document.querySelector('#byte-count');
  const button = document.querySelector('#save');
  const error = document.querySelector('#form-error');
  const update = () => {
    const size = encoder.encode(input.value).length;
    counter.textContent = `${size.toLocaleString('pt-PT')} / 262 144 bytes`;
    counter.classList.toggle('over-limit', size > 262144);
    button.disabled = size > 262144 || !input.value.trim();
  };
  input.addEventListener('input', update);
  update();
  form.addEventListener('submit', async event => {
    event.preventDefault();
    error.hidden = true;
    button.disabled = true;
    button.textContent = 'A guardar…';
    try {
      const response = await fetch('/api/v1/pastes', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ content: input.value, slug_length: Number(document.querySelector('#slug-length').value) })
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || 'Não foi possível guardar. Tente novamente.');
      // Navigate locally; configured public-origin URLs are displayed by the server.
      window.location.assign(`/${encodeURIComponent(result.slug)}`);
    } catch (problem) {
      error.textContent = problem.message || 'Falha de ligação. Tente novamente.';
      error.hidden = false;
      button.textContent = 'Guardar e gerar link ↗';
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
    button.textContent = 'Copiado ✓';
  } catch { button.textContent = 'Selecione e copie'; }
  setTimeout(() => { button.textContent = previous; }, 2200);
}));
const timer = document.querySelector('#countdown');
if (timer) {
  const deadline = Date.parse(timer.dataset.expires);
  const time = document.querySelector('#expires-at');
  time.textContent = new Date(deadline).toLocaleString('pt-PT', { dateStyle: 'medium', timeStyle: 'long' });
  const tick = () => {
    const remaining = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
    timer.textContent = remaining ? `${Math.floor(remaining / 60).toString().padStart(2, '0')}:${(remaining % 60).toString().padStart(2, '0')} restantes` : 'Expirado';
    if (!remaining) {
      document.querySelector('#paste-content').textContent = 'Este texto expirou e não pode ser recuperado.';
      document.querySelectorAll('[data-copy]').forEach(button => { button.disabled = true; });
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
      linesButton.textContent = 'Limite: 10 000 linhas'; return;
    }
    const fragment = document.createDocumentFragment();
    lines.forEach((line, index) => {
      const span = document.createElement('span');
      span.className = 'code-line'; span.dataset.line = String(index + 1);
      span.textContent = line + (index < lines.length - 1 ? '\n' : '');
      fragment.appendChild(span);
    });
    pre.appendChild(fragment);
  } else pre.textContent = text;
  linesButton.setAttribute('aria-pressed', String(numbered));
});
const deleteButton = document.querySelector('#delete-paste');
if (deleteButton) deleteButton.addEventListener('click', async () => {
  if (!window.confirm('Eliminar definitivamente este texto? Esta ação não pode ser revertida.')) return;
  deleteButton.disabled = true;
  const status = document.querySelector('#delete-status');
  try {
    const response = await fetch(`/api/v1/pastes/${encodeURIComponent(deleteButton.dataset.slug)}`, { method: 'DELETE' });
    if (response.status !== 204) {
      const problem = await response.json(); throw new Error(problem.error || 'Não foi possível eliminar.');
    }
    window.location.reload();
  } catch (problem) {
    status.textContent = problem.message || 'Falha de ligação.'; status.hidden = false; deleteButton.disabled = false;
  }
});
