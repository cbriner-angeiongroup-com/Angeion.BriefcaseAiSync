import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './BrowserTests',
  workers: 1,
  use: { baseURL: 'http://127.0.0.1:5068', browserName: 'chromium', viewport: { width: 1440, height: 1100 } },
  webServer: {
    command: 'dotnet run --no-launch-profile --project . --urls http://127.0.0.1:5068',
    url: 'http://127.0.0.1:5068/health/live',
    reuseExistingServer: !process.env.CI,
    env: { ASPNETCORE_ENVIRONMENT: 'Development', BRIEFCASE_Web__DemoMode: 'true' },
    timeout: 120000
  }
});
