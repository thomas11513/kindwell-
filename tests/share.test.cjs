const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function shareHelpers(clipboard, canonical = null) {
  const source = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "app.js"), "utf8");
  const start = source.indexOf("function fundraiserUrl(");
  const end = source.indexOf("function openShareDialog(", start);
  const context = { URL, location: { origin: "https://kindwell.example" }, document: { querySelector: () => canonical && { href: canonical } }, navigator: { clipboard } };
  vm.runInNewContext(source.slice(start, end) + "\nglobalThis.helpers = { fundraiserUrl, socialShareUrls, copyFundraiserLink };", context);
  return context.helpers;
}

test("fundraiser links use the encoded slug and Kindwell domain without a canonical tag", () => {
  const { fundraiserUrl } = shareHelpers();
  assert.equal(fundraiserUrl({ slug: "a cause/with spaces" }), "https://kindwell.care/fundraisers/a%20cause%2Fwith%20spaces");
});

test("sharing uses a matching server-rendered canonical fundraiser URL", () => {
  const { fundraiserUrl } = shareHelpers(null, "https://kindwell.care/fundraisers/my-cause");
  assert.equal(fundraiserUrl({ slug: "my-cause" }), "https://kindwell.care/fundraisers/my-cause");
  assert.equal(fundraiserUrl({ slug: "another" }), "https://kindwell.care/fundraisers/another");
});

test("sharing never uses a Render canonical URL", () => {
  const { fundraiserUrl } = shareHelpers(null, "https://kindwell.onrender.com/fundraisers/my-cause");
  assert.equal(fundraiserUrl({ slug: "my-cause" }), "https://kindwell.care/fundraisers/my-cause");
});

test("Facebook and WhatsApp links contain the fundraiser URL and title", () => {
  const { socialShareUrls } = shareHelpers();
  const url = "https://kindwell.care/fundraisers/a-cause";
  const links = socialShareUrls({ title: "Help & care" }, url);
  const facebook = new URL(links.facebook);
  const whatsapp = new URL(links.whatsapp);
  assert.equal(facebook.origin + facebook.pathname, "https://www.facebook.com/sharer/sharer.php");
  assert.equal(facebook.searchParams.get("u"), url);
  assert.equal(whatsapp.origin + whatsapp.pathname, "https://api.whatsapp.com/send");
  assert.equal(whatsapp.searchParams.get("text"), `Help & care\n${url}`);
});

test("copying the fundraiser link reports success", async () => {
  let copied;
  const { copyFundraiserLink } = shareHelpers({ writeText: async value => { copied = value; } });
  const notice = {};
  await copyFundraiserLink("https://kindwell.example/fundraisers/cause", notice);
  assert.equal(copied, "https://kindwell.example/fundraisers/cause");
  assert.equal(notice.textContent, "Link copied.");
});

test("copying reports clipboard failures and allows manual copying", async () => {
  for (const clipboard of [undefined, { writeText: async () => { throw new Error("denied"); } }]) {
    const { copyFundraiserLink } = shareHelpers(clipboard);
    const notice = {};
    await copyFundraiserLink("https://kindwell.example/fundraisers/cause", notice);
    assert.match(notice.textContent, /select and copy it manually/);
  }
});

function shareDialog(reducedMotion = false, fetchVideo = async () => ({ ok: false, status: 503, json: async () => ({ detail: "Video generation is busy." }) }), category = "Medical") {
  const source = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "app.js"), "utf8");
  const start = source.indexOf("function openShareDialog(");
  const end = source.indexOf("function slugify(", start);
  const element = () => ({
    style: {}, attributes: {}, listeners: {},
    classList: { toggle() {} },
    setAttribute(name, value) { this.attributes[name] = value; },
    removeAttribute(name) { delete this.attributes[name]; },
    addEventListener(name, callback) { this.listeners[name] = callback; },
    insertAdjacentHTML(position, html) { this.insertedHTML = html; }, focus() {}
  });
  const track = element(), slides = element(), cards = [element(), element()], dots = [element(), element()];
  const platforms = [element(), element()];
  platforms[0].dataset = { videoPlatform: "TikTok" };
  platforms[1].dataset = { videoPlatform: "YouTube Shorts" };
  const elements = {
    ".share-dialog": element(), ".share-status": element(), ".share-slide-dots": element(),
    ".share-slides": slides, ".share-slide-track": track, ".modal-close": element(),
    ".share-social": element(), ".share-video-instructions": element(), "#share-video-download": element(),
    ".modal-backdrop": element(), "#share-link": element(), "#share-copy": element()
  };
  const root = {
    innerHTML: "",
    querySelector: selector => elements[selector] || null,
    querySelectorAll: selector => selector === ".share-slide" ? cards : selector === ".share-slide-dot" ? dots : platforms
  };
  let scheduled, nextId = 0;
  const downloaded = {};
  const document = {
    body: { style: { overflow: "auto" } }, querySelector: () => root, addEventListener() {}, removeEventListener() {},
    createElement: () => ({ click() { downloaded.href = this.href; downloaded.filename = this.download; } })
  };
  const context = {
    document, navigator: {}, window: { matchMedia: () => ({ matches: reducedMotion }) }, AbortController, fetch: fetchVideo,
    URL: { createObjectURL: () => "blob:video", revokeObjectURL() {} },
    fundraiserUrl: () => "https://kindwell.example/fundraisers/cause",
    socialShareUrls: () => ({ facebook: "facebook", whatsapp: "whatsapp" }),
    progress: () => 25, money: value => `$${value / 100}`,
    esc: value => String(value ?? "").replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]),
    setTimeout: (callback, delay) => { scheduled = { callback, delay, id: ++nextId }; return nextId; },
    clearTimeout: id => { if (scheduled?.id === id) scheduled = null; }
  };
  vm.runInNewContext(source.slice(start, end) + "\nglobalThis.openShareDialog = openShareDialog;", context);
  const trigger = { isConnected: true, focused: false, focus() { this.focused = true; } };
  context.openShareDialog({ slug: "a-cause", title: "A <cause>", image: "/cover.jpg", organizerName: "An organizer", raised: 2500, goal: 10000, story: "A real & important story.", category }, trigger);
  return { root, track, cards, dots, slides, elements, platforms, document, trigger, downloaded, scheduled: () => scheduled, advance: () => { const task = scheduled; scheduled = null; task.callback(); } };
}

test("preview uses fundraiser content and cycles after five seconds on each slide", () => {
  const dialog = shareDialog();
  assert.match(dialog.root.innerHTML, /A &lt;cause&gt;/);
  assert.match(dialog.root.innerHTML, /A real &amp; important story\./);
  assert.match(dialog.root.innerHTML, /An organizer/);
  assert.match(dialog.root.innerHTML, /\/cover\.jpg/);
  assert.match(dialog.root.innerHTML, /\$25.*\$100/);
  assert.equal(dialog.scheduled().delay, 5000);
  dialog.advance();
  assert.equal(dialog.track.style.transform, "translateX(-50%)");
  assert.equal(dialog.cards[0].attributes["aria-hidden"], "true");
  assert.equal(dialog.dots[1].attributes["aria-current"], "true");
  assert.equal(dialog.scheduled().delay, 5500);
  dialog.advance();
  assert.equal(dialog.track.style.transform, "translateX(-0%)");
  assert.equal(dialog.scheduled().delay, 5500);
  dialog.elements[".modal-close"].listeners.click();
  assert.equal(dialog.scheduled(), null);
  assert.equal(dialog.document.body.style.overflow, "auto");
  assert.equal(dialog.trigger.focused, true);
});

test("the progress ring uses the fundraiser category and defaults for unknown categories", () => {
  for (const [category, expected] of [["Medical", "Medical"], ["Education", "Education"], ["Emergency", "Emergency"], ["Community", "Community"], ["Animals", "Animals"], ["Memorial", "Memorial"], ["Environment", "Environment"], ["Other", "Other"], ["unrecognized", "Other"]]) {
    const dialog = shareDialog(false, undefined, category);
    assert.ok(dialog.root.innerHTML.includes(`/category-icons.svg#${expected}`));
    assert.match(dialog.root.innerHTML, /role="progressbar"[^>]*aria-valuenow="25"/);
  }
});

test("each social sharing option has its own icon and label", () => {
  const dialog = shareDialog();
  for (const [html, labels] of [
    [dialog.elements[".share-slide-dots"].insertedHTML, ["Facebook", "WhatsApp"]],
    [dialog.elements[".share-social"].insertedHTML, ["TikTok", "YouTube"]]
  ]) {
    assert.equal((html.match(/class="share-platform-icon"/g) || []).length, 2);
    for (const label of labels) assert.ok(html.includes(label));
  }
});

test("manual navigation restarts the timer and reduced motion skips transition time", () => {
  const dialog = shareDialog(true);
  dialog.dots[1].listeners.click();
  assert.equal(dialog.track.style.transform, "translateX(-50%)");
  assert.equal(dialog.scheduled().delay, 5000);
  dialog.slides.listeners.touchstart({ touches: [{ clientX: 20 }] });
  dialog.slides.listeners.touchend({ changedTouches: [{ clientX: 90 }] });
  assert.equal(dialog.track.style.transform, "translateX(-0%)");
  assert.equal(dialog.scheduled().delay, 5000);
});

test("video sharing gives platform-specific instructions and reports generation errors", async () => {
  let requested;
  const dialog = shareDialog(false, async url => {
    requested = url;
    return { ok: false, status: 503, json: async () => ({ detail: "Video generation is busy." }) };
  });

  dialog.platforms[0].listeners.click();
  assert.match(dialog.elements[".share-video-instructions"].textContent, /TikTok.*upload it/);
  dialog.platforms[1].listeners.click();
  assert.match(dialog.elements[".share-video-instructions"].textContent, /YouTube.*Short/);
  await dialog.elements["#share-video-download"].listeners.click();
  assert.equal(requested, "/social-videos/a-cause.mp4");
  assert.equal(dialog.elements[".share-status"].textContent, "Video generation is busy.");
  assert.equal(dialog.elements["#share-video-download"].disabled, false);
});

test("a successful video request downloads the MP4", async () => {
  const dialog = shareDialog(false, async () => ({ ok: true, blob: async () => new Blob(["video"]) }));
  await dialog.elements["#share-video-download"].listeners.click();
  assert.equal(dialog.downloaded.href, "blob:video");
  assert.equal(dialog.downloaded.filename, "kindwell-fundraiser.mp4");
  assert.match(dialog.elements[".share-status"].textContent, /Video ready/);
});
