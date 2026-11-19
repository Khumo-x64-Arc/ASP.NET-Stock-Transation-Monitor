(() => {
    const { fmt, api, hub, utc, palette, colors, gbpTicks, accounts, flash } = SM;
    const $ = id => document.getElementById(id);
    let account = '';
    let rows = [];
    let sortKey = 'marketValueGbp', sortDir = -1;
    const charts = {};
    const chart = (id, cfg) => { charts[id]?.destroy(); charts[id] = new Chart($(id), cfg); };

    // ---------- account toggle ----------
    accounts().then(list => {
        const g = $('acct-toggle');
        list.forEach(a => g.insertAdjacentHTML('beforeend', `<button class="btn btn-sm btn-ghost" data-account="${a.accountKey}">${fmt.esc(a.accountType)} ${a.accountNumber}</button>`));
        g.addEventListener('click', e => {
            const b = e.target.closest('button'); if (!b) return;
            g.querySelectorAll('button').forEach(x => x.classList.toggle('active', x === b));
            account = b.dataset.account;
            load();
        });
    });

    // ---------- holdings ----------
    function recompute() {
        const total = rows.reduce((s, r) => s + r.marketValueGbp, 0);
        rows.forEach(r => {
            r.unrealizedGainGbp = r.marketValueGbp - r.costBasisGbp;
            r.unrealizedPct = r.costBasisGbp ? r.unrealizedGainGbp / r.costBasisGbp * 100 : 0;
            r.weightPct = total ? r.marketValueGbp / total * 100 : 0;
        });
        const mv = total, cost = rows.reduce((s, r) => s + r.costBasisGbp, 0), day = rows.reduce((s, r) => s + r.dayChangeGbp, 0);
        $('p-mv').textContent = fmt.gbp0(mv);
        $('p-day').innerHTML = `<span class="${fmt.cls(day)}">${fmt.signed(day)}</span> today`;
        $('p-cost').textContent = fmt.gbp0(cost);
        $('p-count').textContent = `${rows.length} holdings`;
        $('p-unr').textContent = fmt.signed(mv - cost);
        $('p-unr').className = 'value ' + fmt.cls(mv - cost);
        $('p-unr-pct').innerHTML = `<span class="${fmt.cls(mv - cost)}">${fmt.pct(cost ? (mv - cost) / cost * 100 : 0)}</span>`;
        $('p-div').textContent = fmt.gbp0(rows.reduce((s, r) => s + r.dividendsGbp, 0));
    }

    function renderTable() {
        const sorted = [...rows].sort((a, b) => {
            const x = a[sortKey], y = b[sortKey];
            return (typeof x === 'string' ? x.localeCompare(y) : (x - y)) * sortDir;
        });
        $('holdings').tBodies[0].innerHTML = sorted.map(r => `
            <tr data-key="${r.securityKey}" style="cursor:pointer">
              <td><span class="fw-semibold">${fmt.esc(r.ticker)}</span> <span class="text-secondary small d-none d-lg-inline">${fmt.esc(r.securityName)}</span></td>
              <td><span class="acct">${fmt.esc(r.accounts)}</span></td>
              <td class="num">${fmt.num(r.quantity)}</td>
              <td class="num">${r.avgCostGbp.toFixed(2)}</td>
              <td class="num" data-f="price">${r.priceGbp.toFixed(2)}</td>
              <td class="num ${fmt.cls(r.dayChangePct)}" data-f="day">${fmt.pct(r.dayChangePct)}</td>
              <td class="num" data-f="mv">${fmt.gbp0(r.marketValueGbp)}</td>
              <td class="num ${fmt.cls(r.unrealizedGainGbp)}" data-f="unr">${fmt.signed(r.unrealizedGainGbp)}</td>
              <td class="num ${fmt.cls(r.unrealizedPct)}" data-f="unrp">${fmt.pct(r.unrealizedPct)}</td>
              <td class="num" data-f="w">${r.weightPct.toFixed(1)}%</td>
              <td class="num">${fmt.gbp0(r.dividendsGbp)}</td>
            </tr>`).join('') || '<tr><td colspan="11" class="empty">No open positions</td></tr>';
    }

    function renderCharts() {
        const byValue = [...rows].sort((a, b) => b.marketValueGbp - a.marketValueGbp);
        const top = byValue.slice(0, 10), rest = byValue.slice(10).reduce((s, r) => s + r.marketValueGbp, 0);
        const labels = top.map(r => r.ticker).concat(rest > 0 ? ['Other'] : []);
        const data = top.map(r => r.marketValueGbp).concat(rest > 0 ? [rest] : []);
        chart('allocChart', {
            type: 'doughnut',
            data: { labels, datasets: [{ data, backgroundColor: palette.concat(palette).slice(0, labels.length), borderColor: '#121a2e', borderWidth: 2 }] },
            options: { cutout: '60%', plugins: { legend: { position: 'right' }, tooltip: { callbacks: { label: c => `${c.label}: ${fmt.gbp0(c.parsed)} (${(c.parsed / data.reduce((a, b) => a + b, 0) * 100).toFixed(1)}%)` } } } }
        });
        const pl = [...rows].sort((a, b) => b.unrealizedGainGbp - a.unrealizedGainGbp);
        $('plBox').style.height = Math.max(220, pl.length * 19 + 40) + 'px';   // one readable bar per holding
        chart('plChart', {
            type: 'bar',
            data: { labels: pl.map(r => r.ticker), datasets: [{ data: pl.map(r => r.unrealizedGainGbp), backgroundColor: pl.map(r => r.unrealizedGainGbp >= 0 ? colors.pos : colors.neg), borderRadius: 3 }] },
            options: { indexAxis: 'y', plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => fmt.signed(c.parsed.x) } } }, scales: { x: { ticks: gbpTicks }, y: { grid: { display: false }, ticks: { autoSkip: false, font: { size: 10.5 } } } } }
        });
    }

    async function load() {
        const q = account ? `?account=${account}` : '';
        const [h, closed, cas] = await Promise.all([api('/api/portfolio/holdings' + q), api('/api/portfolio/closed' + q), api('/api/portfolio/corporate-actions')]);
        rows = h;
        recompute(); renderTable(); renderCharts();

        $('closed').innerHTML = closed.map(c => `<tr><td><span class="fw-semibold">${fmt.esc(c.ticker)}</span> <span class="small text-secondary">${fmt.esc(c.securityName)}</span></td>
            <td><span class="acct">${c.accountType}</span></td><td class="small">${fmt.date(utc(c.firstBuyAt))}</td><td class="small">${fmt.date(utc(c.closedAt))}</td>
            <td class="num ${fmt.cls(c.realizedGainGbp)}">${fmt.signed(c.realizedGainGbp)}</td><td class="num">${fmt.gbp(c.dividendsGbp)}</td>
            <td class="num ${fmt.cls(c.totalReturnGbp)}">${fmt.signed(c.totalReturnGbp)}</td></tr>`).join('') || '<tr><td colspan="7" class="empty">None</td></tr>';

        $('cas').innerHTML = cas.map(c => `<tr><td class="small">${fmt.esc(c.effectiveDate)}</td><td><span class="badge text-bg-secondary">${c.actionType}</span></td>
            <td class="fw-semibold">${fmt.esc(c.ticker)}${c.newTicker ? ' → ' + fmt.esc(c.newTicker) : ''}</td><td class="small text-secondary">${terms(c)}</td></tr>`).join('');
    }

    const terms = c => c.actionType === 'SPLIT' ? (c.ratio >= 1 ? `${c.ratio}-for-1` : `1-for-${Math.round(1 / c.ratio)}`)
        : c.actionType === 'CASH_ACQUISITION' ? 'position closed for cash'
        : `${c.ratio} ${fmt.esc(c.newTicker)} per share · ${(c.costBasisAllocation * 100).toFixed(1)}% of cost`;

    // sorting
    $('holdings').tHead.addEventListener('click', e => {
        const th = e.target.closest('th.sortable'); if (!th) return;
        sortDir = sortKey === th.dataset.k ? -sortDir : -1;
        sortKey = th.dataset.k;
        renderTable();
    });

    // ---------- live price updates (no reload) ----------
    hub.on('prices', prices => {
        if (!rows.length) return;
        const byKey = new Map(prices.map(p => [p.securityKey, p]));
        let changed = false;
        rows.forEach(r => {
            const p = byKey.get(r.securityKey); if (!p) return;
            const old = r.priceGbp;
            r.priceGbp = p.priceGbp;
            r.marketValueGbp = r.quantity * p.priceGbp;
            r.dayChangePct = p.changePct;
            r.dayChangeGbp = r.marketValueGbp - r.marketValueGbp / (1 + p.changePct / 100);
            if (old !== r.priceGbp) changed = true;
            const tr = document.querySelector(`#holdings tr[data-key="${r.securityKey}"]`);
            if (tr && old !== r.priceGbp) {
                const cell = tr.querySelector('[data-f="price"]');
                cell.textContent = r.priceGbp.toFixed(2);
                flash(cell, r.priceGbp > old);
            }
        });
        if (changed) {
            recompute();
            // refresh computed cells in place to keep row order stable
            rows.forEach(r => {
                const tr = document.querySelector(`#holdings tr[data-key="${r.securityKey}"]`); if (!tr) return;
                const set = (f, html, cls) => { const c = tr.querySelector(`[data-f="${f}"]`); c.innerHTML = html; if (cls !== undefined) c.className = 'num ' + cls; };
                set('day', fmt.pct(r.dayChangePct), fmt.cls(r.dayChangePct));
                set('mv', fmt.gbp0(r.marketValueGbp));
                set('unr', fmt.signed(r.unrealizedGainGbp), fmt.cls(r.unrealizedGainGbp));
                set('unrp', fmt.pct(r.unrealizedPct), fmt.cls(r.unrealizedPct));
                set('w', r.weightPct.toFixed(1) + '%');
            });
        }
    });

    // positions change only on trades / dividends - reload then (debounced)
    let t = null;
    hub.on('transaction', tx => {
        if (!['BUY', 'SELL', 'DIVIDEND'].includes(tx.actionCode)) return;
        clearTimeout(t); t = setTimeout(load, 1500);
    });

    // ---------- detail drawer ----------
    const drawer = new bootstrap.Offcanvas($('detail'));
    $('holdings').tBodies[0].addEventListener('click', async e => {
        const tr = e.target.closest('tr[data-key]'); if (!tr) return;
        const d = await api(`/api/portfolio/security/${tr.dataset.key}`);
        const r = rows.find(x => x.securityKey == tr.dataset.key);
        $('d-title').textContent = `${d.security.ticker} · ${d.security.securityName}`;
        $('d-sub').textContent = `${d.security.isin ?? 'ISIN n/a'} · quoted in ${d.security.quoteCurrency ?? '?'} · ${r ? fmt.num(r.quantity) + ' shares, avg ' + fmt.gbp(r.avgCostGbp) : ''}`;
        $('d-cas').innerHTML = d.corporateActions.length ? `<div class="card-s mb-3"><h6>Corporate actions</h6>${d.corporateActions.map(c =>
            `<div class="small mb-1"><span class="badge text-bg-secondary me-1">${c.actionType}</span>${fmt.esc(c.effectiveDate)} — ${fmt.esc(c.notes ?? '')}</div>`).join('')}</div>` : '';
        $('d-tx').innerHTML = d.transactions.map(x => `<tr><td class="small">${fmt.dt(utc(x.txTimestamp))}</td><td><span class="acct">${x.accountType}</span></td>
            <td><span class="badge-act act-${x.actionCode}">${fmt.esc(x.actionName)}</span></td><td class="num">${x.shares != null ? fmt.num(x.shares) : ''}</td>
            <td class="num">${x.priceLocal != null ? Number(x.priceLocal).toFixed(2) + ' ' + (x.priceCurrency ?? '') : ''}</td>
            <td class="num">${fmt.gbp(x.totalGbp)}</td><td class="num ${fmt.cls(x.brokerResultGbp)}">${x.brokerResultGbp != null ? fmt.signed(x.brokerResultGbp) : ''}</td></tr>`).join('');
        chart('tickChart', {
            type: 'line',
            data: { datasets: [{ data: d.ticks.map(p => ({ x: new Date(utc(p.t)), y: p.p })), borderColor: palette[0], pointRadius: 0, borderWidth: 1.5, tension: .2 }] },
            options: { plugins: { legend: { display: false } }, scales: { x: { type: 'time', time: { displayFormats: { minute: 'HH:mm' } }, grid: { display: false }, ticks: { maxTicksLimit: 6, maxRotation: 0 } }, y: { ticks: { callback: v => '£' + Number(v).toFixed(2) } } } }
        });
        drawer.show();
    });

    load().catch(console.error);
})();
