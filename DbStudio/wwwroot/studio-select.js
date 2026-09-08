/* 统一选择器：浏览器负责浮层、检索与键盘体验，Blazor 负责业务值。 */
window.studioSelect = {
    active: null,

    open(root, dotnet) {
        const trigger = root.querySelector('.select-trigger');
        const popup = root.querySelector('.select-popup');
        if (trigger.matches(':disabled')) return;
        if (popup.matches(':popover-open')) { popup.hidePopover(); return; }
        this.active?.popup.hidePopover();

        const controller = new AbortController();
        const listen = (element, event, handler, capture = false) =>
            element.addEventListener(event, handler, { signal: controller.signal, capture });
        const input = popup.querySelector('input');
        const options = [...popup.querySelectorAll('[role="option"]')];
        const custom = popup.querySelector('.select-custom');
        let visible = options;
        let highlighted = null;

        const highlight = option => {
            highlighted?.classList.remove('is-highlighted');
            highlighted = option;
            if (!option) { input.removeAttribute('aria-activedescendant'); return; }
            option.classList.add('is-highlighted');
            input.setAttribute('aria-activedescendant', option.id);
            option.scrollIntoView({ block: 'nearest' });
        };
        const close = (restoreFocus = false) => {
            popup.hidePopover();
            if (restoreFocus && trigger.isConnected) trigger.focus({ preventScroll: true });
        };
        const choose = async value => {
            close(true);
            try { await dotnet.invokeMethodAsync('SelectValue', value); }
            catch (error) { console.error('选择器提交失败，请检查连接。', error); }
        };
        const position = () => {
            const rect = trigger.getBoundingClientRect();
            const width = Math.min(Math.max(rect.width, 310), window.innerWidth - 24);
            const below = window.innerHeight - rect.bottom - 12;
            const above = rect.top - 12;
            const upwards = below < 330 && above > below;
            popup.style.width = `${width}px`;
            popup.style.maxHeight = `${Math.max(130, Math.min(410, upwards ? above : below))}px`;
            popup.style.left = `${Math.max(12, Math.min(rect.left, window.innerWidth - width - 12))}px`;
            popup.style.top = `${upwards ? Math.max(12, rect.top - popup.offsetHeight - 6) : rect.bottom + 6}px`;
        };

        input.value = '';
        options.forEach(option => { option.hidden = false; option.classList.remove('is-highlighted'); });
        popup.querySelector('.select-empty').hidden = true;
        popup.querySelector('.select-count').textContent = `${options.length} 项`;
        if (custom) custom.hidden = true;
        popup.showPopover({ source: trigger });
        trigger.setAttribute('aria-expanded', 'true');
        this.active = { popup, close };
        position();
        input.focus({ preventScroll: true });
        highlight(options.find(option => option.getAttribute('aria-selected') === 'true') ?? options[0]);

        listen(input, 'input', () => {
            const query = input.value.trim().toLocaleLowerCase();
            visible = options.filter(option => {
                option.hidden = !option.dataset.search.toLocaleLowerCase().includes(query);
                return !option.hidden;
            });
            popup.querySelector('.select-empty').hidden = visible.length > 0 || !!custom;
            popup.querySelector('.select-count').textContent = `${visible.length} / ${options.length} 项`;
            if (custom) {
                custom.hidden = !query || options.some(option => option.dataset.value === input.value.trim());
                custom.textContent = `使用「${input.value.trim()}」`;
            }
            highlight(visible[0]);
            position();
        });
        listen(popup, 'pointermove', event => {
            const option = event.target.closest('[role="option"]');
            if (option && option !== highlighted) highlight(option);
        });
        listen(popup, 'click', event => {
            const option = event.target.closest('[role="option"]');
            if (option) { event.preventDefault(); choose(option.dataset.value); }
            else if (event.target.closest('.select-custom')) { event.preventDefault(); choose(input.value.trim()); }
        });
        // 浮层优先消费按键，Escape 不应同时关闭外层成员编辑弹窗。
        listen(document, 'keydown', event => {
            if (!popup.matches(':popover-open')) return;
            if (event.key === 'Escape') {
                event.preventDefault(); event.stopImmediatePropagation(); close(true);
            } else if (event.key === 'Tab') {
                close(true); // 恢复到触发器后，让浏览器自然前进到下一个表单控件。
            } else if (['ArrowDown', 'ArrowUp', 'Enter'].includes(event.key)) {
                event.preventDefault(); event.stopImmediatePropagation();
                if (event.key === 'Enter') {
                    if (highlighted) choose(highlighted.dataset.value);
                    else if (custom && input.value.trim()) choose(input.value.trim());
                } else {
                    const step = event.key === 'ArrowDown' ? 1 : -1;
                    const index = visible.indexOf(highlighted);
                    highlight(visible[(index + step + visible.length) % visible.length]);
                }
            } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') close(true);
        }, true);
        listen(popup, 'toggle', event => {
            if (event.newState !== 'closed') return;
            trigger.setAttribute('aria-expanded', 'false');
            input.removeAttribute('aria-activedescendant');
            if (this.active?.popup === popup) this.active = null;
            controller.abort();
        });
        listen(window, 'resize', () => close());
        listen(document, 'scroll', event => {
            if (!popup.contains(event.target)) close();
        }, true);
    }
};

document.addEventListener('keydown', event => {
    if (event.target.matches('.select-trigger') && ['ArrowDown', 'ArrowUp'].includes(event.key)) {
        event.preventDefault(); event.target.click();
    }
});
