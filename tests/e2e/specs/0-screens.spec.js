// 화면 캡처용 (CAPTURE=1): 로그인, 대시보드, 보고서·알림. 캡처가 아니어도 화면이 뜨는지 확인한다.
const { test, expect } = require('@playwright/test');
const { loginAs, shot } = require('./helpers');

test('관리자 첫 화면은 대시보드이고 프레임워크 없이 값을 채운다', async ({ browser, page }) => {
  await page.goto('/Account/Login');
  await shot(page, '01-login');
  const admin = await loginAs(browser, 'admin');
  await expect(admin).toHaveURL(/\/Admin\/Dashboard/);
  await expect(admin.locator('[data-kpi="waiting"]')).not.toHaveText('–');
  expect(await admin.evaluate(() => !!(window.React || window.Vue || window.angular))).toBe(false);
  await shot(admin, '02-dashboard');
  await admin.goto('/Admin/Reports');
  await expect(admin.locator('h1')).toHaveText('보고서·알림');
  await shot(admin, '10-reports');
});

test('생산 담당도 LOT 조회·상세에서 불합격 사유와 다음 할 일을 본다 (집 PC 피드백 B1·B2·B4)', async ({ browser }) => {
  const producer = await loginAs(browser, 'producer');
  await producer.goto('/Trace');
  await expect(producer.locator('h1')).toHaveText('LOT 조회');
  await shot(producer, '11-lot-search');
  // 시드 2번 LOT(LOT-날짜-002): 합격분은 출하됐지만 불합격이 남아 '출하 완료'가 아니다(A2)
  await producer.locator('#trace-lots a.lot-link', { hasText: /-002$/ }).click();
  await expect(producer).toHaveURL(/\/Trace\/Lot\/LOT-\d{8}-002/);
  await expect(producer.locator('h1')).not.toContainText('출하 완료');
  await expect(producer.locator('#next-steps')).toContainText('재검사 또는 폐기');
  await expect(producer.locator('#lot-inspections')).toContainText('불합격');
  await expect(producer.locator('#lot-timeline')).toContainText('검사 의뢰');
  await shot(producer, '12-lot-detail');
});
