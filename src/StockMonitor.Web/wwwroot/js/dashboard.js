(() => {
    const { fmt, api, hub, utc, palette, gbpTicks, flash } = SM;
    const $ = id => document.getElementById(id);
    let lastSummary = null;
    let alertCount = 0;

    // ---------- KPIs ----------
    function setVal(id, text, cls, prev, next) {
        const el = $(id);
        el.textContent = text;
        el.classList.remove('pos', 'neg');
        if (cls) el.classList.add(cls);
        if (prev != null && next != null && prev !== next) flash(el.closest('.card-s'), next > prev);
    }

    function renderSummary(s) {
        const p = lastSummary;
        setVal('k-total', fmt.gbp0(s.totalValueGbp), null, p?.totalValueGbp, s.totalValueGbp);
        setVal('k-mv', fmt.gbp0(s.marketValueGbp), null, p?.marketValueGbp, s.marketValueGbp);
        $('k-day').innerHTML = `<span class="${fmt.cls(s.dayChangeGbp)}">${fmt.signed(s.dayChangeGbp)} (${fmt.pct(s.dayChangePct)})</span> today`;
        setVal('k-unr', fmt.signed(s.unrealizedGainGbp), fmt.cls(s.unrealizedGainGbp));
        $('k-unr-pct').innerHTML = `<span class="${fmt.cls(s.unrealizedPct)}">${fmt.pct(s.unrealizedPct)}</span> on ${fmt.gbp0(s.costBasisGbp)} cost`;
        setVal('k-real-ty', fmt.signed(s.realizedThisTaxYearGbp), fmt.cls(s.realizedThisTaxYearGbp));
        $('k-real').textContent = `${fmt.signed(s.realizedGainGbp)} all time`;
        setVal('k-div', fmt.gbp0(s.dividendsLast12mGbp));
        $('k-div-all').textContent = `${fmt.gbp0(s.dividendsGbp)} all time`;
        setVal('k-cash', fmt.gbp(s.cashGbp), s.cashGbp < 0 ? 'neg' : null);
        $('k-pos').textContent = `${s.openPositions} open positions`;
        $('last-tx').textContent = s.lastTransactionAt ? `Latest trade date: ${fmt.dt(utc(s.lastTransactionAt))}` : '';
        lastSummary = s;
    }

    // ---------- value chart ----------
    const chart = new Chart($('valueChart'), {
        type: 'line',
        data: { datasets: [
            { label: 'Total value', data: [], borderColor: palette[0], backgroundColor: palette[0] + '22', fill: true, tension: .25, pointRadius: 0, borderWidth: 2 },
            { label: 'Cost basis', data: [], borderColor: palette[7], borderDash: [4, 4], pointRadius: 0, borderWidth: 1.5 }
        ] },
        options: {
            interaction: { mode: 'index', intersect: false },
            scales: { x: { type: 'time', time: { tooltipFormat: 'HH:mm:ss', displayFormats: { minute: 'HH:mm', second: 'HH:mm:ss' } }, grid: { display: false }, ticks: { maxTicksLimit: 8, maxRotation: 0 } }, y: { ticks: gbpTicks } },
            plugins: { tooltip: { callbacks: { label: c => `${c.dataset.label}: ${fmt.gbp(c.parsed.y)}` } } }
        }
    });
    function addPoint(p) {
        const t = new Date(utc(p.t));
        chart.data.datasets[0].data.push({ x: t, y: p.value });
        chart.data.datasets[1].data.push({ x: t, y: p.cost });
        const cutoff = Date.now() - 2 * 3600 * 1000;
        chart.data.datasets.forEach(d => { while (d.data.length && d.data[0].x < cutoff) d.data.shift(); });
        chart.update('none');
    }

    // ---------- feed ----------
    function feedRow(r, isNew) {
        const name = r.ticker ? `<span class="fw-medium">${fmt.esc(r.ticker)}</span> <span class="text-secondary small">${fmt.esc(r.securityName ?? '')}</span>` : '<span class="text-secondary">—</span>';
        return `<tr class="${isNew ? 'row-new' : ''}">
            <td class="mono small">${fmt.time(utc(r.insertedAt))}</td>
            <td class="small">${fmt.date(utc(r.txTimestamp))}</td>
            <td><span class="acct">${fmt.esc(r.accountType)}</span></td>
            <td><span class="badge-act act-${r.actionCode}">${fmt.esc(r.actionName)}</span>${r.source === 'LIVE' ? ' <i class="bi bi-broadcast text-secondary" title="live"></i>' : ''}</td>
            <td>${name}</td>
            <td class="num">${r.shares != null ? fmt.num(r.shares) : ''}</td>
            <td class="num">${r.priceGbp != null ? Number(r.priceGbp).toFixed(2) : ''}</td>
            <td class="num">${fmt.gbp(r.totalGbp)}</td>
            <td class="num ${fmt.cls(r.cashImpactGbp)}">${fmt.signed(r.cashImpactGbp)}</td></tr>`;
    }
    function prependFeed(r) {
        const body = $('feed');
        if (body.querySelector('.empty')) body.innerHTML = '';
        body.insertAdjacentHTML('afterbegin', feedRow(r, true));
        while (body.rows.length > 100) body.deleteRow(body.rows.length - 1);
    }

    // ---------- alerts ----------
    function alertHtml(a) {
        return `<div class="alert-item sev-${a.severity} ${a.isAcknowledged ? 'ack' : ''}" data-id="${a.alertId}">
            <div class="d-flex justify-content-between gap-2"><strong class="small">${fmt.esc(a.alertType.replaceAll('_', ' '))}</strong>
            ${a.isAcknowledged ? '' : `<button class="btn btn-sm p-0 text-secondary ack-btn" title="Acknowledge"><i class="bi bi-check2"></i></button>`}</div>
            <div class="small">${fmt.esc(a.message)}</div><div class="meta">${fmt.dt(utc(a.createdAt))}</div></div>`;
    }
    function addAlert(a) {
        const box = $('alerts');
        if (box.querySelector('.empty')) box.innerHTML = '';
        box.insertAdjacentHTML('afterbegin', alertHtml(a));
        $('alert-count').textContent = ++alertCount;
    }
    $('alerts').addEventListener('click', async e => {
        const btn = e.target.closest('.ack-btn'); if (!btn) return;
        const item = btn.closest('.alert-item');
        await api(`/api/alerts/${item.dataset.id}/ack`, { method: 'POST' });
        item.classList.add('ack'); btn.remove();
    });

    // ---------- movers ----------
    function renderMovers(prices) {
        const top = [...prices].sort((a, b) => Math.abs(b.changePct) - Math.abs(a.changePct)).slice(0, 10);
        $('movers').innerHTML = top.map(p => `<tr><td class="fw-medium">${fmt.esc(p.ticker)}</td><td class="num">${Number(p.priceGbp).toFixed(2)}</td>
            <td class="num ${fmt.cls(p.changePct)}">${fmt.pct(p.changePct)}</td></tr>`).join('') || '<tr><td colspan="3" class="empty">No prices yet</td></tr>';
    }

    // ---------- replay control ----------
    let paused = false;
    function renderReplay(s) {
        paused = s.paused;
        $('replay-status').textContent = `Replay: ${s.paused ? 'paused' : 'streaming'} · ${s.pending} queued`;
        $('replay-toggle').innerHTML = s.paused ? '<i class="bi bi-play-fill"></i> Resume feed' : '<i class="bi bi-pause-fill"></i> Pause feed';
    }
    $('replay-toggle').addEventListener('click', async () => renderReplay(await api(`/api/replay/${paused ? 'resume' : 'pause'}`, { method: 'POST' })));

    // ---------- wire up ----------
    hub.on('summary', renderSummary);
    hub.on('snapshot', addPoint);
    hub.on('transaction', prependFeed);
    hub.on('alert', addAlert);
    hub.on('prices', renderMovers);
    hub.on('replay', renderReplay);

    (async () => {
        const [summary, recent, alerts, snaps, prices, replay] = await Promise.all([
            api('/api/dashboard/summary'), api('/api/dashboard/recent?take=50'), api('/api/dashboard/alerts?take=30'),
            api('/api/dashboard/snapshots?hours=2'), api('/api/dashboard/prices'), api('/api/replay')]);
        renderSummary(summary);
        $('feed').innerHTML = recent.length ? recent.map(r => feedRow(r, false)).join('') : '<tr><td colspan="9" class="empty">No transactions</td></tr>';
        alerts.reverse().forEach(addAlert);
        snaps.forEach(addPoint);
        renderMovers(prices);
        renderReplay(replay);
    })().catch(err => console.error(err));
})();
