// 운영 대시보드: 프레임워크 없이 fetch로 /api/dashboard를 읽어 DOM을 그린다.
// - 60초마다 다시 읽고, 탭이 안 보일 때는 쉰다.
// - 값은 textContent로만 넣는다(innerHTML 미사용 → 서버 값에 태그가 있어도 실행되지 않음).
// - 로그인이 끊기면(401) 로그인 화면으로, DB 장애(503)는 안내만 하고 마지막 값을 유지한다.
(function () {
    'use strict';
    const REFRESH_MS = 60_000;
    const $ = (sel) => document.querySelector(sel);
    const kpi = (name) => document.querySelector(`[data-kpi="${name}"]`);
    const num = new Intl.NumberFormat('ko-KR');
    const pct = (r) => (r === null || r === undefined) ? '–' : (r * 100).toFixed(1) + '%';
    const kst = new Intl.DateTimeFormat('ko-KR', { timeZone: 'Asia/Seoul', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
    let filter = '';
    let last = null;
    let timer = null;

    function el(tag, attrs, ...children) {
        const node = document.createElement(tag);
        for (const [k, v] of Object.entries(attrs || {})) {
            if (k === 'class') node.className = v; else node.setAttribute(k, v);
        }
        for (const c of children) node.append(c instanceof Node ? c : document.createTextNode(String(c ?? '')));
        return node;
    }

    function showError(message) {
        const box = $('#dash-error');
        box.textContent = message;
        box.classList.toggle('d-none', !message);
    }

    async function load() {
        try {
            const res = await fetch('/api/dashboard', { headers: { 'Accept': 'application/json' }, credentials: 'same-origin', cache: 'no-store' });
            if (res.status === 401) { location.href = '/Account/Login?ReturnUrl=' + encodeURIComponent(location.pathname); return; }
            if (res.status === 403) { showError('대시보드는 관리자만 볼 수 있습니다.'); return; }
            if (!res.ok) {
                let detail = '';
                try { detail = (await res.json()).detail || ''; } catch { /* 본문 없음 */ }
                throw new Error(res.status === 503 ? '데이터베이스에 연결할 수 없습니다. ' + detail : `서버 오류 (${res.status})`);
            }
            last = await res.json();
            showError('');
            render(last);
        } catch (e) {
            showError((e && e.message ? e.message : '불러오지 못했습니다.') + (last ? ' 마지막으로 받은 값을 보여주고 있습니다.' : ''));
        }
    }

    function render(d) {
        $('#dash-updated').textContent = '갱신 ' + kst.format(new Date(d.generatedAt)) + ' (60초마다 자동)';
        kpi('waiting').textContent = num.format(d.waiting.lots) + '건';
        kpi('waiting-qty').textContent = '미검사 ' + num.format(d.waiting.qty) + '개';
        kpi('threshold').textContent = d.delayed.thresholdHours;
        kpi('delayed').textContent = num.format(d.delayed.count) + '건';
        kpi('delayed').classList.toggle('text-danger', d.delayed.count > 0);
        kpi('fail-today').textContent = pct(d.failRate.today.failRate);
        kpi('fail-7d').textContent = `오늘 ${num.format(d.failRate.today.failed)}/${num.format(d.failRate.today.inspected)}개 · 7일 ${pct(d.failRate.last7Days.failRate)}`;
        kpi('shipped').textContent = num.format(d.shippedToday.qty) + '개';
        kpi('shipped-count').textContent = '출하 ' + num.format(d.shippedToday.count) + '건';
        kpi('action-total').textContent = num.format(d.actionTotal);

        renderActions(d);

        const delayedRows = $('#dash-delayed-rows');
        delayedRows.replaceChildren(...(d.delayed.lots.length === 0
            ? [el('tr', {}, el('td', { colspan: '4', class: 'text-success' }, '지연된 LOT이 없습니다.'))]
            : d.delayed.lots.map(l => el('tr', { class: 'delayed' },
                el('td', {}, el('a', { href: '/Admin/Ledger?LotNo=' + encodeURIComponent(l.lotNo) }, l.lotNo)),
                el('td', {}, l.itemCode),
                el('td', { class: 'text-end' }, l.hours + '시간'),
                el('td', { class: 'text-end' }, num.format(l.uninspected))))));

        $('#dash-daily-rows').replaceChildren(...d.failRate.daily.slice().reverse().map(r => el('tr', {},
            el('td', {}, r.date),
            el('td', { class: 'text-end' }, num.format(r.inspected)),
            el('td', { class: 'text-end' }, num.format(r.failed)),
            el('td', { class: 'text-end' }, pct(r.failRate)),
            el('td', { class: 'text-end' }, num.format(r.shipped)))));
    }

    function renderActions(d) {
        const items = d.actions.filter(a => !filter || a.severity === filter);
        const list = $('#dash-action-list');
        if (items.length === 0) {
            list.replaceChildren(el('div', { class: 'list-group-item text-success' }, '처리할 조치가 없습니다.'));
        } else {
            list.replaceChildren(...items.map(a => el('a', { class: `list-group-item list-group-item-action action-item sev-${a.severity}`, href: a.href, 'data-kind': a.kind },
                el('div', { class: 'd-flex justify-content-between' },
                    el('strong', {}, a.title + (a.lotNo ? ' · ' + a.lotNo : '')),
                    el('span', { class: 'badge rounded-pill bg-secondary fw-normal' }, a.owner)),
                el('div', { class: 'small text-muted' }, a.detail))));
        }
        $('#dash-action-note').textContent = d.actionTotal > d.actions.length
            ? `위험도 순 상위 ${d.actions.length}건만 보입니다(전체 ${d.actionTotal}건).`
            : '항목을 누르면 처리 화면으로 갑니다. 그 화면 권한이 없으면 해당 LOT 이력 원장으로 갑니다.';
    }

    $('#dash-filters').addEventListener('click', (e) => {
        const btn = e.target.closest('button[data-filter]');
        if (!btn) return;
        filter = btn.dataset.filter;
        for (const b of $('#dash-filters').querySelectorAll('button')) b.classList.toggle('active', b === btn);
        if (last) renderActions(last);
    });
    $('#dash-refresh').addEventListener('click', load);

    function schedule() {
        clearInterval(timer);
        if (!document.hidden) timer = setInterval(load, REFRESH_MS);
    }
    document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); schedule(); });

    load();
    schedule();
})();
