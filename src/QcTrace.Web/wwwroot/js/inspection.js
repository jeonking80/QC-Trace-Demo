// 검사 입력 화면: 측정값으로 판정을 제안하고, 불합격·보류일 때 불량 유형을 필수로,
// 유형이 "기타"일 때 사유를 필수로 표시한다(유형을 고르면 사유는 덧붙이는 설명).
// 화면 편의 기능일 뿐이고, 최종 검사는 서버(usp_ApplyInspection)가 한다.
(function () {
    const form = document.getElementById('inspection-form');
    if (!form) return;
    const inputs = [...form.querySelectorAll('.measured')];
    const reason = form.querySelector('[name="Reason"]');
    const reasonMark = document.getElementById('reason-required');
    const auto = document.getElementById('auto-verdict');
    const defect = form.querySelector('[name="DefectCodeId"]');
    const defectMark = document.getElementById('defect-required');
    let userPicked = false;

    function check(input) {
        const cell = input.closest('tr').querySelector('.item-verdict');
        const raw = input.value.trim();
        if (raw === '') { cell.textContent = ''; return null; }
        const v = Number(raw);
        if (Number.isNaN(v)) { cell.textContent = '숫자 아님'; cell.className = 'item-verdict small text-danger'; return false; }
        const ok = v >= Number(input.dataset.lower) && v <= Number(input.dataset.upper);
        cell.textContent = ok ? '적합' : '규격 벗어남';
        cell.className = 'item-verdict small ' + (ok ? 'text-success' : 'text-danger');
        return ok;
    }

    function selected() { return form.querySelector('.verdict:checked')?.value; }

    function updateReason() {
        const issue = selected() === 'Fail' || selected() === 'Hold';
        const other = defect?.selectedOptions[0]?.dataset.code === 'ETC';
        if (defect) {
            defect.disabled = !issue;
            defect.required = issue;
            defectMark.classList.toggle('d-none', !issue);
        }
        const needs = issue && (!defect || other);
        reasonMark.classList.toggle('d-none', !needs);
        reason.required = needs;
    }

    function refresh() {
        const results = inputs.map(check);
        const entered = results.filter(r => r !== null);
        if (entered.length === inputs.length && inputs.length > 0) {
            const pass = entered.every(r => r === true);
            auto.textContent = '자동 판정: ' + (pass ? '합격' : '불합격 (규격 벗어난 항목 있음)');
            if (!userPicked || selected() !== 'Hold') {
                form.querySelector(`.verdict[value="${pass ? 'Pass' : 'Fail'}"]`).checked = true;
            }
        } else {
            auto.textContent = entered.length ? '모든 항목을 입력하면 자동 판정합니다.' : '';
        }
        updateReason();
    }

    inputs.forEach(i => i.addEventListener('input', refresh));
    form.querySelectorAll('.verdict').forEach(r => r.addEventListener('change', () => { userPicked = true; updateReason(); }));
    defect?.addEventListener('change', updateReason);
    refresh();
})();
