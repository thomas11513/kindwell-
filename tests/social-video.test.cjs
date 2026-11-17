const test = require("node:test");
const assert = require("node:assert/strict");
const { spawnSync } = require("node:child_process");

const base = process.env.SOCIAL_VIDEO_TEST_URL;
const database = process.env.SOCIAL_VIDEO_TEST_DATABASE;
const cover = process.env.SOCIAL_VIDEO_TEST_SECOND_COVER;

test("video uses current published fundraiser data and is a mobile-compatible MP4", {
  skip: !base || !database || !cover ? "Set an isolated server, database and second cover fixture" : false,
  timeout: 360000
}, async () => {
  const { DatabaseSync } = require("node:sqlite");
  const db = new DatabaseSync(database);
  const slug = "a-brighter-start-for-maya";
  const original = db.prepare("SELECT image_url,title,story,raised FROM fundraisers WHERE slug=?").get(slug);
  assert.ok(original);
  const update = db.prepare("UPDATE fundraisers SET image_url=?,title=?,story=?,raised=? WHERE slug=?");
  async function download() {
    const response = await fetch(`${base}/social-videos/${slug}.mp4`);
    if (!response.ok) assert.fail(`Video request failed (${response.status}): ${await response.text()}`);
    assert.match(response.headers.get("content-type"), /^video\/mp4/);
    assert.match(response.headers.get("cache-control"), /no-store/);
    return Buffer.from(await response.arrayBuffer());
  }
  try {
    assert.equal((await fetch(`${base}/social-videos/not-a-real-fundraiser.mp4`)).status, 404);
    const first = await download();
    const probe = spawnSync("ffprobe", ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height,pix_fmt:format=duration", "-of", "json", "-i", "pipe:0"], { input: first, encoding: "utf8" });
    assert.equal(probe.status, 0, probe.stderr);
    const info = JSON.parse(probe.stdout);
    const video = info.streams.find(stream => stream.codec_type === "video");
    const audio = info.streams.find(stream => stream.codec_type === "audio");
    assert.equal(video.codec_name, "h264");
    assert.equal(video.pix_fmt, "yuv420p");
    assert.equal(video.width, 1080);
    assert.equal(video.height, 1920);
    assert.equal(audio.codec_name, "aac");
    assert.equal(Number(info.format.duration), 13);
    assert.ok(first.length < 20_000_000);
    function volume(start, end) {
      const result = spawnSync("ffmpeg", ["-hide_banner", "-nostats", "-i", "pipe:0", "-vn", "-af", `atrim=start=${start}:end=${end},volumedetect`, "-f", "null", "-"], { input: first, encoding: "utf8" });
      assert.equal(result.status, 0, result.stderr);
      const peak = result.stderr.match(/max_volume:\s*(-?[\d.]+) dB/);
      assert.ok(peak, result.stderr);
      return Number(peak[1]);
    }
    const middle = volume(2, 3);
    assert.ok(volume(0, 0.1) < middle - 8, "music fades in");
    assert.ok(volume(12.9, 13) < middle - 8, "music fades out");
    update.run(cover, "Updated fundraiser title", "A different fundraiser story.", original.raised + 12345, slug);
    const updated = await download();
    assert.notDeepEqual(updated, first);
  } finally {
    update.run(original.image_url, original.title, original.story, original.raised, slug);
    db.close();
  }
});
