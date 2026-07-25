const { defineConfig, devices } = require("@playwright/test");

const PORT = Number(process.env.WIDGET_TEST_PORT || 5601);

module.exports = defineConfig({
  testDir: "./specs",
  // The scenarios are about host/widget timing, so several tests deliberately
  // wait out multi-second resize latencies.
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [["github"], ["html", { open: "never" }]] : [["list"]],
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{
    name: "chromium",
    use: {
      ...devices["Desktop Chrome"],
      // CI runs `playwright install chromium` and needs nothing here. Dev
      // containers that ship a prebuilt Chromium of a different build (e.g.
      // PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers) can point at it instead of
      // downloading: CHROMIUM_EXECUTABLE=/opt/pw-browsers/chromium-*/chrome-linux/chrome
      launchOptions: { executablePath: process.env.CHROMIUM_EXECUTABLE || undefined },
    },
  }],
  webServer: {
    command: "node harness/server.js",
    url: `http://localhost:${PORT}/`,
    reuseExistingServer: !process.env.CI,
    stdout: "ignore",
    stderr: "pipe",
  },
});
