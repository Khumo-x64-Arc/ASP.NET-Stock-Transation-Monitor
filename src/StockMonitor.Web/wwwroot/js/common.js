// Shared helpers: formatting, API access, SignalR connection, ticker tape, chart defaults.
window.SM = (() => {
    const gbp = new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP', maximumFractionDigits: 2 });
    const gbp0 = new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP', maximumFractionDigits: 0 });
    const num = new Intl.NumberFormat('en-GB', { maximumFractionDigits: 4 });

    const fmt = {
        gbp: v => v == null ? '–' : gbp.format(v),
        gbp0: v => v == null ? '–' : gbp0.format(v),
        signed: v => v == null ? '–' : (v > 0 ? '+' : '') + gbp.format(v),
        pct: v => v == null ? '–' : (v > 0 ? '+' : '') + Number(v).toFixed(2) + '%',
        num: v => v == null ? '–' : num.format(v),
        cls: v => v > 0 ? 'pos' : v < 0 ? 'neg' : '',
        dt: v => v ? new Date(v).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '–',
        date: v => v ? new Date(v).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : '–',
        time: v => v ? new Date(v).toLocaleTimeString('en-GB') : '–',
        esc: s => s == null ? '' : String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]))
    };

    async function api(url, opts) {
        const r = await fetch(url, opts);
        if (!r.ok && r.status !== 422) throw new Error(`${r.status} ${url}`);
        return r.status === 204 ? null : r.json();
    }

    // Server timestamps are UTC without a 'Z' (datetime2) - treat them as UTC
    const utc = s => (typeof s === 'string' && !/[zZ]|[+-]\d\d:\d\d$/.test(s)) ? s + 'Z' : s;

    // ---- SignalR ----
    const hub = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/market')
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

    const pill = () => document.getElementById('conn-status');
    function setConn(state, label) {
        const el = pill(); if (!el) return;
        el.className = 'conn-pill conn-' + state;
        el.querySelector('.label').textContent = label;
    }
    hub.onreconnecting(() => setConn('connecting', 'Reconnecting'));
    hub.onreconnected(() => setConn('live', 'Live'));
    hub.onclose(() => setConn('down', 'Offline'));

    async function start() {
        try { await hub.start(); setConn('live', 'Live'); }
        catch { setConn('down', 'Offline'); setTimeout(start, 5000); }
    }

    // ---- ticker tape ----
    function renderTape(prices) {
        const track = document.getElementById('ticker-track');
        if (!track || !prices || !prices.length) return;
        track.innerHTML = prices.map(p =>
            `<span class="ticker-item"><span class="sym">${fmt.esc(p.ticker)}</span><span>${Number(p.priceGbp).toFixed(2)}</span>
             <span class="${fmt.cls(p.changePct)} ms-1">${fmt.pct(p.changePct)}</span></span>`).join('');
    }
    hub.on('prices', renderTape);

    // ---- toasts for alerts (all pages) ----
    hub.on('alert', a => {
        const box = document.getElementById('toasts'); if (!box) return;
        const el = document.createElement('div');
        el.className = 'toast align-items-center border-0';
        el.style.background = 'var(--surface-2)';
        el.innerHTML = `<div class="d-flex"><div class="toast-body"><strong class="me-1">${fmt.esc(a.alertType)}</strong>${fmt.esc(a.message)}</div>
                        <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button></div>`;
        box.appendChild(el);
        new bootstrap.Toast(el, { delay: 6000 }).show();
        el.addEventListener('hidden.bs.toast', () => el.remove());
    });

    // ---- charts ----
    const css = getComputedStyle(document.documentElement);
    const palette = ['--c1', '--c2', '--c3', '--c4', '--c5', '--c6', '--c7', '--c8'].map(v => css.getPropertyValue(v).trim());
    const colors = { pos: css.getPropertyValue('--pos').trim(), neg: css.getPropertyValue('--neg').trim(), muted: css.getPropertyValue('--muted').trim(), border: css.getPropertyValue('--border').trim() };
    if (window.Chart) {
        Chart.defaults.color = colors.muted;
        Chart.defaults.borderColor = 'rgba(36,48,73,.6)';
        Chart.defaults.font.family = 'Inter, system-ui, sans-serif';
        Chart.defaults.font.size = 12;
        Chart.defaults.plugins.legend.labels.boxWidth = 10;
        Chart.defaults.plugins.legend.labels.boxHeight = 10;
        Chart.defaults.plugins.tooltip.backgroundColor = '#0b1020';
        Chart.defaults.plugins.tooltip.borderColor = colors.border;
        Chart.defaults.plugins.tooltip.borderWidth = 1;
        Chart.defaults.maintainAspectRatio = false;
    }
    const gbpTicks = { callback: v => gbp0.format(v) };

    // accounts (cached)
    let accountsP = null;
    const accounts = () => accountsP ??= api('/api/accounts');
    async function fillAccountSelect(sel) {
        const list = await accounts();
        sel.innerHTML = '<option value="">All accounts</option>' +
            list.map(a => `<option value="${a.accountKey}">${fmt.esc(a.accountType)} · ${a.accountNumber}</option>`).join('');
    }

    function flash(el, up) {
        if (!el) return;
        el.classList.remove('flash-up', 'flash-down'); void el.offsetWidth;
        el.classList.add(up ? 'flash-up' : 'flash-down');
    }

    document.addEventListener('DOMContentLoaded', () => {
        start();
        api('/api/dashboard/prices').then(renderTape).catch(() => { });
    });

    return { fmt, api, hub, utc, palette, colors, gbpTicks, accounts, fillAccountSelect, flash };
})();
