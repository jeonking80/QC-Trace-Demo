// 시나리오 1 (정상): LOT 등록 → 부분 검사 → 승인 → 승인 초과 출하 거절 → 출하 → 원장·대사 확인
const { test, expect } = require('@playwright/test');
const { loginAs, shot, success, failure, registerAndRequest, inspect } = require('./helpers');

test('정상 흐름: 검사 전 물량은 출하되지 않고, 승인 수량까지만 출하된다', async ({ browser }) => {
  const producer = await loginAs(browser, 'producer');
  const lotNo = await registerAndRequest(producer, 100, { capture: '03-lot-register' });

  const inspector = await loginAs(browser, 'inspector1');
  expect(await inspect(inspector, lotNo, 60, 'Pass')).toBe(40);          // 부분 검사: 미검사 40 남음
  await shot(inspector, '04-inspection-partial');
  expect(await inspect(inspector, lotNo, 35, 'Pass')).toBe(5);
  expect(await inspect(inspector, lotNo, 5, 'Fail', { reason: '외경 규격 초과', outOfSpec: true })).toBe(0);

  // 승인권자: 출하 가능 = 합격 95
  const approver = await loginAs(browser, 'approver');
  await approver.goto('/Approvals');
  const row = approver.locator('tr', { hasText: lotNo });
  await expect(row.locator('td').nth(3)).toHaveText('95');
  await row.locator('button', { hasText: '승인' }).click();
  await expect(success(approver)).toContainText('95개 출하를 승인했습니다');

  // 출하 담당: 승인 수량(95)을 넘는 100은 거절, 95는 출하
  const shipper = await loginAs(browser, 'shipper');
  await shipper.goto('/Shipments');
  let shipRow = shipper.locator('tr', { hasText: lotNo });
  await shipRow.locator('input[name="qty"]').fill('100');
  await shipRow.locator('input[name="destination"]').fill('E2E 고객사');
  await shipRow.locator('button', { hasText: '출하' }).click();
  await expect(failure(shipper)).toBeVisible();
  await shot(shipper, '07-shipment-over-approved');
  shipRow = shipper.locator('tr', { hasText: lotNo });
  await shipRow.locator('input[name="qty"]').fill('95');
  await shipRow.locator('input[name="destination"]').fill('E2E 고객사');
  await shipRow.locator('button', { hasText: '출하' }).click();
  await expect(success(shipper)).toContainText('95개 출하를 기록했습니다');

  // 관리자: 원장에 전 과정이 남고, 대사 불일치 0건
  const admin = await loginAs(browser, 'admin');
  await admin.goto(`/Admin/Ledger?LotNo=${lotNo}`);
  const ledger = admin.locator('main table tbody');
  for (const kind of ['생산', '검사 의뢰', '검사', '출하 승인', '출하']) await expect(ledger).toContainText(kind);
  await shot(admin, '08-ledger');
  await admin.goto('/Admin/Reconcile');
  await expect(admin.locator('#mismatch-count')).toHaveText('0건');
  await shot(admin, '09-reconcile');
});
