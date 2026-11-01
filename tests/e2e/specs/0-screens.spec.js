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
