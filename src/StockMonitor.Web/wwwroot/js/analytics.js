(() => {
    const { fmt, api, hub, utc, palette, colors, gbpTicks, fillAccountSelect } = SM;
    const $ = id => document.getElementById(id);
    const charts = {};

    function chart(id, config) {
        charts[id]?.destroy();
        charts[id] = new Chart($(id), config);
    }
    const gbpTooltip = { callbacks: { label: c => `${c.dataset.label ?? c.label}: ${fmt.gbp(c.parsed.y ?? c.parsed)}` } };

    function query() {
        const p = new URLSearchParams();
        if ($('f-account').value) p.set('account', $('f-account').value);
        if ($('f-from').value) p.set('from', $('f-from').value);
        if ($('f-to').value) p.set('to', $('f-to').value);
        return p.toString();
    }

    async function loadFlows() {
        const d = await api('/api/analytics/cashflow?' + query());
        const m = d.months;
        const labels = m.map(x => x.month);
        chart('flowChart', {
            type: 'bar',
            data: { labels, datasets: [
                { label: 'Deposits', data: m.map(x => x.deposits), backgroundColor: palette[5] },
                { label: 'Withdrawals', data: m.map(x => -x.withdrawals), backgroundColor: palette[6] },
                { label: 'Buys', data: m.map(x => -x.buys), backgroundColor: palette[0] },
                { label: 'Sells', data: m.map(x => x.sells), backgroundColor: palette[2] },
            ] },
            options: { interaction: { mode: 'index', intersect: false }, scales: { x: { stacked: true, grid: { display: false } }, y: { stacked: true, ticks: gbpTicks } }, plugins: { tooltip: gbpTooltip } }
        });
        chart('cumChart', {
            type: 'line',
            data: { labels: d.cumulative.map(x => x.month), datasets: [
                { label: 'Net contributions', data: d.cumulative.map(x => x.netContributions), borderColor: palette[5], pointRadius: 0, tension: .2 },
                { label: 'Net invested (buys − sells)', data: d.cumulative.map(x => x.netInvested), borderColor: palette[0], pointRadius: 0, tension: .2 }
            ] },
            options: { interaction: { mode: 'index', intersect: false }, scales: { x: { grid: { display: false } }, y: { ticks: gbpTicks } }, plugins: { tooltip: gbpTooltip } }
        });
        const totalDiv = m.reduce((s, x) => s + x.dividends, 0);
        $('div-total').textContent = `${fmt.gbp(totalDiv)} in range`;
        chart('divChart', {
            type: 'bar',
            data: { labels, datasets: [
                { label: 'ISA (tax-free)', data: m.map(x => x.dividendsIsa), backgroundColor: palette[1] },
                { label: 'GIA (taxable)', data: m.map(x => x.dividendsInv), backgroundColor: palette[3] }
            ] },
            options: { interaction: { mode: 'index', intersect: false }, scales: { x: { stacked: true, grid: { display: false } }, y: { stacked: true, ticks: gbpTicks } }, plugins: { tooltip: gbpTooltip } }
        });
    }

    async function loadPayers() {
        const acct = $('f-account').value ? `&account=${$('f-account').value}` : '';
        const d = await api('/api/analytics/dividends?take=12' + acct);
        chart('payerChart', {
            type: 'bar',
            data: { labels: d.map(x => x.ticker), datasets: [{ label: 'Dividends', data: d.map(x => x.dividends), backgroundColor: palette[1], borderRadius: 4 }] },
            options: { indexAxis: 'y', plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => fmt.gbp(c.parsed.x), afterLabel: c => `${d[c.dataIndex].payments} payments · ${d[c.dataIndex].securityName}` } } },
                       scales: { x: { ticks: gbpTicks }, y: { grid: { display: false } } } }
        });
        const mix = await api('/api/analytics/mix?' + ($('f-account').value ? `account=${$('f-account').value}` : ''));
        chart('mixChart', {
            type: 'doughnut',
            data: { labels: mix.map(x => x.action), datasets: [{ data: mix.map(x => x.count), backgroundColor: palette.concat(palette), borderColor: '#121a2e', borderWidth: 2 }] },
            options: { cutout: '62%', plugins: { legend: { position: 'bottom', labels: { font: { size: 11 } } } } }
        });
    }

    async function loadGains() {
        const d = await api('/api/analytics/realized');
        const years = [...new Set(d.map(x => x.ukTaxYear))];
        const series = acct => years.map(y => d.filter(x => x.ukTaxYear === y && x.accountType === acct).reduce((s, x) => s + x.gainGbp, 0));
        chart('realChart', {
            type: 'bar',
            data: { labels: years, datasets: [
                { label: 'ISA', data: series('ISA'), backgroundColor: palette[1] },
                { label: 'GIA', data: series('INV'), backgroundColor: palette[3] }
            ] },
            options: { scales: { x: { grid: { display: false } }, y: { ticks: gbpTicks } }, plugins: { tooltip: gbpTooltip } }
        });
        $('realTable').innerHTML = d.map(x => `<tr><td>${x.ukTaxYear}</td><td><span class="acct">${x.accountType}</span></td><td class="num">${x.disposals}</td>
            <td class="num">${fmt.gbp0(x.proceedsGbp)}</td><td class="num ${fmt.cls(x.gainGbp)}">${fmt.signed(x.gainGbp)}</td>
            <td class="num text-secondary">${fmt.signed(x.brokerResultGbp)}</td></tr>`).join('') || '<tr><td colspan="6" class="empty">No disposals</td></tr>';

        const isa = await api('/api/analytics/isa');
        chart('isaChart', {
            type: 'bar',
            data: { labels: isa.map(x => x.ukTaxYear), datasets: [{ label: 'Used %', data: isa.map(x => x.usedPct),
                backgroundColor: isa.map(x => x.usedPct > 100 ? colors.neg : x.usedPct > 80 ? palette[2] : palette[1]), borderRadius: 4 }] },
            options: { plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => `${c.parsed.y.toFixed(1)}% · ${fmt.gbp0(isa[c.dataIndex].subscribedGbp)} deposited` } } },
                       scales: { y: { ticks: { callback: v => v + '%' }, suggestedMax: 120 }, x: { grid: { display: false } } } }
        });
    }

    async function loadFees() {
        const acct = $('f-account').value ? `?account=${$('f-account').value}` : '';
        const d = await api('/api/analytics/fees' + acct);
        const f = d.fees ?? {};
        const rows = [['FX conversion fees', f.fxFees], ['Stamp duty (SDRT)', f.stampDuty], ['Transaction + FINRA fees', f.transactionFees], ['Deposit fees', f.depositFees]];
        $('feesTable').innerHTML = rows.map(([k, v]) => `<tr><td>${k}</td><td class="num">${fmt.gbp(v ?? 0)}</td></tr>`).join('') +
            `<tr><td class="fw-semibold">Total</td><td class="num fw-semibold">${fmt.gbp(rows.reduce((s, r) => s + (r[1] ?? 0), 0))}</td></tr>`;
        $('whtTable').innerHTML = d.withholding.map(w => `<tr><td>${w.year}</td><td>${fmt.esc(w.currency ?? '?')}</td><td class="num">${Number(w.amount).toFixed(2)}</td></tr>`).join('')
            || '<tr><td colspan="3" class="empty">None</td></tr>';
    }

    async function loadPipeline() {
        const d = await api('/api/analytics/pipeline');
        $('pipe-meta').textContent = `${d.rejected} rejected rows · ${d.pendingReplay} queued for replay · ${d.liveTransactions} live transactions`;
        $('batchTable').innerHTML = d.batches.map(b => `<tr><td class="mono">#${b.batchId}</td><td>${b.batchType}</td><td class="small">${fmt.dt(utc(b.startedAt))}</td>
            <td><span class="badge ${b.status === 'SUCCEEDED' ? 'text-bg-success' : b.status === 'RUNNING' ? 'text-bg-primary' : 'text-bg-danger'}">${b.status}</span></td>
            <td class="num">${b.rowsExtracted}</td><td class="num">${b.rowsCleansed}</td><td class="num">${b.rowsRejected}</td><td class="num">${b.rowsLoaded}</td>
            <td class="num">${b.rowsQueued}</td><td class="num">${b.warnings}</td></tr>`).join('') || '<tr><td colspan="10" class="empty">No batches - run the ETL</td></tr>';
        $('ruleTable').innerHTML = d.rules.map(r => `<tr><td class="mono small">${fmt.esc(r.ruleCode)}</td><td class="small">${r.severity}</td><td class="num">${r.count}</td></tr>`).join('');
        $('issueTable').innerHTML = d.recentIssues.map(i => `<tr><td class="mono small">${fmt.esc(i.ruleCode)}</td>
            <td class="small text-secondary">${i.sourceSheet ? fmt.esc(i.sourceSheet) + '!' + (i.sourceRow ?? '') : fmt.esc(i.transactionKey ?? '')}</td>
            <td class="small">${fmt.esc(i.detail)}</td></tr>`).join('');
    }

    const loadAll = () => Promise.all([loadFlows(), loadPayers(), loadGains(), loadFees(), loadPipeline()]).catch(console.error);

    // live: debounce reloads while the replay streams
    let pending = null;
    hub.on('transaction', () => { clearTimeout(pending); pending = setTimeout(loadAll, 2500); });

    $('f-apply').addEventListener('click', loadAll);
    $('f-account').addEventListener('change', loadAll);
    fillAccountSelect($('f-account')).then(loadAll);
})();
