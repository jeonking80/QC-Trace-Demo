// 시나리오 4 (검사 일정 협의, 2026-11-01 사용자 요청):
// 긴급 의뢰(사유) → 검사자 목록 맨 위 + 부분 검사 → 나머지 완료 예정 회신(사유)
// → 생산 담당 거절(사유) → 승인권자가 최종 기한 결정 → LOT 상세에 전 과정이 남는다
const { test, expect } = require('@playwright/test');
const { loginAs, shot, success, failure, inspect } = require('./helpers');

/** datetime-local 값(한국 시각) */
function kstInput(hoursFromNow) {
  const d = new Date(Date.now() + hoursFromNow * 3600_000 + 9 * 3600_000);
  return d.toISOString().slice(0, 16);
}

test('긴급 의뢰 → 검사자 회신 → 생산 담당 거절 → 승인권자 최종 기한', async ({ browser }) => {
  const producer = await loginAs(browser, 'producer');
  await producer.goto('/Lots');
  await producer.selectOption('#Input_ItemId', { index: 1 });
  await producer.fill('#Input_ProducedQty', '40');
  await producer.click('form:has(#Input_ProducedQty) button[type="submit"]');
  const lotNo = (await success(producer).textContent()).match(/LOT (\S+)을/)[1];

  // 긴급인데 사유가 없으면 서버가 거절한다
  let row = producer.locator('tr', { hasText: lotNo });
  await row.locator('summary').click();
  await row.locator('select[name="priority"]').selectOption('Urgent');
  await row.locator('button', { hasText: '검사 의뢰' }).click();
  await expect(failure(producer)).toContainText('긴급 의뢰는 사유가 필요합니다');

  row = producer.locator('tr', { hasText: lotNo });
  await row.locator('summary').click();
  await row.locator('select[name="priority"]').selectOption('Urgent');
  await row.locator('input[name="priorityReason"]').fill('고객사 납기 당김');
  await row.locator('input[name="dueAt"]').fill(kstInput(6));
  await shot(producer, '13-urgent-request');
  await row.locator('button', { hasText: '검사 의뢰' }).click();
  await expect(success(producer)).toContainText('긴급으로 의뢰했습니다');

  // 검사자: 긴급 LOT이 목록 맨 위, 일부만 검사한 뒤 나머지 일정을 회신
  const inspector = await loginAs(browser, 'inspector1');
  await inspector.goto('/Inspections');
  await expect(inspector.locator('#to-inspect tbody tr').first()).toContainText(lotNo);
  await expect(inspector.locator('#to-inspect tbody tr').first()).toContainText('긴급');
  expect(await inspect(inspector, lotNo, 15, 'Pass')).toBe(25);
  await inspector.goto('/Inspections');
  await inspector.locator('#to-inspect tr', { hasText: lotNo }).locator('a', { hasText: '일정 회신' }).click();
  await inspector.fill('#DueAt', kstInput(30));
  await inspector.fill('#Reason', '측정 지그 교정 중, 내일 나머지 검사');
  await shot(inspector, '14-schedule-propose');
  await inspector.click('#propose-form button[type="submit"]');
  await expect(success(inspector)).toContainText('회신했습니다');

  // 생산 담당: 회신 확인 → 거절(사유 필수)
  await producer.goto('/Lots');
  const proposal = producer.locator('#schedule-proposals tr', { hasText: lotNo });
  await expect(proposal).toContainText('측정 지그 교정 중');
  await proposal.locator('button', { hasText: '거절' }).click();
  await expect(failure(producer)).toContainText('거절은 사유가 필요합니다');
  await producer.locator('#schedule-proposals tr', { hasText: lotNo }).locator('input[name="reason"]').fill('오늘 출하 필요');
  await producer.locator('#schedule-proposals tr', { hasText: lotNo }).locator('button', { hasText: '거절' }).click();
  await expect(success(producer)).toContainText('결정권자');

  // 승인권자: 일정 결정 화면에서 최종 기한
  const approver = await loginAs(browser, 'approver');
  await approver.goto('/Schedule');
  const card = approver.locator('.decide-card', { hasText: lotNo });
  await expect(card).toContainText('오늘 출하 필요');
  await card.locator('input[name="dueAt"]').fill(kstInput(10));
  await card.locator('input[name="reason"]').fill('오후 생산분과 묶어 오늘 밤까지');
  await shot(approver, '15-schedule-decide');
  await card.locator('button', { hasText: '최종 기한 확정' }).click();
  await expect(success(approver)).toContainText('최종 기한을');

  // LOT 상세: 협의 기록 4단계가 순서대로
  await approver.goto(`/Trace/Lot/${lotNo}`);
  const history = approver.locator('#lot-schedule tbody tr');
  await expect(history).toHaveCount(4);
  await expect(history.nth(0)).toContainText('의뢰 조건 · 긴급');
  await expect(history.nth(1)).toContainText('검사자 일정 회신');
  await expect(history.nth(2)).toContainText('회신 거절 → 결정권자');
  await expect(history.nth(3)).toContainText('최종 기한 결정');
  await expect(approver.locator('.schedule-badges').first()).toContainText('결정권자 확정');
});
