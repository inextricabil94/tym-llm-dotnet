import { expect, test } from "@playwright/test";

const apiBaseUrl = process.env.E2E_API_URL ?? "http://127.0.0.1:5000";

test.beforeEach(async ({ page, request }) => {
  await request.delete(`${apiBaseUrl}/events`);
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Timeline Operations" })).toBeVisible();
});

test("ingests timeline text and shows events in timeline view", async ({ page, request }) => {
  await page.getByRole("button", { name: "Ingest Text" }).click();
  await expect.poll(async () => {
    const response = await request.get(`${apiBaseUrl}/events`);
    const events = await response.json();
    return Array.isArray(events) ? events.length : 0;
  }).toBeGreaterThan(0);

  await page.getByRole("button", { name: "Timeline" }).click();
  await expect.poll(() => page.locator(".event-row").count()).toBeGreaterThan(0);
});

test("runs query flow and returns evidence", async ({ page, request }) => {
  await page.getByRole("button", { name: "Ingest Text" }).click();
  await expect.poll(async () => {
    const response = await request.get(`${apiBaseUrl}/events`);
    const events = await response.json();
    return Array.isArray(events) ? events.length : 0;
  }).toBeGreaterThan(0);

  await page.getByRole("button", { name: "Ask" }).click();
  await expect(page.locator(".query-result pre")).toBeVisible();
  await expect.poll(() => page.locator(".evidence-list .event-row").count()).toBeGreaterThan(0);
});

test("reset clears timeline memory", async ({ page, request }) => {
  await page.getByRole("button", { name: "Ingest Text" }).click();
  await expect.poll(async () => {
    const response = await request.get(`${apiBaseUrl}/events`);
    const events = await response.json();
    return Array.isArray(events) ? events.length : 0;
  }).toBeGreaterThan(0);

  await page.getByRole("button", { name: "Reset" }).click();
  await expect.poll(async () => {
    const response = await request.get(`${apiBaseUrl}/events`);
    const events = await response.json();
    return Array.isArray(events) ? events.length : -1;
  }).toBe(0);
});
