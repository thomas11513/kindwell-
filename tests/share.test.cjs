const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function shareHelpers(clipboard) {
  const source = fs.readFileSync(path.join(__dirname, "..", "wwwroot", "app.js"), "utf8");
  const start = source.indexOf("function fundraiserUrl(");
  const end = source.indexOf("function openShareDialog(", start);
  const context = { URL, location: { origin: "https://kindwell.example" }, navigator: { clipboard } };
  vm.runInNewContext(source.slice(start, end) + "\nglobalThis.helpers = { fundraiserUrl, copyFundraiserLink };", context);
  return context.helpers;
}

test("fundraiser links use the encoded slug and site origin", () => {
  const { fundraiserUrl } = shareHelpers();
  assert.equal(fundraiserUrl({ slug: "a cause/with spaces" }), "https://kindwell.example/fundraisers/a%20cause%2Fwith%20spaces");
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
