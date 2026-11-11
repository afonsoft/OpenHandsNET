#!/usr/bin/env node
// Sandcastle QA driver — executes a batch of browser steps with
// Playwright inside the sandbox runtime image (SPEC-131/142).
// Usage: node qa-driver.mjs <job.json>
// job:  { "base_url": "http://localhost:3000", "out_dir": "/tmp/qa-shots",
//         "video_dir": "/tmp/qa-video", "timeout_ms": 30000,
//         "steps": [ {kind:"browser", action:"goto", url:"/"}, ... ] }
// stdout: JSON {ok, error?, video?, steps:[{ok, detail?, screenshot?}]}
// Screenshots land in out_dir/step-<n>.png; video (webm) in video_dir.
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';

const job = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const baseUrl = job.base_url;
const outDir = job.out_dir;
const stepTimeout = job.timeout_ms ?? 30000;
const result = { ok: true, error: null, video: null, steps: [] };
let page = null;
let videoPath = null;

const resolve = (u) =>
  /^https?:\/\//i.test(u) ? u : baseUrl.replace(/\/$/, '') + u;

const expectCheck = (expect, content, url) => {
  if (!expect) return null;
  const [op, ...rest] = expect.split(':');
  const arg = rest.join(':');
  if (op === 'contains')
    return content.includes(arg) ? null : `expected content to contain "${arg}"`;
  if (op === 'not_contains')
    return content.includes(arg) ? `expected content NOT to contain "${arg}"` : null;
  if (op === 'url_contains')
    return url.includes(arg) ? null : `expected url to contain "${arg}"`;
  return null;
};

try {
  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });
  const ctx = await browser.newContext({
    viewport: { width: 1280, height: 720 },
    recordVideo: job.video_dir
      ? { dir: job.video_dir, size: { width: 1280, height: 720 } }
      : undefined,
  });
  page = await ctx.newPage();
  page.setDefaultTimeout(stepTimeout);

  for (let i = 0; i < job.steps.length; i++) {
    const s = job.steps[i];
    const shot = path.join(outDir, `step-${i + 1}.png`);
    const r = { ok: true, detail: null, screenshot: shot };
    try {
      switch (s.action) {
        case 'goto':
          await page.goto(resolve(s.url ?? '/'), {
            waitUntil: 'domcontentloaded', timeout: stepTimeout });
          break;
        case 'click':
          await page.click(s.selector, { timeout: stepTimeout });
          break;
        case 'type':
          await page.fill(s.selector, s.text ?? '', { timeout: stepTimeout });
          break;
        case 'scroll':
          await page.mouse.wheel(0, 600);
          break;
        case 'back':
          await page.goBack({ timeout: stepTimeout });
          break;
        case 'forward':
          await page.goForward({ timeout: stepTimeout });
          break;
        case 'wait':
          if (s.timeout_seconds) {
            await page.waitForTimeout(
              Math.min(s.timeout_seconds, 60) * 1000);
          } else if (s.text) {
            await page.waitForSelector(`text=${s.text}`,
              { timeout: stepTimeout });
          }
          break;
        case 'screenshot':
          break; // captured below — action is "take screenshot"
        default:
          r.ok = false;
          r.detail = `unknown action ${s.action}`;
      }
      if (r.ok) {
        await page.waitForLoadState('domcontentloaded')
          .catch(() => {});
        const content = await page.evaluate(
          () => document.body?.innerText ?? '').catch(() => '');
        const err = expectCheck(s.expect, content, page.url());
        if (err) { r.ok = false; r.detail = err; }
        else { r.detail = page.url(); }
      }
    } catch (e) {
      r.ok = false;
      r.detail = String(e).slice(0, 400);
    }
    try { await page.screenshot({ path: shot }); }
    catch { r.screenshot = null; }
    result.steps.push(r);
    if (!r.ok && (job.stop_on_fail ?? true)) {
      for (let j = i + 1; j < job.steps.length; j++) {
        result.steps.push(
          { ok: false, detail: 'skipped (previous failure)', screenshot: null });
      }
      break;
    }
  }
  await ctx.close();
  await browser.close();
  try {
    const files = fs.readdirSync(job.video_dir ?? '/dev/null')
      .filter((f) => f.endsWith('.webm'))
      .map((f) => path.join(job.video_dir, f))
      .sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
    videoPath = files[0] ?? null;
  } catch { videoPath = null; }
} catch (e) {
  result.ok = false;
  result.error = String(e).slice(0, 400);
}
result.video = videoPath;
console.log(JSON.stringify(result));
