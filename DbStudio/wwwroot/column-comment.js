// 悬停预览完全在浏览器完成，避免鼠标移动产生 Blazor 网络请求。
(() => {
  let active = null;
  let hideTimer;
  const hide = () => {
    clearTimeout(hideTimer);
    if (active?.matches(':popover-open')) active.hidePopover();
    active = null;
  };
  const show = cell => {
    const preview = cell.querySelector('.comment-tooltip');
    if (!preview) { hide(); return; }
    clearTimeout(hideTimer);
    if (preview === active) return;
    hide();
    if (!preview.showPopover) { cell.title = preview.textContent; return; }
    active = preview;
    preview.showPopover();
    const anchor = cell.getBoundingClientRect();
    const popup = preview.getBoundingClientRect();
    const left = Math.max(12, Math.min(anchor.right - popup.width, innerWidth - popup.width - 12));
    const below = anchor.bottom + 6;
    const top = below + popup.height <= innerHeight - 12 ? below : Math.max(12, anchor.top - popup.height - 6);
    preview.style.left = `${left}px`;
    preview.style.top = `${top}px`;
  };
  document.addEventListener('pointerover', event => {
    const cell = event.target instanceof Element ? event.target.closest('.column-comment') : null;
    if (cell) show(cell);
  });
  document.addEventListener('pointerout', event => {
    const cell = event.target instanceof Element ? event.target.closest('.column-comment') : null;
    if (cell && !cell.contains(event.relatedTarget)) hideTimer = setTimeout(hide, 160);
  });
  document.addEventListener('focusin', event => {
    const cell = event.target instanceof Element ? event.target.closest('.column-comment') : null;
    if (cell) show(cell); else hide();
  });
  // 预览里的文字可以选择、复制，点击正文不关闭浮层。
  document.addEventListener('click', event => { if (!active?.contains(event.target)) hide(); });
  document.addEventListener('keydown', event => { if (event.key === 'Escape') hide(); });
  document.addEventListener('scroll', event => { if (!active?.contains(event.target)) hide(); }, true);
  window.addEventListener('resize', hide);
})();
