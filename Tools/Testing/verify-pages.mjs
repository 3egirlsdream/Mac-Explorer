// Usage: NODE_PATH=<directory containing playwright and axe-core> node Tools/Testing/verify-pages.mjs [base URL] [output directory]
import { createRequire } from "node:module";
import { mkdir, writeFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import assert from "node:assert/strict";
const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const axePath = require.resolve("axe-core/axe.min.js");
const url = process.argv[2] || "http://127.0.0.1:4317/";
const output = resolve(process.argv[3] || "/tmp/macexplorer-pages-qa");
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const checks = [];
const errors = [];
function check(name, result) {
  assert.ok(result, name);
  checks.push(name);
}
function observe(page) {
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => {
    if (["error", "warning"].includes(message.type()))
      errors.push(message.text());
  });
  page.on("response", (response) => {
    if (response.status() >= 400)
      errors.push(`${response.status()} ${response.url()}`);
  });
}
async function audit(page, name) {
  await page.addScriptTag({ path: axePath });
  const result = await page.evaluate(() =>
    axe.run(document, {
      runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21aa"] },
    }),
  );
  await writeFile(
    join(output, `${name}-axe.json`),
    JSON.stringify(result.violations, null, 2),
  );
  check(`${name}: WCAG audit`, result.violations.length === 0);
}
try {
  for (const [name, width, height] of [
    ["desktop", 1440, 1000],
    ["tablet", 768, 1024],
    ["mobile", 390, 844],
    ["small", 320, 740],
  ]) {
    for (const theme of ["light", "dark"]) {
      const page = await browser.newPage({
        viewport: { width, height },
        colorScheme: theme,
      });
      observe(page);
      await page.goto(url, { waitUntil: "networkidle" });
      const dimensions = await page.evaluate(() => ({
        width: innerWidth,
        scroll: document.documentElement.scrollWidth,
      }));
      check(
        `${name}-${theme}: no horizontal overflow`,
        dimensions.scroll <= dimensions.width,
      );
      check(
        `${name}-${theme}: theme`,
        (await page.locator("html").getAttribute("data-theme")) === theme,
      );
      check(
        `${name}-${theme}: main sections`,
        (await page.locator("main section[id]").count()) === 7,
      );
      await audit(page, `${name}-${theme}`);
      await page.screenshot({
        path: join(output, `${name}-${theme}.png`),
        fullPage: true,
      });
      if (name === "desktop") {
        for (const id of ["organize", "find", "workflow", "extend"])
          await page
            .locator(`#${id}`)
            .screenshot({ path: join(output, `${id}-${theme}.png`) });
      }
      if (name === "mobile") {
        await page
          .getByRole("button", { name: "展开导航", exact: true })
          .click();
        await page.locator('#nav-links a[href="#organize"]').click();
        check(
          `mobile-${theme}: menu closes after navigation`,
          (await page.locator(".menu-toggle").getAttribute("aria-expanded")) ===
            "false",
        );
        await page.locator("#organize [data-resize-collection]").click();
        check(
          `mobile-${theme}: resize alternative`,
          (await page
            .locator("#organize .collection-demo")
            .getAttribute("data-size")) === "large",
        );
        await page
          .locator("#organize .scene-window")
          .screenshot({ path: join(output, `collection-mobile-${theme}.png`) });
      }
      await page.locator('[data-hero-scene="home"]').click();
      check(
        `${name}-${theme}: home scene fits`,
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      );
      check(
        `${name}-${theme}: home title`,
        (await page.locator(".app-tab.active").textContent()).includes(
          "收藏首页",
        ),
      );
      await page.locator('[data-search-mode="text"]').click();
      await page.locator('[data-workflow="sftp"]').click();
      check(
        `${name}-${theme}: alternative scenes fit`,
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      );
      if (name === "small" || name === "desktop")
        await audit(page, `${name}-${theme}-alternatives`);
      await page.close();
    }
  }
  const page = await browser.newPage({
    viewport: { width: 1440, height: 1000 },
  });
  observe(page);
  await page.goto(url, { waitUntil: "networkidle" });
  const structure = await page.evaluate(() => {
    const ids = [...document.querySelectorAll("[id]")].map((el) => el.id);
    const broken = [...document.querySelectorAll('a[href^="#"]')]
      .map((a) => a.getAttribute("href"))
      .filter(
        (href) => href !== "#" && !document.getElementById(href.slice(1)),
      );
    return {
      duplicates: ids.filter((id, index) => ids.indexOf(id) !== index),
      broken,
    };
  });
  check("unique element IDs", !structure.duplicates.length);
  check("all section links resolve", !structure.broken.length);
  const localLinks = await page
    .locator("a[href]")
    .evaluateAll((links) => [
      ...new Set(
        links
          .map((a) => a.href)
          .filter(
            (href) => href.startsWith(location.origin) && !href.includes("#"),
          ),
      ),
    ]);
  for (const link of localLinks)
    check(`local page available: ${link}`, (await page.request.get(link)).ok());
  const missingIcons = await page.evaluate(async () => {
    const sprite = new DOMParser().parseFromString(
      await (await fetch("Assets/icons/fluent.svg")).text(),
      "image/svg+xml",
    );
    return [...document.querySelectorAll("use")]
      .map((use) => use.getAttribute("href").split("#")[1])
      .filter((id) => !sprite.getElementById(id));
  });
  check("all Fluent symbols exist", !missingIcons.length);

  await page.locator('button[data-view="list"]').first().click();
  check(
    "list view",
    (await page.locator(".app-window").getAttribute("data-view")) === "list",
  );
  await page.locator('button[data-view="split"]').first().click();
  check("split view", await page.locator(".second-pane").isVisible());
  await page.locator("#demo-filter").selectOption("image");
  check(
    "file type filter",
    (await page.locator(".file-item:visible").count()) === 1,
  );
  await page.locator("#demo-search").fill("不存在");
  check(
    "file filter empty state",
    await page.locator(".demo-empty").isVisible(),
  );
  await page.locator("#demo-search").fill("");
  await page.locator("#demo-filter").selectOption("all");
  await page.locator('.file-item[data-file="lake"]').focus();
  await page.keyboard.press("Space");
  check(
    "Space opens preview",
    await page.locator(".preview-dialog").isVisible(),
  );
  await page.keyboard.press("Escape");
  check(
    "preview focus returns to file",
    await page
      .locator('.file-item[data-file="lake"]')
      .evaluate((el) => el === document.activeElement),
  );
  await page.locator('[data-hero-scene="home"]').click();
  check(
    "hero home switch",
    (await page.locator("#hero-home").isVisible()) &&
      !(await page.locator("#hero-browser").isVisible()),
  );
  await page.locator('[data-hero-scene="browser"]').click();
  await page.locator('[data-layout="four"]').click();
  check(
    "four-pane layout",
    (await page.locator(".pane-map").getAttribute("data-layout")) === "four",
  );
  await page.locator('[data-layout="three"]').click();
  check(
    "three-pane layout",
    (await page.locator(".pane-map").getAttribute("data-layout")) === "three",
  );

  const organize = page.locator("#organize");
  await organize.locator('[data-collection-tab="inspiration"]').click();
  check(
    "same photo in another collection",
    (await organize
      .locator('.collection-grid [data-preview="lake"]')
      .count()) === 1,
  );
  await organize.locator('[data-collection-tab="work"]').click();
  await organize.locator(".resize-handle").focus();
  await page.keyboard.press("ArrowRight");
  check(
    "keyboard card resize",
    (await organize.locator(".collection-demo").getAttribute("data-size")) ===
      "large",
  );
  await page.keyboard.press("ArrowLeft");
  const handle = await organize.locator(".resize-handle").boundingBox();
  await page.mouse.move(
    handle.x + handle.width / 2,
    handle.y + handle.height / 2,
  );
  await page.mouse.down();
  await page.mouse.move(handle.x + 85, handle.y, { steps: 8 });
  await page.mouse.up();
  check(
    "pointer card resize",
    (await organize.locator(".collection-demo").getAttribute("data-size")) ===
      "large",
  );
  const expand = organize.locator(
    ".collection-controls [data-expand-collection]",
  );
  await expand.click();
  check(
    "collection opens",
    await page.locator(".collection-dialog").isVisible(),
  );
  await page.locator("#collection-next").click();
  check(
    "collection pagination",
    (await page.locator("#collection-page").textContent()) === "2 / 2",
  );
  await page.locator("#collection-prev").click();
  await audit(page, "collection-dialog");
  await page.keyboard.press("Escape");
  check(
    "collection focus returns",
    await expand.evaluate((el) => el === document.activeElement),
  );

  await page.locator('[data-search-mode="text"]').click();
  check(
    "OCR and PDF share results",
    (await page.locator("#search-results .search-result").count()) === 2,
  );
  await page.locator("#content-search").fill("交付");
  check(
    "PDF keyword result",
    (await page.locator("#search-results").textContent()).includes(
      "设计简报.pdf",
    ),
  );
  await page.locator("#content-search").fill("无此内容");
  check(
    "content search empty state",
    await page.locator(".search-empty").isVisible(),
  );
  await page.locator('[data-search-mode="image"]').click();
  await page.locator('[data-query="人物"]').click();
  check(
    "image category results",
    (await page.locator("#search-results").textContent()).includes(
      "同行者.jpg",
    ),
  );
  await page.locator('[data-search-mode="name"]').click();
  await page.locator('[data-query="笔记"]').click();
  check(
    "filename search",
    (await page.locator("#search-results").textContent()).includes(
      "创作笔记.md",
    ),
  );
  await page
    .locator("#markdown-input")
    .fill("# 测试标题\n\n<script>window.injected=true</script>\n- 示例条目");
  check(
    "Markdown heading preview",
    (await page.locator("#markdown-output h3").textContent()) === "测试标题",
  );
  check(
    "Markdown input treated as text",
    await page.evaluate(
      () =>
        !window.injected && !document.querySelector("#markdown-output script"),
    ),
  );

  await page.locator('[data-delivery-tab="project"]').click();
  check(
    "delivery tabs",
    (await page.locator(".delivery-path span").textContent()) === "山野计划",
  );
  await page.locator("#delivery-toggle").click();
  check("delivery close", !(await page.locator("#delivery-panel").isVisible()));
  await page.locator("#delivery-toggle").click();
  await page.locator("#flow-play").click();
  await page.waitForTimeout(1750);
  await page.locator("#flow-play").click();
  const pausedStep = await page
    .locator(".workflow-scene")
    .getAttribute("data-flow-step");
  await page.waitForTimeout(1750);
  check(
    "flow pauses",
    (await page.locator(".workflow-scene").getAttribute("data-flow-step")) ===
      pausedStep,
  );
  await page.locator("#flow-play").click();
  await page.waitForFunction(
    () => document.querySelector(".workflow-scene").dataset.flowStep === "3",
  );
  check("copy completed", await page.locator(".received-file").isVisible());
  await page.locator('[data-workflow="sftp"]').click();
  await page.locator("#flow-play").click();
  await page.waitForFunction(
    () => document.querySelector(".workflow-scene").dataset.flowStep === "3",
  );
  check(
    "SFTP round trip",
    (await page.locator("#flow-status").textContent()).includes("回传"),
  );
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.locator('[data-workflow="delivery"]').click();
  await page.locator("#flow-play").click();
  await page.waitForTimeout(1750);
  check(
    "reduced motion manual steps",
    (await page.locator(".workflow-scene").getAttribute("data-flow-step")) ===
      "1",
  );
  await page.locator('[data-script-command="check"]').click();
  check(
    "script handoff simulation",
    (await page.locator("#terminal-command").textContent()).includes("--check"),
  );
  check(
    "simulation disclosure",
    (await page.locator("#terminal-status").textContent()).includes("未执行"),
  );
  await page.locator(".site-header .theme-toggle").click();
  await page.reload({ waitUntil: "networkidle" });
  check(
    "theme persists",
    (await page.locator("html").getAttribute("data-theme")) === "dark",
  );
  await page.close();

  const staticPage = await browser.newPage({
    javaScriptEnabled: false,
    viewport: { width: 390, height: 844 },
  });
  observe(staticPage);
  await staticPage.goto(url, { waitUntil: "networkidle" });
  check(
    "no-JS content visible",
    (await staticPage.locator("#organize h2").isVisible()) &&
      (await staticPage.locator("#search-results .search-result").count()) ===
        2,
  );
  check(
    "no-JS download accessible",
    (await staticPage
      .locator('#download a[href$="releases/latest"]')
      .count()) >= 1,
  );
  check(
    "no-JS no overflow",
    await staticPage.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  );
  await staticPage.close();
  check("no console, resource or JavaScript errors", errors.length === 0);
  await writeFile(
    join(output, "report.json"),
    JSON.stringify({ passed: checks.length, checks, errors }, null, 2),
  );
  console.log(
    `${checks.length} checks passed. Screenshots and audits: ${output}`,
  );
} catch (error) {
  await writeFile(
    join(output, "report.json"),
    JSON.stringify(
      { passed: checks.length, checks, errors, failure: error.message },
      null,
      2,
    ),
  );
  throw error;
} finally {
  await browser.close();
}
