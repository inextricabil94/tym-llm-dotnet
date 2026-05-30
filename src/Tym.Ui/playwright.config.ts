import { defineConfig, devices } from "@playwright/test";

const isCi = Boolean(process.env.CI);
const apiBaseUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:5000";
const uiBaseUrl = process.env.E2E_UI_URL ?? "http://127.0.0.1:5173";

export default defineConfig({
  testDir: "./e2e",
  timeout: 45_000,
  expect: {
    timeout: 10_000
  },
  retries: isCi ? 1 : 0,
  workers: 1,
  reporter: [
    ["list"],
    ["html", { open: "never", outputFolder: "playwright-report" }]
  ],
  use: {
    baseURL: uiBaseUrl,
    trace: "on-first-retry",
    screenshot: "only-on-failure",
    video: "retain-on-failure"
  },
  projects: [
    {
      name: "chromium",
      use: {
        ...devices["Desktop Chrome"]
      }
    }
  ],
  webServer: [
    {
      command: "dotnet run --project ../Tym.Api/Tym.Api.csproj --urls http://127.0.0.1:5000",
      url: `${apiBaseUrl}/health`,
      reuseExistingServer: !isCi,
      timeout: 180_000
    },
    {
      command: "npm run dev -- --host 127.0.0.1 --port 5173",
      url: uiBaseUrl,
      reuseExistingServer: !isCi,
      timeout: 180_000,
      env: {
        ...process.env,
        VITE_TYM_API_URL: apiBaseUrl
      }
    }
  ]
});
