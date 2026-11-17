const test = require("node:test");
const assert = require("node:assert/strict");

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
    const image = new URL(campaign.image, canonicalOrigin).href;
    assert.equal(attribute(html, 'meta property="og:title"', "content"), campaign.title);
    assert.equal(attribute(html, 'meta property="og:description"', "content"), campaign.summary);
    assert.equal(attribute(html, 'meta property="og:image"', "content"), image);
    assert.equal(attribute(html, 'meta property="og:url"', "content"), canonical);
    assert.equal(attribute(html, 'meta property="og:type"', "content"), "website");
    assert.equal(attribute(html, 'meta property="og:site_name"', "content"), "Kindwell");
    assert.equal(attribute(html, 'meta name="twitter:card"', "content"), "summary_large_image");
    assert.equal(attribute(html, 'meta name="twitter:image"', "content"), image);
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

test("escaped content, draft privacy, and relative cover URLs", { skip: !base || !process.env.SOCIAL_TEST_FIXTURES }, async () => {
  const response = await fetch(`${base}/fundraisers/escaping-test`, { headers: { Host: "attacker.example" } });
  assert.equal(response.status, 200);
  const html = await response.text();
  assert.equal(attribute(html, 'meta property="og:title"', "content"), 'A "title" & <test>');
  assert.equal(attribute(html, 'meta property="og:description"', "content"), "A 'description' & <test>");
  assert.equal(attribute(html, 'meta property="og:image"', "content"), `${canonicalOrigin}/uploads/image.jpg?x=1&y=2`);
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
  assert.equal(attribute(html, 'meta property="og:image"', "content"), `${canonicalOrigin}/uploads/87f9be4703a14979b16ce3710dbd4ac9.jpg`);
  assert.equal(attribute(html, 'meta name="twitter:image"', "content"), `${canonicalOrigin}/uploads/87f9be4703a14979b16ce3710dbd4ac9.jpg`);
});
