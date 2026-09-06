// Render social-card.html to an exact 1280x640 PNG via headless Chrome + puppeteer-core.
// Renders at deviceScaleFactor 2 (2560x1280) for crisp text, caller downscales to 1280x640.
// Usage: CHROME=<chrome-bin> node render.js social-card.html social-card@2x.png
// Driven by scripts/refresh-social-card.sh, which is what you should run.
const puppeteer = require("puppeteer-core");
const path = require("path");

(async () => {
  const [, , htmlFile, outFile] = process.argv;
  const browser = await puppeteer.launch({
    executablePath: process.env.CHROME,
    headless: "new",
    args: ["--no-sandbox", "--force-color-profile=srgb"],
  });
  try {
    const page = await browser.newPage();
    await page.setViewport({ width: 1280, height: 640, deviceScaleFactor: 2 });
    await page.goto("file://" + path.resolve(htmlFile), { waitUntil: "networkidle0" });
    await page.screenshot({
      path: outFile,
      clip: { x: 0, y: 0, width: 1280, height: 640 },
    });
  } finally {
    await browser.close();
  }
})();
