// 시나리오 3 (본인 승인 차단, R5): 검사자 겸 승인권자는 자기가 검사한 LOT을 승인하지 못한다
const { test, expect } = require('@playwright/test');
const { loginAs, shot, success, failure, registerAndRequest, inspect } = require('./helpers');

test('본인이 검사한 LOT은 화면에서 막히고, 버튼을 우회해 보내도 서버가 거절한다', async ({ browser }) => {
  // 관리자가 검사자 + 승인권자 겸직 계정을 만든다
  const userName = `e2e-${Date.now().toString(36)}`;  // 실행마다 하나씩 남는다(e2e- 접두어로 구분)
  const password = 'E2e!Pass2026';
  const admin = await loginAs(browser, 'admin');
  await admin.goto('/Admin/Users');
  await admin.fill('#NewUserName', userName);
  await admin.fill('#NewDisplayName', 'E2E 겸직');
  await admin.fill('#NewPassword', password);
  await admin.check('#new-Inspector');
  await admin.check('#new-Approver');
  await admin.click('form:has(#NewUserName) button[type="submit"]');
  await expect(success(admin)).toContainText(`사용자 ${userName}을 만들었습니다`);

  const producer = await loginAs(browser, 'producer');
  const lotNo = await registerAndRequest(producer, 30);

  const dual = await loginAs(browser, userName, password);
  await inspect(dual, lotNo, 30, 'Pass');

  // 화면: 승인·반려 버튼 비활성화 + 안내
  await dual.goto('/Approvals');
  const row = dual.locator('tr', { hasText: lotNo });
  await expect(row.locator('button', { hasText: '승인' })).toBeDisabled();
  await expect(row).toContainText('본인이 검사한 LOT은 승인할 수 없습니다');
  await shot(dual, '06-approval-self-blocked');

  // 우회: disabled를 지우고 그대로 제출 → 저장 프로시저가 R5로 거절
  await row.locator('form:has(button:has-text("승인"))').evaluate(form => {
    form.querySelectorAll('[disabled]').forEach(e => e.removeAttribute('disabled'));
    form.submit();
  });
  await dual.waitForLoadState();
  await expect(failure(dual)).toBeVisible();
  await expect(success(dual)).toHaveCount(0);

  // 다른 승인권자는 승인할 수 있다
  const approver = await loginAs(browser, 'approver');
  await approver.goto('/Approvals');
  await approver.locator('tr', { hasText: lotNo }).locator('button', { hasText: '승인' }).click();
  await expect(success(approver)).toContainText('30개 출하를 승인했습니다');

  // 원장: 승인자는 검사자와 다르다
  await admin.goto(`/Admin/Ledger?LotNo=${lotNo}`);
  const approvalRow = admin.locator('main table tbody tr', { hasText: '출하 승인' });
  await expect(approvalRow).toHaveCount(1);
  await expect(approvalRow).not.toContainText('E2E 겸직');
});
