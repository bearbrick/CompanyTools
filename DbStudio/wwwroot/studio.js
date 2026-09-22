window.studio = {
  init(dotnet) {
    window.studio.ref = dotnet;
    if (window.studio.bound) return;
    window.studio.bound = true;
    document.addEventListener('keydown', e => {
      if (e.key === 'Escape') { window.studio.ref.invokeMethodAsync('ShortcutEscape'); }
      const dialog = [...document.querySelectorAll('[aria-modal="true"]')].at(-1);
      if (e.key === 'Tab' && dialog) {
        const items = [...dialog.querySelectorAll('input,select,textarea,button,a[href]')].filter(x => !x.disabled && x.offsetParent !== null);
        if (items.length && e.shiftKey && document.activeElement === items[0]) { e.preventDefault(); items.at(-1).focus(); }
        else if (items.length && !e.shiftKey && document.activeElement === items.at(-1)) { e.preventDefault(); items[0].focus(); }
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); document.activeElement?.blur(); window.studio.ref.invokeMethodAsync('ShortcutSave'); }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); document.querySelector('[aria-label="字段与引用快速检索"]')?.focus(); }
      if (e.key === '/' && !e.target.matches('input,textarea,select')) { e.preventDefault(); document.querySelector('[aria-label="搜索表名或字段"]')?.focus(); }
      if (e.key === 'Enter' && e.target.closest('.field-grid') && e.target.tagName === 'INPUT' && e.target.type !== 'checkbox') {
        e.preventDefault();
        const cell = e.target.closest('td'); const row = cell.closest('tr');
        row.nextElementSibling?.children[cell.cellIndex]?.querySelector('input,select,button')?.focus();
      }
    });
    window.addEventListener('beforeunload', e => { if (window.studio.dirty) { e.preventDefault(); e.returnValue = ''; } });
  },
  dirty: false,
  syncModal() {
    const dialog = [...document.querySelectorAll('[aria-modal="true"]')].at(-1);
    if (dialog && dialog !== window.studio.dialog) {
      window.studio.returnFocus = document.activeElement;
      const target = dialog.querySelector('input:not([disabled]),textarea:not([disabled]),select:not([disabled])') || dialog.querySelector('button'); target?.focus();
    }
    if (!dialog && window.studio.dialog) window.studio.returnFocus?.focus();
    window.studio.dialog = dialog;
  },
  setDirty(value) { window.studio.dirty = value; },
  focusColumn(id) {
    const row = [...document.querySelectorAll('.field-grid tr[data-column-id]')].find(item => item.dataset.columnId === id);
    const input = row?.querySelector('input[data-column-name]');
    if (!input || input.disabled) return;
    row.scrollIntoView({ block: 'center', inline: 'nearest' });
    input.focus({ preventScroll: true });
    input.select();
  },
  download(name, content, type) {
    const url = URL.createObjectURL(new Blob([content], {type: type || 'text/plain;charset=utf-8'}));
    const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 2000);
  },
  async downloadArchive(url, request) {
    const sessionUrl = new URL('api/session', document.baseURI);
    const sessionResponse = await fetch(sessionUrl, { credentials: 'same-origin', cache: 'no-store' });
    if (!sessionResponse.ok || sessionResponse.redirected) throw new Error('登录已失效，请刷新并重新登录。');
    const session = await sessionResponse.json();
    const response = await fetch(url, {
      method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': session.csrf },
      body: JSON.stringify(request)
    });
    if (!response.ok || !response.headers.get('content-type')?.startsWith('application/pdf')) {
      const problem = await response.json().catch(() => null);
      throw new Error(problem?.error || 'PDF 生成失败，请刷新项目后重试。');
    }
    const blob = await response.blob();
    const encoded = response.headers.get('content-disposition')?.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
    const name = encoded ? decodeURIComponent(encoded) : '数据库结构归档.pdf';
    window.studio.download(name, blob, 'application/pdf');
  },
  async readColumnClipboard(maxCharacters) {
    if (!navigator.clipboard?.readText || !window.isSecureContext) return null;
    let text;
    try { text = await navigator.clipboard.readText(); }
    catch { return null; } // 不允许自动读取时，由用户在输入框 Ctrl+V。
    if (text.length > maxCharacters) throw new Error('字段剪贴板内容过大');
    return text;
  },
  async copy(text) {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text);
      return;
    }
    // 内网 HTTP 地址可能没有 Clipboard API；复制按钮仍可用原生选择区复制。
    const previous = document.activeElement;
    const input = document.createElement('textarea');
    input.value = text;
    input.setAttribute('readonly', '');
    input.style.cssText = 'position:fixed;left:-9999px;top:0';
    document.body.appendChild(input);
    input.select();
    const copied = document.execCommand('copy');
    input.remove();
    previous?.focus();
    if (!copied) throw new Error('复制失败，请手动选择并复制内容。');
  },
  erStates: new WeakMap(),
  erDiagramInit(element, reset) {
    if (!element) return;
    const stage = element.querySelector('.er-stage');
    if (!stage) return;
    let state = window.studio.erStates.get(element);
    if (!state) {
      state = { x: 0, y: 0, scale: 1, stage, dragging: false, moved: false };
      window.studio.erStates.set(element, state);
      const apply = () => { if (state.stage) state.stage.style.transform = `translate(${state.x}px,${state.y}px) scale(${state.scale})`; };
      state.apply = apply;
      element.addEventListener('pointerdown', event => {
        if (event.button !== 0 || event.target.closest('.er-node,button,input,select')) return;
        state.dragging = true; state.moved = false; state.startX = event.clientX; state.startY = event.clientY; state.originX = state.x; state.originY = state.y;
        element.classList.add('panning');
        element.setPointerCapture(event.pointerId);
      });
      element.addEventListener('pointermove', event => {
        if (!state.dragging) return;
        const dx = event.clientX - state.startX; const dy = event.clientY - state.startY;
        state.moved ||= Math.abs(dx) + Math.abs(dy) > 3;
        state.x = state.originX + dx; state.y = state.originY + dy; apply();
      });
      const finish = event => {
        if (!state.dragging) return;
        state.dragging = false; element.classList.remove('panning');
        if (element.hasPointerCapture(event.pointerId)) element.releasePointerCapture(event.pointerId);
      };
      element.addEventListener('pointerup', finish);
      element.addEventListener('pointercancel', finish);
      element.addEventListener('wheel', event => {
        event.preventDefault();
        const bounds = element.getBoundingClientRect();
        const px = event.clientX - bounds.left; const py = event.clientY - bounds.top;
        const previous = state.scale;
        const next = Math.min(1.8, Math.max(0.2, previous * (event.deltaY > 0 ? 0.9 : 1.1)));
        state.x = px - (px - state.x) * next / previous;
        state.y = py - (py - state.y) * next / previous;
        state.scale = next; apply();
      }, { passive: false });
    }
    state.stage = stage;
    if (reset) window.studio.erDiagramFit(element);
    else state.apply();
  },
  erDiagramFit(element) {
    const state = window.studio.erStates.get(element); if (!state) return;
    const width = Number(element.dataset.diagramWidth) || state.stage.offsetWidth;
    const height = Number(element.dataset.diagramHeight) || state.stage.offsetHeight;
    const padding = 34;
    state.scale = Math.min(1, Math.max(0.2, Math.min((element.clientWidth - padding * 2) / width, (element.clientHeight - padding * 2) / height)));
    state.x = (element.clientWidth - width * state.scale) / 2;
    state.y = (element.clientHeight - height * state.scale) / 2;
    state.apply();
  },
  erDiagramZoom(element, delta) {
    const state = window.studio.erStates.get(element); if (!state) return;
    const previous = state.scale;
    const next = Math.min(1.8, Math.max(0.2, previous + delta));
    const px = element.clientWidth / 2; const py = element.clientHeight / 2;
    state.x = px - (px - state.x) * next / previous;
    state.y = py - (py - state.y) * next / previous;
    state.scale = next;
    state.apply();
  }
};
