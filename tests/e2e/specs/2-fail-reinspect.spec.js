// 시나리오 2 (불합격 재검사): 불합격 → 재검사로 되돌림 → 다시 검사해 합격 → 원장에 재검사 기록
const { test, expect } = require('@playwright/test');
const { loginAs, shot, success, registerAndRequest, inspect } = require('./helpers');

test('불합격분을 재검사로 되돌리면 미검사로 돌아가고 다시 검사할 수 있다', async ({ browser }) => {
  const producer = await loginAs(browser, 'producer');
  const lotNo = await registerAndRequest(producer, 20);

  const inspector = await loginAs(browser, 'inspector2');
  expect(await inspect(inspector, lotNo, 20, 'Fail', { reason: '길이 미달', outOfSpec: true })).toBe(0);

  // 불합격 처리: 사유 필수, 재검사 선택
  await inspector.goto('/Inspections');
  await inspector.locator('tr', { hasText: lotNo }).locator('a', { hasText: '불합격 처리' }).click();
  await inspector.check('#act-re');
  await inspector.fill('#Qty', '20');
  await inspector.fill('#Reason', '측정 지그 재교정 후 재검사');
  await shot(inspector, '05-dispose-reinspect');
  await inspector.click('form button[type="submit"]:has-text("처리")');
  await expect(success(inspector)).toContainText('재검사 대기(미검사)로 되돌렸습니다');

  // 다시 검사 목록에 미검사 20으로 나오고, 합격 처리된다
  await inspector.goto('/Inspections');
  await expect(inspector.locator('tr', { hasText: lotNo })).toContainText('20 / 20');
  expect(await inspect(inspector, lotNo, 20, 'Pass')).toBe(0);

  const admin = await loginAs(browser, 'admin');
  await admin.goto(`/Admin/Ledger?LotNo=${lotNo}`);
  await expect(admin.locator('main table tbody')).toContainText('재검사');
  await admin.goto('/Admin/Reconcile');
  await expect(admin.locator('#mismatch-count')).toHaveText('0건');
});
