import fs from "node:fs";
import http from "node:http";
import { chromium } from "playwright-core";

const args = process.argv.slice(2);
function arg(name) {
  const i = args.indexOf(name);
  if (i === -1 || !args[i + 1]) {
    throw new Error(`missing ${name}`);
  }
  return args[i + 1];
}

const baseUrl = arg("--url");
const portFile = arg("--port-file");
const tokenFile = arg("--token-file");
const evidenceDir = process.env.VHC_EVIDENCE || "";

const token = fs.readFileSync(tokenFile, "utf8").trim();

function chromePath() {
  const fromEnv = process.env.CHROME_BIN;
  const candidates = [
    fromEnv,
    "/usr/local/bin/google-chrome",
    "/usr/bin/google-chrome",
    "/opt/google/chrome/chrome",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  ].filter(Boolean);
  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return candidate;
  }
  throw new Error(
    "Google Chrome was not found. Set CHROME_BIN to the chrome executable.",
  );
}

const browser = await chromium.launch({
  executablePath: chromePath(),
  headless: true,
  args: ["--no-sandbox", "--disable-dev-shm-usage"],
});
const context = await browser.newContext({
  viewport: { width: 1280, height: 900 },
});
const page = await context.newPage();

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

async function controlForLabel(label) {
  const pattern = new RegExp(`^\\s*${escapeRegExp(label)}\\b`);
  const labels = page.locator("label").filter({ hasText: pattern });
  const count = await labels.count();
  for (let i = 0; i < count; i += 1) {
    const lab = labels.nth(i);
    const wrapped = lab.locator("input, select, textarea");
    if ((await wrapped.count()) > 0) return wrapped.first();
    const row = lab.locator(
      'xpath=ancestor::div[contains(concat(" ", normalize-space(@class), " "), " form-row ")][1]',
    );
    const inRow = row.locator(
      "input:not([type=radio]):not([type=checkbox]), select, textarea",
    );
    if ((await inRow.count()) > 0) return inRow.first();
  }
  throw new Error(`No control labeled ${label}`);
}

async function gotoPath(path) {
  const target = new URL(path, baseUrl).toString();
  const socket = page
    .waitForEvent("websocket", { timeout: 20000 })
    .catch(() => null);
  await page.goto(target, { waitUntil: "domcontentloaded", timeout: 20000 });
  await socket;
  await page.getByRole("heading", { level: 1 }).first().waitFor({ timeout: 20000 });
  const loading = page.getByText("Loading…", { exact: true });
  if ((await loading.count()) > 0) {
    await loading.first().waitFor({ state: "hidden", timeout: 20000 });
  }
}

async function run(command) {
  switch (command.op) {
    case "goto":
      await gotoPath(command.path);
      return { url: page.url() };
    case "click": {
      const root = command.withinHeading
        ? page.locator(".modal").filter({
            has: page.getByRole("heading", {
              name: command.withinHeading,
              exact: true,
            }),
          })
        : page;
      await root
        .getByRole(command.role, { name: command.name, exact: true })
        .click({ timeout: 15000 });
      return { url: page.url() };
    }
    case "fill": {
      const control = await controlForLabel(command.label);
      await control.fill(command.value, { timeout: 15000 });
      return { value: await control.inputValue() };
    }
    case "select": {
      const control = await controlForLabel(command.label);
      await control.selectOption(command.value, { timeout: 15000 });
      return { value: await control.inputValue() };
    }
    case "wait-text":
      await page.getByText(command.text, { exact: false }).first().waitFor({
        timeout: command.timeout ?? 15000,
      });
      return { found: true };
    case "wait-heading":
      await page
        .getByRole("heading", { name: command.name, exact: true })
        .waitFor({ timeout: command.timeout ?? 15000 });
      return { found: true };
    case "wait-hidden":
      await page.getByText(command.text, { exact: false }).first().waitFor({
        state: "hidden",
        timeout: command.timeout ?? 15000,
      });
      return { hidden: true };
    case "wait-value": {
      const control = await controlForLabel(command.label);
      await control.waitFor({ timeout: 15000 });
      const deadline = Date.now() + (command.timeout ?? 15000);
      let current = "";
      while (Date.now() < deadline) {
        current = await control.inputValue();
        if (current === command.value) return { value: current };
        await page.waitForTimeout(100);
      }
      throw new Error(
        `Label ${command.label} stayed ${JSON.stringify(current)}, wanted ${JSON.stringify(command.value)}`,
      );
    }
    case "assert-text": {
      const match = page.getByText(command.text, { exact: false }).first();
      if ((await match.count()) === 0 || !(await match.isVisible())) {
        throw new Error(`Text not visible: ${command.text}`);
      }
      return { found: true };
    }
    case "assert-absent": {
      const match = page.getByText(command.text, { exact: false });
      const count = await match.count();
      for (let i = 0; i < count; i += 1) {
        if (await match.nth(i).isVisible()) {
          throw new Error(`Text still visible: ${command.text}`);
        }
      }
      return { absent: true };
    }
    case "screenshot":
      fs.mkdirSync(command.path.split("/").slice(0, -1).join("/") || ".", {
        recursive: true,
      });
      await page.screenshot({ path: command.path, fullPage: true });
      return { path: command.path };
    case "snapshot": {
      fs.mkdirSync(command.path.split("/").slice(0, -1).join("/") || ".", {
        recursive: true,
      });
      const aria = await page.locator("body").ariaSnapshot();
      fs.writeFileSync(command.path, aria);
      return { path: command.path };
    }
    default:
      throw new Error(`Unknown op ${command.op}`);
  }
}

async function dumpFailure() {
  if (!evidenceDir || page.isClosed()) return;
  fs.mkdirSync(evidenceDir, { recursive: true });
  await page.screenshot({
    path: `${evidenceDir}/last-error.png`,
    fullPage: true,
  });
}

const server = http.createServer(async (req, res) => {
  if (req.method !== "POST" || req.url !== "/command") {
    res.writeHead(404);
    res.end();
    return;
  }
  if (req.headers.authorization !== `Bearer ${token}`) {
    res.writeHead(401);
    res.end(JSON.stringify({ ok: false, error: "bad token" }));
    return;
  }
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  let command;
  let commandError;
  try {
    command = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    const result = await run(command);
    res.writeHead(200, { "content-type": "application/json" });
    res.end(JSON.stringify({ ok: true, result }));
    return;
  } catch (error) {
    commandError = error instanceof Error ? error.message : String(error);
  }
  try {
    await dumpFailure();
  } catch (shotError) {
    const shot = shotError instanceof Error ? shotError.message : String(shotError);
    res.writeHead(500, { "content-type": "application/json" });
    res.end(JSON.stringify({ ok: false, error: shot, commandError }));
    return;
  }
  res.writeHead(200, { "content-type": "application/json" });
  res.end(JSON.stringify({ ok: false, error: commandError }));
});

await new Promise((resolve) => {
  server.listen(0, "127.0.0.1", resolve);
});
fs.writeFileSync(portFile, String(server.address().port));

async function shutdown() {
  server.close();
  await browser.close();
  process.exit(0);
}
process.on("SIGTERM", () => {
  shutdown();
});
process.on("SIGINT", () => {
  shutdown();
});
