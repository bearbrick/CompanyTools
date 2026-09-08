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
  download(name, content, type) {
    const url = URL.createObjectURL(new Blob([content], {type: type || 'text/plain;charset=utf-8'}));
    const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 2000);
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
  }
};
