// Usage: NODE_PATH=<playwright and axe-core modules> node Tools/Testing/verify-pages.mjs [URL] [output]
import { createRequire } from "node:module";
import { mkdir, writeFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import assert from "node:assert/strict";
const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const axePath = require.resolve("axe-core/axe.min.js");
const base = process.argv[2] || "http://127.0.0.1:4317/";
const output = resolve(process.argv[3] || "/tmp/macexplorer-pages-qa");
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const checks = [];
const errors = [];
function check(name, value) {
  assert.ok(value, name);
  checks.push(name);
}
function observe(page) {
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text());
  });
  page.on("response", (response) => {
    if (response.status() >= 400)
      errors.push(`${response.status()} ${response.url()}`);
  });
}
async function audit(page, name) {
  await page.addScriptTag({ path: axePath });
  const violations = await page.evaluate(() =>
    axe
      .run(
        { exclude: [[".replica-stage"]] },
        {
          runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21aa"] },
        },
      )
      .then((result) => result.violations),
  );
  await writeFile(
    join(output, `${name}-axe.json`),
    JSON.stringify(violations, null, 2),
  );
  check(
    `${name}: WCAG A/AA (page content and controls)`,
    violations.length === 0,
  );
}
try {
  for (const [lang, route] of [
    ["zh-CN", ""],
    ["en", "en/"],
    ["zh-Hant", "zh-Hant/"],
    ["fr", "fr/"],
  ]) {
    for (const [width, height] of [
      [1440, 900],
      [1366, 768],
      [1280, 720],
      [768, 1024],
      [390, 844],
      [320, 740],
    ]) {
      const name = `${lang}-${width}`;
      const page = await browser.newPage({
        viewport: { width, height },
        colorScheme: "dark",
        reducedMotion: "reduce",
      });
      observe(page);
      await page.goto(new URL(route, base).href, { waitUntil: "networkidle" });
      check(
        `${name}: language`,
        (await page.locator("html").getAttribute("lang")) === lang,
      );
      check(
        `${name}: requested light appearance`,
        (await page.locator("html").getAttribute("data-theme")) === "light",
      );
      check(
        `${name}: no page overflow`,
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth,
        ),
      );
      check(`${name}: one H1`, (await page.locator("h1").count()) === 1);
      check(
        `${name}: crawlable identity`,
        (await page.locator("main").innerText()).includes("Mac Explorer"),
      );
      const metadata = await page.evaluate(() => ({
        canonical: document.querySelector("link[rel=canonical]").href,
        alternates: [...document.querySelectorAll("link[hreflang]")].map(
          (e) => e.href,
        ),
        description: document.querySelector("meta[name=description]").content,
        schema: JSON.parse(
          document.querySelector('script[type="application/ld+json"]')
            .textContent,
        ),
      }));
      check(
        `${name}: canonical and five alternates`,
        metadata.canonical.endsWith("/Mac-Explorer/" + route) &&
          metadata.alternates.length === 5,
      );
      check(
        `${name}: description and software schema`,
        metadata.description.length > 40 &&
          metadata.schema["@graph"].some(
            (item) => item["@type"] === "SoftwareApplication",
          ),
      );
      check(
        `${name}: current file list and context menu`,
        (await page
          .locator('[data-panel="browser"] .browser-context-menu')
          .count()) === 1 &&
          (
            await page.locator(".replica-stage").getAttribute("aria-label")
          ).includes("1.0.60"),
      );
      check(
        `${name}: replica keeps its aspect ratio`,
        await page.locator(".replica-stage").evaluate((stage) => {
          const content = stage.firstElementChild.getBoundingClientRect();
          const frame = stage.getBoundingClientRect();
          return (
            Math.abs(content.width - frame.width) < 2 &&
            Math.abs(content.height - frame.height) < 2
          );
        }),
      );
      check(
        `${name}: entire software window visible on entry`,
        await page.locator(".showcase-window").evaluate((element) => {
          const frame = element.getBoundingClientRect();
          return frame.top >= 0 && frame.bottom <= innerHeight;
        }),
      );
      check(
        `${name}: sixteen product capabilities`,
        (await page.locator(".capability").count()) === 16,
      );
      check(
        `${name}: no implementation commentary`,
        !/HTML|复刻|示例|recreated|recréée/.test(
          await page.locator("main").innerText(),
        ),
      );
      await page.locator(".download-picker summary").click();
      check(
        `${name}: chip download choices`,
        (await page.locator(".download-picker a").count()) === 2 &&
          (await page.locator(".download-picker").evaluate((e) => e.open)),
      );
      await page.keyboard.press("Escape");
      check(
        `${name}: download Escape and focus`,
        await page
          .locator(".download-picker")
          .evaluate(
            (e) =>
              !e.open && document.activeElement === e.querySelector("summary"),
          ),
      );
      if (lang === "zh-CN" && width === 1440) {
        await page
          .locator(".replica-stage")
          .screenshot({ path: join(output, "browser-replica.png") });
      }
      await page.locator(".faq summary").first().click();
      check(
        `${name}: FAQ expands`,
        await page
          .locator(".faq details")
          .first()
          .evaluate((e) => e.open),
      );
      await audit(page, name);
      await page.locator(".language-picker summary").click();
      check(
        `${name}: language links`,
        (await page.locator(".language-picker a").count()) === 4,
      );
      await page.keyboard.press("Escape");
      check(
        `${name}: language Escape and focus`,
        await page
          .locator(".language-picker")
          .evaluate(
            (e) =>
              !e.open && document.activeElement === e.querySelector("summary"),
          ),
      );
      await page.locator(".faq summary").first().click();
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.screenshot({
        path: join(output, `${name}-full.png`),
        fullPage: true,
      });
      await page.screenshot({ path: join(output, `${name}.png`) });
      if (lang === "zh-CN" && width === 1440) {
        const broken = await page.evaluate(async () => {
          const failures = [];
          const urls = new Set(
            [...document.querySelectorAll("[href],[src]")]
              .map((e) => e.getAttribute("href") || e.getAttribute("src"))
              .filter(
                (u) => u && !u.startsWith("https:") && !u.startsWith("mailto:"),
              ),
          );
          for (const value of urls) {
            const [path, fragment] = value.split("#");
            if (!path) {
              if (!document.getElementById(fragment)) failures.push(value);
              continue;
            }
            const response = await fetch(path);
            if (!response.ok) {
              failures.push(value);
              continue;
            }
            if (fragment && path.endsWith(".svg")) {
              const svg = new DOMParser().parseFromString(
                await response.text(),
                "image/svg+xml",
              );
              if (!svg.getElementById(fragment)) failures.push(value);
            }
          }
          return failures;
        });
        check(
          "Local links, anchors, assets and Fluent symbols",
          broken.length === 0,
        );
        check(
          "No broken images",
          await page
            .locator("img")
            .evaluateAll((images) =>
              images.every(
                (image) => !image.complete || image.naturalWidth > 0,
              ),
            ),
        );
      }
      await page.close();
    }
  }
  for (const route of ["", "en/", "zh-Hant/", "fr/"]) {
    const page = await browser.newPage({
      javaScriptEnabled: false,
      viewport: { width: 390, height: 844 },
    });
    await page.goto(new URL(route, base).href);
    check(
      `${route || "zh-CN"}: no JS content`,
      (await page.locator("main").innerText()).includes("Mac Explorer"),
    );
    check(
      `${route || "zh-CN"}: no JS download`,
      (await page.locator(".hero-actions a").first().getAttribute("href")) ===
        "https://github.com/3egirlsdream/Mac-Explorer/releases/latest",
    );
    check(
      `${route || "zh-CN"}: no JS replica fit`,
      await page
        .locator('[data-panel="browser"] .replica-stage')
        .evaluate(
          (stage) =>
            Math.abs(
              stage.getBoundingClientRect().width -
                stage.firstElementChild.getBoundingClientRect().width,
            ) < 2,
        ),
    );
    await page.close();
  }
  const request = await browser.newContext();
  const sitemap = await request.request.get(new URL("sitemap.xml", base).href);
  check(
    "Sitemap contains four languages and existing public pages",
    sitemap.ok() && (await sitemap.text()).match(/<loc>/g).length === 7,
  );
  const page = await request.newPage();
  await page.goto(new URL("privacy/", base).href);
  check(
    "Privacy keeps its shared legacy styles",
    (await page.locator('link[href="../styles.css"]').count()) === 1,
  );
  await page.goto(new URL("developers/", base).href);
  check(
    "Developer center remains available",
    (await page.locator("h1").count()) === 1,
  );
  check("No browser or HTTP errors", errors.length === 0);
  await writeFile(
    join(output, "report.json"),
    JSON.stringify({ checks, errors }, null, 2),
  );
  console.log(`${checks.length} checks passed. ${output}`);
} catch (error) {
  await writeFile(
    join(output, "report.json"),
    JSON.stringify({ checks, errors, failure: error.message }, null, 2),
  );
  throw error;
} finally {
  await browser.close();
}
