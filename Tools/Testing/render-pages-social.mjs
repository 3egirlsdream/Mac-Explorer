// Export the actual homepage demo as a 1200 × 630 social image.
// Usage: NODE_PATH=<Playwright modules> node Tools/Testing/render-pages-social.mjs [base URL] [PNG output]
import { createRequire } from "node:module";
import { resolve } from "node:path";
const require = createRequire(import.meta.url);
const { chromium } = require("playwright");
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({
    viewport: { width: 1200, height: 630 },
    deviceScaleFactor: 1,
    colorScheme: "light",
  });
  await page.goto(process.argv[2] || "http://127.0.0.1:4317/", {
    waitUntil: "networkidle",
  });
  await page.evaluate(() => {
    document.documentElement.dataset.theme = "light";
    const card = document.createElement("main");
    card.className = "social-card";
    const copy = document.createElement("div");
    copy.className = "social-copy";
    copy.append(document.querySelector(".brand").cloneNode(true));
    copy.append(document.querySelector("h1").cloneNode(true));
    const summary = document.createElement("p");
    summary.textContent =
      "浏览、整理、查找、处理。\n为 macOS 打造的开源文件工作台。";
    copy.append(summary);
    const tags = document.createElement("div");
    tags.className = "social-tags";
    for (const label of ["多窗格", "标签收藏", "文字搜索", "文件速递"]) {
      const item = document.createElement("span");
      item.textContent = label;
      tags.append(item);
    }
    copy.append(tags);
    const stage = document.createElement("div");
    stage.className = "social-stage";
    stage.append(document.querySelector(".app-window").cloneNode(true));
    const caption = document.createElement("p");
    caption.className = "social-caption";
    caption.textContent = "MAC EXPLORER  /  界面示意";
    card.append(copy, stage, caption);
    document.body.replaceChildren(card);
  });
  await page.addStyleTag({
    content: `
    html, body { width: 1200px; height: 630px; overflow: hidden; }
    .social-card { width:1200px; height:630px; position:relative; background:#f6f7f9; }
    .social-copy { position:absolute; left:56px; top:57px; width:405px; }
    .social-copy .brand { display:flex; gap:10px; font-size:19px; margin-bottom:48px; }
    .social-copy .brand img { width:38px; height:38px; }
    .social-copy h1 { text-align:left; font-size:56px; line-height:1.18; letter-spacing:-2px; margin:0; }
    .social-copy h1 span { color:#2463d4; }
    .social-copy > p { white-space:pre-line; color:#555e6d; font-size:17px; line-height:1.9; margin-top:25px; }
    .social-tags { display:flex; flex-wrap:wrap; gap:8px; margin-top:25px; }
    .social-tags span { font-size:11px; padding:5px 9px; background:#fff; border:1px solid #e6e9ee; border-radius:5px; }
    .social-stage { position:absolute; left:496px; top:140px; width:645px; }
    .social-stage .app-window { width:1040px; max-width:none; transform:scale(.62); transform-origin:top left; box-shadow:0 30px 70px -20px #26395745; }
    .social-caption { position:absolute; right:60px; bottom:47px; color:#606a79; font-size:10px; letter-spacing:2px; }
  `,
  });
  await page.evaluate(() => document.fonts.ready);
  const output = resolve(process.argv[3] || "docs/Assets/social-preview.png");
  await page.screenshot({ path: output });
  console.log(output);
} finally {
  await browser.close();
}
