// Usage: NODE_PATH=<playwright and axe-core modules> node Tools/Testing/verify-pages.mjs [URL] [output]
import { createRequire } from "node:module";
import { mkdir, writeFile } from "node:fs/promises";
import { resolve, join } from "node:path";
import assert from "node:assert/strict";
const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const axePath = require.resolve("axe-core/axe.min.js");
const base = process.argv[2] || "http://127.0.0.1:4317/";
const canonicalBase = "https://macexplorer.top/";
const languageRoutes = [
  ["zh-CN", ""],
  ["en", "en/"],
  ["zh-Hant", "zh-Hant/"],
  ["fr", "fr/"],
];
const guideSlugs = ["finder-comparison", "batch-rename"];
const expectedAlternates = [...languageRoutes, ["x-default", ""]].map(
  ([lang, route]) => [
    lang,
    canonicalBase + route,
  ],
);
const output = resolve(process.argv[3] || "/tmp/macexplorer-pages-qa");
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const checks = [];
const errors = [];
function check(name, value) {
  assert.ok(value, name);
  checks.push(name);
}
async function checkDownloadLinks(page, name) {
  const actions = page.locator(".hero-actions > a");
  check(
    `${name}: App Store first, GitHub Releases second`,
    (await actions.count()) === 2 &&
      (await actions.nth(0).getAttribute("href")) ===
        "https://apps.apple.com/cn/app/id6820338798?mt=12" &&
      (await actions.nth(1).getAttribute("href")) ===
        "https://github.com/3egirlsdream/Mac-Explorer/releases/latest",
  );
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
  for (const [lang, route] of languageRoutes) {
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
        alternates: [...document.querySelectorAll("link[rel=alternate][hreflang]")]
          .map((e) => [e.hreflang, e.href]),
        description: document.querySelector("meta[name=description]").content,
        schema: JSON.parse(
          document.querySelector('script[type="application/ld+json"]')
            .textContent,
        ),
      }));
      check(
        `${name}: canonical and five alternates`,
        metadata.canonical === canonicalBase + route &&
          metadata.alternates.length === expectedAlternates.length &&
          expectedAlternates.every(([language, href]) =>
            metadata.alternates.some(
              ([actualLanguage, actualHref]) =>
                actualLanguage === language && actualHref === href,
            ),
          ),
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
            await page
              .locator('[data-panel="browser"] .replica-stage')
              .getAttribute("aria-label")
          ).includes("Mac Explorer"),
      );
      check(
        `${name}: replica keeps its aspect ratio`,
        await page.locator(".replica-stage").evaluateAll((stages) =>
          stages.every((stage) => {
            const content = stage.firstElementChild.getBoundingClientRect();
            const frame = stage.getBoundingClientRect();
            return (
              Math.abs(content.width - frame.width) < 2 &&
              Math.abs(content.height - frame.height) < 2
            );
          }),
        ),
      );
      check(
        `${name}: entire software window visible on entry`,
        await page.locator(".showcase-stack").evaluate((element) => {
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
      await checkDownloadLinks(page, name);
      if (lang === "zh-CN" && width === 1440) {
        await page
          .locator('[data-panel="browser"] .replica-stage')
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
    await checkDownloadLinks(page, `${route || "zh-CN"}: no JS`);
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
  const request = await browser.newContext({ javaScriptEnabled: false });
  const sitemap = await request.request.get(new URL("sitemap.xml", base).href);
  const publicRoutes = ["privacy/", "developers/", "developers/sdk.html"];
  const guideRoutes = guideSlugs.flatMap((slug) =>
    languageRoutes.map(([lang, prefix]) => [lang, `${prefix}guides/${slug}/`, slug]),
  );
  const sitemapUrls = [...(await sitemap.text()).matchAll(/<loc>([^<]+)<\/loc>/g)]
    .map(([, url]) => url);
  const expectedUrls = [
    ...languageRoutes.map(([, route]) => route),
    ...publicRoutes,
    ...guideRoutes.map(([, route]) => route),
  ]
    .map((route) => canonicalBase + route);
  check(
    "Sitemap contains every homepage, guide and public page",
    sitemap.ok() &&
      sitemapUrls.length === expectedUrls.length &&
      expectedUrls.every((url) => sitemapUrls.includes(url)),
  );
  const robots = await request.request.get(new URL("robots.txt", base).href);
  check(
    "Root robots.txt allows crawling and points to the canonical sitemap",
    robots.ok() &&
      (await robots.text()) === `User-agent: *\nAllow: /\nSitemap: ${canonicalBase}sitemap.xml\n`,
  );
  const page = await request.newPage();
  for (const route of publicRoutes) {
    const response = await page.goto(new URL(route, base).href);
    check(
      `${route} is available at its canonical URL`,
      response.ok() &&
        (await page.locator("link[rel=canonical]").getAttribute("href")) ===
          canonicalBase + route,
    );
  }
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
  for (const [lang, route, slug] of guideRoutes) {
    const guidePage = await browser.newPage({
      viewport: { width: 320, height: 740 },
    });
    observe(guidePage);
    const response = await guidePage.goto(new URL(route, base).href);
    const guide = await guidePage.evaluate(() => {
      const head = document.head;
      const image = document.querySelector(".guide-page figure img");
      return {
        lang: document.documentElement.lang,
        canonical: head.querySelector("link[rel=canonical]").href,
        alternates: Object.fromEntries(
          [...head.querySelectorAll("link[hreflang]")].map((link) => [link.hreflang, link.href]),
        ),
        imageLoaded: image.complete && image.naturalWidth > 0 && image.alt.length > 0,
        schema: JSON.parse(head.querySelector('script[type="application/ld+json"]').textContent),
        headings: document.querySelectorAll("main article h1").length,
        noOverflow: document.documentElement.scrollWidth <= innerWidth,
      };
    });
    check(`${route}: canonical and readable on mobile`, response.ok() &&
      guide.lang === lang && guide.canonical === canonicalBase + route &&
      guide.headings === 1 && guide.imageLoaded && guide.noOverflow);
    check(`${route}: localized links and article schema`,
      Object.keys(guide.alternates).length === 5 &&
      languageRoutes.every(([code, prefix]) =>
        guide.alternates[code] === `${canonicalBase}${prefix}guides/${slug}/`) &&
      guide.alternates["x-default"] === `${canonicalBase}guides/${slug}/` &&
      guide.schema["@type"] === "TechArticle" && guide.schema.url === guide.canonical);
    await audit(guidePage, `${lang}-${slug}`);
    await guidePage.locator(".language-picker summary").click();
    check(`${route}: language switch has four choices`,
      await guidePage.locator(".language-picker a").count() === 4);
    await guidePage.keyboard.press("Escape");
    if (lang === "zh-CN") {
      await guidePage.screenshot({ path: join(output, `${slug}-mobile.png`), fullPage: true });
      await guidePage.setViewportSize({ width: 1440, height: 900 });
      await guidePage.screenshot({ path: join(output, `${slug}-desktop.png`), fullPage: true });
    }
    await guidePage.close();
  }
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
