// 시나리오 5 (2026-11-01 사용자 결정): 불량 유형을 목록에서 고르고(기타면 직접 입력), 폐기는 관리자만
const { test, expect } = require('@playwright/test');
const { loginAs, shot, success, failure, registerAndRequest } = require('./helpers');

test('불량 유형을 골라 불합격 → 검사자는 폐기 못 함 → 관리자가 폐기 → 유형별 수량', async ({ browser }) => {
  const producer = await loginAs(browser, 'producer');
  const lotNo = await registerAndRequest(producer, 12);

  // 검사자: 규격 밖 측정값 → 불합격, 유형 없이 보내면 화면이 막고, "기타"는 사유가 필요
  const inspector = await loginAs(browser, 'inspector1');
  await inspector.goto('/Inspections');
  await inspector.locator('#to-inspect tr', { hasText: lotNo }).locator('a', { hasText: '검사 입력' }).click();
  for (const input of await inspector.locator('input.measured').all()) {
    const upper = Number(await input.getAttribute('data-upper'));
    const lower = Number(await input.getAttribute('data-lower'));
    await input.fill(String(upper + (upper - lower)));
  }
  await inspector.fill('#Qty', '5');
  await expect(inspector.locator('#verdict-Fail')).toBeChecked();
  await inspector.selectOption('#DefectCodeId', { label: '치수 상한 초과' });
  await inspector.fill('#Reason', '외경 측정 2회 모두 상한 초과');
  await shot(inspector, '16-defect-code');
  await inspector.click('#inspection-form button[type="submit"]');
  await expect(success(inspector)).toContainText('검사 5개');

  // 검사자 불합격 처리 화면에는 폐기가 없다
  await inspector.goto('/Inspections');
  await inspector.locator('tr', { hasText: lotNo }).locator('a', { hasText: '불합격 처리' }).click();
  await expect(inspector.locator('main')).toContainText('폐기는 관리자만');
  await expect(inspector.locator('#act-scrap')).toHaveCount(0);

  // 관리자: 불합격 폐기 화면에서 사유를 보고 폐기
  const admin = await loginAs(browser, 'admin');
  await admin.goto('/Admin/Scrap');
  const row = admin.locator('#scrap-lots tr', { hasText: lotNo });
  await expect(row).toContainText('치수 상한 초과 - 외경 측정 2회 모두 상한 초과');
  await row.locator('input[name="qty"]').fill('5');
  await row.locator('input[name="reason"]').fill('재가공 불가, 폐기');
  await shot(admin, '17-admin-scrap');
  await row.locator('button', { hasText: '폐기 확정' }).click();
  await expect(success(admin)).toContainText(`${lotNo} 불합격 5개를 폐기로 확정했습니다`);

  // 불량 유형 화면: 유형별 수량에 반영
  await admin.goto('/Admin/DefectCodes');
  const dim = admin.locator('#defect-codes tr', { hasText: 'DIM-OVER' });
  expect(Number(await dim.locator('td').nth(2).textContent())).toBeGreaterThanOrEqual(5);

  // LOT 상세: 폐기한 사람은 관리자
  await admin.goto(`/Trace/Lot/${lotNo}`);
  await expect(admin.locator('#lot-dispositions')).toContainText('관리자');
  await expect(admin.locator('#lot-dispositions')).toContainText('재가공 불가, 폐기');
});
