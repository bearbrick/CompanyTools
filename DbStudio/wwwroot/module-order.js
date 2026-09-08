// 指针拖拽同时支持鼠标和触屏；浏览器只处理反馈，最终顺序由 .NET 服务验证保存。
const bindings = new WeakMap();

export function attach(root, receiver) {
    detach(root);
    let gesture = null;
    const clear = () => {
        root.querySelectorAll('.dragging, .drop-target').forEach(row => row.classList.remove('dragging', 'drop-target'));
        if (gesture?.handle.hasPointerCapture(gesture.id)) gesture.handle.releasePointerCapture(gesture.id);
        gesture = null;
    };
    const targetAt = event => {
        const row = root.ownerDocument.elementFromPoint(event.clientX, event.clientY)?.closest('.module-order-row');
        return row && root.contains(row) ? row : null;
    };
    const down = event => {
        const handle = event.target.closest('.module-drag-handle');
        if (event.button !== 0 || !handle || handle.disabled) return;
        const source = handle.closest('.module-order-row');
        gesture = { handle, source, id: event.pointerId, x: event.clientX, y: event.clientY, moved: false };
        handle.setPointerCapture(event.pointerId);
        event.preventDefault();
    };
    const move = event => {
        if (!gesture || gesture.id !== event.pointerId) return;
        if (!gesture.moved && Math.hypot(event.clientX - gesture.x, event.clientY - gesture.y) < 4) return;
        gesture.moved = true;
        gesture.source.classList.add('dragging');
        const bounds = root.getBoundingClientRect();
        if (event.clientY < bounds.top + 24) root.scrollTop -= 12;
        if (event.clientY > bounds.bottom - 24) root.scrollTop += 12;
        const target = targetAt(event);
        root.querySelectorAll('.drop-target').forEach(row => row.classList.remove('drop-target'));
        if (target && target !== gesture.source) target.classList.add('drop-target');
    };
    const up = event => {
        if (!gesture || gesture.id !== event.pointerId) return;
        const sourceName = gesture.source.dataset.module;
        const targetName = gesture.moved ? targetAt(event)?.dataset.module : null;
        clear();
        if (targetName && sourceName !== targetName) {
            receiver.invokeMethodAsync('ReorderModule', sourceName, targetName)
                .catch(() => { /* 会话断开由工作台统一显示重连提示。 */ });
        }
    };
    const escape = event => { if (event.key === 'Escape') clear(); };
    const listeners = { pointerdown: down, pointermove: move, pointerup: up, pointercancel: clear, lostpointercapture: clear };
    Object.entries(listeners).forEach(([event, handler]) => root.addEventListener(event, handler));
    root.ownerDocument.addEventListener('keydown', escape);
    bindings.set(root, () => {
        Object.entries(listeners).forEach(([event, handler]) => root.removeEventListener(event, handler));
        root.ownerDocument.removeEventListener('keydown', escape);
        clear();
    });
}

// 组件销毁时显式移除监听，重新打开面板不会重复提交同一次拖动。
export function detach(root) {
    bindings.get(root)?.();
    bindings.delete(root);
}