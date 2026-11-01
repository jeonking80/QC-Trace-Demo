const path = require('path');
const { expect } = require('@playwright/test');

const PASSWORD = process.env.DEMO_PASSWORD || '12341234!';
const SHOTS = path.resolve(__dirname, '../../../docs/screenshots');

/** 새 브라우저 컨텍스트로 로그인한 페이지. 역할마다 따로 띄워 쿠키가 섞이지 않게 한다. */
async function loginAs(browser, userName, password = PASSWORD) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto('/Account/Login');
  await page.fill('[name="UserName"]', userName);
  await page.fill('[name="Password"]', password);
  await Promise.all([page.waitForURL(u => !u.pathname.startsWith('/Account/Login')), page.click('button[type="submit"]')]);
  return page;
}

/** CAPTURE=1일 때만 docs/screenshots/<name>.png로 저장 */
async function shot(page, name) {
  if (process.env.CAPTURE !== '1') return;
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: true });
}

const success = page => page.locator('.alert-success');
const failure = page => page.locator('.validation-summary-errors');

/** 생산 담당: LOT 등록 → 검사 의뢰. 반환: LOT 번호 */
async function registerAndRequest(page, qty, { capture } = {}) {
  await page.goto('/Lots');
  // 검사 항목이 있는 품목(첫 번째)을 고른다
  await page.selectOption('#Input_ItemId', { index: 1 }).catch(() => page.selectOption('#Input_ItemId', { index: 0 }));
  await page.fill('#Input_ProducedQty', String(qty));
  await page.click('form:has(#Input_ProducedQty) button[type="submit"]');
  await expect(success(page)).toContainText('등록했습니다');
  const lotNo = (await success(page).textContent()).match(/LOT (\S+)을/)[1];
  if (capture) await shot(page, capture);
  const row = page.locator('tr', { hasText: lotNo });
  await row.locator('button', { hasText: '검사 의뢰' }).click();
  await expect(success(page)).toContainText('검사를 의뢰했습니다');
  return lotNo;
}

/** 검사자: 측정값을 규격 가운데 값(합격) 또는 규격 밖(불합격)으로 넣고 판정을 반영 */
async function inspect(page, lotNo, qty, verdict, { reason, outOfSpec = false } = {}) {
  await page.goto('/Inspections');
  await page.locator('tr', { hasText: lotNo }).locator('a', { hasText: '검사 입력' }).click();
  for (const input of await page.locator('input.measured').all()) {
    const lower = Number(await input.getAttribute('data-lower'));
    const upper = Number(await input.getAttribute('data-upper'));
    const value = outOfSpec ? upper + (upper - lower) : (lower + upper) / 2;
    await input.fill(String(value));
  }
  await page.fill('#Qty', String(qty));
  await page.check(`#verdict-${verdict}`);
  // 불합격·보류는 불량 유형 필수: 시나리오의 사유를 그대로 남기려고 "기타"를 고른다
  if (verdict !== 'Pass') await page.selectOption('#DefectCodeId', { label: '기타 (직접 입력)' });
  if (reason) await page.fill('#Reason', reason);
  await page.click('#inspection-form button[type="submit"]');
  await expect(success(page)).toContainText(`검사 ${qty}개`);
  return Number((await success(page).textContent()).match(/남은 미검사 (\d+)개/)[1]);
}

module.exports = { loginAs, shot, success, failure, registerAndRequest, inspect, PASSWORD };
