// QC Trace E2E. 앱이 떠 있어야 한다: docker compose up -d 후 npm test
// BASE_URL(기본 http://localhost:8080), CAPTURE=1이면 docs/screenshots에 화면 캡처를 남긴다.
const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './specs',
  timeout: 60_000,
  workers: 1,             // 시나리오가 같은 DB를 쓰므로 순서대로
  retries: 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: process.env.BASE_URL || 'http://localhost:8080',
    viewport: { width: 1280, height: 900 },
    locale: 'ko-KR',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});
