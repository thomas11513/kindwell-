const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const base = process.env.SOCIAL_TEST_URL;
const canonicalOrigin = process.env.SOCIAL_TEST_CANONICAL_ORIGIN || "https://kindwell.care";
const integration = { skip: !base && "Set SOCIAL_TEST_URL to an isolated running Kindwell server" };
const decode = value => value.replace(/&#x([0-9a-f]+);/gi, (_, code) => String.fromCodePoint(parseInt(code, 16)))
  .replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number(code)))
  .replace(/&quot;/g, '"').replace(/&amp;/g, "&").replace(/&lt;/g, "<").replace(/&gt;/g, ">");
function attribute(html, selector, name) {
  const tag = html.match(new RegExp(`<${selector}[^>]*>`, "i"))?.[0];
  const value = tag?.match(new RegExp(`\\b${name}="([^"]*)"`, "i"))?.[1];
  assert.notEqual(value, undefined, `Missing ${selector} ${name}`);
  return decode(value);
}
function socialImage(html, slug) {
  const image = attribute(html, 'meta property="og:image"', "content");
  assert.match(image, new RegExp(`^${canonicalOrigin}/social-images/${slug}\\.jpg\\?v=[0-9a-f]{20}$`));
  assert.equal(attribute(html, 'meta name="twitter:image"', "content"), image);
  assert.equal(attribute(html, 'meta property="og:image:width"', "content"), "1200");
  assert.equal(attribute(html, 'meta property="og:image:height"', "content"), "630");
  assert.equal(attribute(html, 'meta property="og:image:type"', "content"), "image/jpeg");
  return new URL(image);
}
function jpegDimensions(bytes) {
  assert.equal(bytes.readUInt16BE(0), 0xffd8);
  let offset = 2;
  while (offset < bytes.length) {
    assert.equal(bytes[offset++], 0xff);
    const marker = bytes[offset++];
    const length = bytes.readUInt16BE(offset);
    if ([0xc0, 0xc1, 0xc2].includes(marker))
      return [bytes.readUInt16BE(offset + 5), bytes.readUInt16BE(offset + 3)];
    offset += length;
  }
  throw new Error("JPEG has no dimensions");
}

for (const slug of ["a-brighter-start-for-maya", "keeping-the-neighborhood-garden-growing"]) {
  test(`server renders social metadata for ${slug}`, integration, async () => {
    const api = await fetch(`${base}/api/fundraisers/${slug}`);
    assert.equal(api.status, 200);
    const { campaign } = await api.json();
    const response = await fetch(`${base}/fundraisers/${slug}`);
    assert.equal(response.status, 200);
    assert.match(response.headers.get("content-type"), /^text\/html/);
    const html = await response.text();
    const canonical = `${canonicalOrigin}/fundraisers/${slug}`;
    assert.equal(attribute(html, 'meta property="og:title"', "content"), campaign.title);
    assert.equal(attribute(html, 'meta property="og:description"', "content"), campaign.summary);
    socialImage(html, slug);
    assert.equal(attribute(html, 'meta property="og:url"', "content"), canonical);
    assert.equal(attribute(html, 'meta property="og:type"', "content"), "website");
    assert.equal(attribute(html, 'meta property="og:site_name"', "content"), "Kindwell");
    assert.equal(attribute(html, 'meta name="twitter:card"', "content"), "summary_large_image");
    assert.equal(attribute(html, 'link rel="canonical"', "href"), canonical);
    assert.match(html, /<title>.* \| Kindwell<\/title>/);
  });
}

test("unknown fundraisers return 404 and the homepage retains its default HTML", integration, async () => {
  assert.equal((await fetch(`${base}/fundraisers/not-a-real-fundraiser`)).status, 404);
  const homepage = await (await fetch(base)).text();
  assert.match(homepage, /Kindwell — Good things grow together/);
  assert.doesNotMatch(homepage, /property="og:title"/);
});

test("category changes update the static social image icon and URL", {
  skip: !base || !process.env.SOCIAL_TEST_DATABASE || !process.env.SOCIAL_TEST_COVER
}, async () => {
  const { DatabaseSync } = require("node:sqlite");
  const db = new DatabaseSync(process.env.SOCIAL_TEST_DATABASE);
  const slug = "a-brighter-start-for-maya";
  const original = db.prepare("SELECT category,image_url FROM fundraisers WHERE slug=?").get(slug);
  const update = db.prepare("UPDATE fundraisers SET category=?,image_url=? WHERE slug=?");
  const snapshot = async category => {
    update.run(category, process.env.SOCIAL_TEST_COVER, slug);
    const url = socialImage(await (await fetch(`${base}/fundraisers/${slug}`)).text(), slug);
    const response = await fetch(new URL(url.pathname + url.search, base));
    assert.equal(response.status, 200);
    const image = Buffer.from(await response.arrayBuffer());
    const pixels = spawnSync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-vf", "crop=100:100:550:405", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"], { input: image });
    assert.equal(pixels.status, 0, pixels.stderr.toString());
    assert.equal(pixels.stdout.length, 100 * 100 * 3);
    return { url: url.href, pixels: pixels.stdout };
  };
  try {
    const medical = await snapshot("Medical");
    const education = await snapshot("Education");
    assert.notEqual(medical.url, education.url);
    assert.notDeepEqual(medical.pixels, education.pixels);
  } finally {
    update.run(original.category, original.image_url, slug);
    db.close();
  }
});

test("escaped content, draft privacy, and relative cover URLs", { skip: !base || !process.env.SOCIAL_TEST_FIXTURES }, async () => {
  const response = await fetch(`${base}/fundraisers/escaping-test`, { headers: { Host: "attacker.example" } });
  assert.equal(response.status, 200);
  const html = await response.text();
  assert.equal(attribute(html, 'meta property="og:title"', "content"), 'A "title" & <test>');
  assert.equal(attribute(html, 'meta property="og:description"', "content"), "A 'description' & <test>");
  socialImage(html, "escaping-test");
  assert.equal(attribute(html, 'meta property="og:url"', "content"), `${canonicalOrigin}/fundraisers/escaping-test`);
  assert.doesNotMatch(html, /attacker\.example/);
  assert.doesNotMatch(html, /content="A "title"/);
  const draft = await fetch(`${base}/fundraisers/draft-test`);
  assert.equal(draft.status, 404);
  assert.doesNotMatch(await draft.text(), /Private title/);
});

test("live fundraiser example uses its own title, summary, URL, and cover", { skip: !base || !process.env.SOCIAL_TEST_FIXTURES }, async () => {
  const slug = "help-me-repair-my-car-so-i-can-keep-working";
  const html = await (await fetch(`${base}/fundraisers/${slug}`)).text();
  assert.equal(attribute(html, 'meta property="og:title"', "content"), "Help Me Repair My Car So I Can Keep Working");
  assert.equal(attribute(html, 'meta property="og:description"', "content"), "My name is Michael Foster, and I'm raising funds to repair my car so I can continue getting to work.");
  assert.equal(attribute(html, 'meta property="og:url"', "content"), `${canonicalOrigin}/fundraisers/${slug}`);
  socialImage(html, slug);
});

test("generated images use each cover without changing the original", { skip: !base || !process.env.SOCIAL_TEST_FIXTURES }, async () => {
  const images = [];
  for (const slug of ["social-wide", "social-portrait"]) {
    const { campaign } = await (await fetch(`${base}/api/fundraisers/${slug}`)).json();
    const originalUrl = new URL(campaign.image, base);
    const original = Buffer.from(await (await fetch(originalUrl)).arrayBuffer());
    const html = await (await fetch(`${base}/fundraisers/${slug}`)).text();
    const social = socialImage(html, slug);
    const response = await fetch(new URL(social.pathname + social.search, base));
    assert.equal(response.status, 200);
    assert.match(response.headers.get("content-type"), /^image\/jpeg/);
    const bytes = Buffer.from(await response.arrayBuffer());
    assert.deepEqual(jpegDimensions(bytes), [1200, 630]);
    assert.deepEqual(Buffer.from(await (await fetch(originalUrl)).arrayBuffer()), original);
    images.push(bytes);
    social.searchParams.set("v", "outdated");
    assert.equal((await fetch(new URL(social.pathname + social.search, base))).status, 200);
  }
  assert.notDeepEqual(images[0], images[1]);
  assert.equal((await fetch(`${base}/social-images/draft-test.jpg`)).status, 404);
  assert.equal((await fetch(`${base}/social-images/not-a-real-fundraiser.jpg`)).status, 404);
  for (const slug of ["social-missing", "social-invalid", "social-private"])
    assert.equal((await fetch(`${base}/social-images/${slug}.jpg`)).status, 502);
});

test("portrait covers fill both edges of the photo area", {
  skip: !base || !process.env.SOCIAL_TEST_FIXTURES || process.platform !== "win32"
}, async () => {
  const response = await fetch(`${base}/social-images/social-portrait.jpg`);
  assert.equal(response.status, 200);
  const jpeg = Buffer.from(await response.arrayBuffer()).toString("base64");
  const script = `
    Add-Type -AssemblyName System.Drawing
    $stream = [System.IO.MemoryStream]::new([Convert]::FromBase64String([Console]::In.ReadToEnd()))
    $bitmap = [System.Drawing.Bitmap]::new($stream)
    try {
      @(@(510,200), @(1190,200)) | ForEach-Object {
        $color = $bitmap.GetPixel($_[0], $_[1])
        "$($color.R),$($color.G),$($color.B)"
      }
    } finally { $bitmap.Dispose(); $stream.Dispose() }
  `;
  const pixels = spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
    input: jpeg, encoding: "utf8"
  });
  assert.equal(pixels.status, 0, pixels.stderr);
  const colors = pixels.stdout.trim().split(/\r?\n/).map(line => line.split(",").map(Number));
  assert.equal(colors.length, 2);
  for (const [red, green, blue] of colors) assert.ok(red < 50 && green < 50 && blue > 200);
});

test("updating an uploaded cover changes the social image URL", { skip: !base || !process.env.SOCIAL_TEST_FIXTURES || !process.env.SOCIAL_TEST_UPLOAD_DIR }, async () => {
  const file = path.join(process.env.SOCIAL_TEST_UPLOAD_DIR, "wide.png");
  const original = await fs.readFile(file);
  const times = await fs.stat(file);
  const before = socialImage(await (await fetch(`${base}/fundraisers/social-wide`)).text(), "social-wide");
  try {
    await fs.copyFile(path.join(process.env.SOCIAL_TEST_UPLOAD_DIR, "portrait.png"), file);
    await fs.utimes(file, new Date(), new Date(times.mtimeMs + 5000));
    const after = socialImage(await (await fetch(`${base}/fundraisers/social-wide`)).text(), "social-wide");
    assert.notEqual(after.href, before.href);
    assert.equal((await fetch(new URL(before.pathname + before.search, base))).status, 200);
    assert.equal((await fetch(new URL(after.pathname + after.search, base))).status, 200);
  } finally {
    await fs.writeFile(file, original);
    await fs.utimes(file, times.atime, times.mtime);
  }
});

test("card and image URL update when fundraiser details and totals change", { skip: !base || !process.env.SOCIAL_TEST_FIXTURES || !process.env.SOCIAL_TEST_DATABASE }, async () => {
  const { DatabaseSync } = require("node:sqlite");
  const db = new DatabaseSync(process.env.SOCIAL_TEST_DATABASE);
  const original = db.prepare("SELECT title,organizer_name,goal,raised,reported_raised FROM fundraisers WHERE slug=?").get("social-wide");
  const update = db.prepare("UPDATE fundraisers SET title=?,organizer_name=?,goal=?,raised=?,reported_raised=? WHERE slug='social-wide'");
  const snapshot = async () => {
    const html = await (await fetch(`${base}/fundraisers/social-wide`)).text();
    const image = socialImage(html, "social-wide");
    const response = await fetch(new URL(image.pathname + image.search, base));
    assert.equal(response.status, 200);
    return { html, image: image.href, bytes: Buffer.from(await response.arrayBuffer()) };
  };
  try {
    let values = ["A fundraiser title", "First Organizer", 100000, 0, 0];
    update.run(...values);
    let previous = await snapshot();
    for (const [index, value] of [[0, "A different fundraiser title"], [1, "Another Organizer"], [2, 200000], [3, 50000], [4, 150000]]) {
      values[index] = value;
      update.run(...values);
      const current = await snapshot();
      assert.notEqual(current.image, previous.image);
      assert.notDeepEqual(current.bytes, previous.bytes);
      assert.equal(attribute(current.html, 'meta property="og:description"', "content"), "Wide cover");
      previous = current;
    }
    values[0] = "帮助我的妈妈继续治疗 🎉";
    values[1] = "محمد أحمد";
    update.run(...values);
    const international = await snapshot();
    assert.equal(attribute(international.html, 'meta property="og:title"', "content"), values[0]);
    assert.notDeepEqual(international.bytes, previous.bytes);
    values[0] = "ช่วยเหลือครอบครัว";
    values[1] = "W".repeat(100);
    update.run(...values);
    const longName = await snapshot();
    assert.notEqual(longName.image, international.image);
  } finally {
    update.run(original.title, original.organizer_name, original.goal, original.raised, original.reported_raised);
    db.close();
  }
});
