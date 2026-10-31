const appRoot = document.querySelector("#app");
const money = cents => new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 }).format(cents / 100);
const formatCurrency = (minor, code) => new Intl.NumberFormat(undefined, { style: "currency", currency: code, minimumFractionDigits: 0, maximumFractionDigits: 2 }).format(minor / 100);
const DONATION_PRESETS = { USD: [25, 50, 100, 250], NGN: [10000, 25000, 50000, 100000], GBP: [20, 40, 80, 200], EUR: [20, 50, 100, 200] };
const esc = value => String(value ?? "").replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]);
let csrfToken = "";
let campaigns = [];
const PAYMENT_ICONS = {
  paypal: `<span class="payment-icon payment-icon-paypal" aria-hidden="true">P</span>`,
  cashapp: `<span class="payment-icon payment-icon-cashapp" aria-hidden="true">$</span>`,
  venmo: `<span class="payment-icon payment-icon-venmo" aria-hidden="true">V</span>`,
  bank: `<span class="payment-icon payment-icon-bank" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="m3 9 9-5 9 5M5 10v7m4-7v7m6-7v7m4-7v7M3 19h18"/></svg></span>`,
  card: `<span class="payment-icon payment-icon-card" aria-hidden="true"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><rect x="3" y="5" width="18" height="14" rx="2"/><path d="M3 10h18"/></svg></span>`
};

async function request(url, options = {}) {
  const headers = new Headers(options.headers || {});
  if (options.method && options.method !== "GET" && csrfToken) headers.set("X-CSRF-TOKEN", csrfToken);
  if (options.body && !(options.body instanceof FormData)) headers.set("Content-Type", "application/json");
  const response = await fetch(url, { ...options, headers });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload.error || payload.detail || `Request failed (${response.status})`);
  return payload;
}

async function loadCsrf() {
  try { csrfToken = (await request("/api/csrf")).token || ""; } catch { csrfToken = ""; }
}

function brand() {
  return `<a class="brand" href="/" aria-label="Kindwell home"><span class="brand-mark" aria-hidden="true">k</span><span>kindwell</span></a>`;
}

function header() {
  return `<header class="site-header"><div class="header-inner">${brand()}<nav class="main-nav" id="main-nav" aria-label="Main navigation"><a href="/#home">Home</a><a href="/fundraisers">View ongoing fundraisers</a><a href="/people">People</a><a href="/#how-it-works">How it works</a><a href="/#about">About us</a><a href="/#contact">Contact</a></nav><div class="header-actions"><a class="login-link" href="/login">Log in</a><a class="button button-small" href="/#fundraisers">Find a cause</a><button class="mobile-menu" aria-label="Toggle navigation" aria-expanded="false" aria-controls="main-nav"><svg width="19" height="19" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M4 7h16M4 12h16M4 17h16"/></svg></button></div></div></header>`;
}

function footer() {
  return `<footer class="site-footer" id="contact"><div class="footer-inner"><div class="footer-top"><div class="footer-brand">${brand()}<p>Small acts of care can make a lasting difference. Good things grow together.</p></div><div class="footer-links"><a href="/#fundraisers">Browse fundraisers</a><a href="/people">People</a><a href="/#how-it-works">How it works</a><a href="mailto:hello@kindwell.care">Contact</a><a href="/privacy">Privacy</a><a href="/terms">Terms</a><a href="https://instagram.com" target="_blank" rel="noreferrer">Instagram</a><a href="https://facebook.com" target="_blank" rel="noreferrer">Facebook</a><a href="/admin">Administrator</a></div></div><div class="footer-bottom"><span>© 2016 Kindwell. Made with care.</span><span>Questions? <a href="mailto:hello@kindwell.care">hello@kindwell.care</a></span></div></div></footer>`;
}

function progress(campaign) { return Math.min(100, Math.round(campaign.raised / campaign.goal * 100)); }

function slugify(value) {
  return String(value ?? "").trim().toLowerCase().normalize("NFKD").replace(/[^\w\s-]/g, "").replace(/\s+/g, "-").replace(/-+/g, "-").replace(/^-|-$/g, "");
}

function setupLocationPicker(countrySelect, regionWrap, regionSelect, cityInput, savedLocation) {
  const countries = (typeof COUNTRIES !== "undefined" ? COUNTRIES : []);
  const regions = (typeof REGIONS !== "undefined" ? REGIONS : {});
  countrySelect.innerHTML = `<option value="">Choose a country</option>` + countries.map(country => `<option value="${esc(country)}">${esc(country)}</option>`).join("");
  const fillRegions = country => {
    const list = regions[country] || [];
    regionWrap.hidden = list.length === 0;
    regionSelect.innerHTML = `<option value="">Choose a state or region</option>` + list.map(region => `<option value="${esc(region)}">${esc(region)}</option>`).join("");
  };
  countrySelect.addEventListener("change", () => fillRegions(countrySelect.value));
  if (!savedLocation) return;
  const parts = savedLocation.split(",").map(part => part.trim()).filter(Boolean);
  const country = parts.find(part => countries.includes(part));
  if (country) {
    countrySelect.value = country;
    fillRegions(country);
    const region = parts.find(part => (regions[country] || []).includes(part));
    if (region) regionSelect.value = region;
    const city = parts.filter(part => part !== country && part !== region);
    if (city.length) cityInput.value = city.join(", ");
  } else {
    cityInput.value = savedLocation;
  }
}

function organizerProfileLink(campaign) {
  const name = (campaign.organizerName || "").trim();
  if (!name) return "";
  const slug = slugify(name);
  if (!slug) return "";
  const photo = campaign.organizerProfileImage || campaign.image;
  return `<div class="organizer-line"><a class="organizer-badge" href="/people/${encodeURIComponent(slug)}"><img src="${esc(photo)}" alt="${esc(name)} profile photo" loading="lazy"><span><strong>${esc(name)}</strong><small>${esc(campaign.organizerLocation || "Organizer")}</small></span></a></div>`;
}

function card(campaign, index) {
  const href = `/fundraisers/${encodeURIComponent(campaign.slug)}`;
  return `<article class="campaign-card" data-card-slug="${esc(campaign.slug)}" tabindex="0" role="button" aria-label="Donate to ${esc(campaign.title)}" style="animation-delay:${index * 70}ms"><a class="card-image-link" href="${href}" tabindex="-1" aria-hidden="true"><img src="${esc(campaign.image)}" alt="" loading="lazy"></a><div class="card-body"><div class="card-kicker">${esc(campaign.category || "Community")}</div><h3><a href="${href}">${esc(campaign.title)}</a></h3>${organizerProfileLink(campaign)}<p class="card-summary">${esc(campaign.summary)}</p><div class="raised-line"><span><strong>${money(campaign.raised)}</strong> raised</span><span>of ${money(campaign.goal)}</span></div><div class="progress-track" role="progressbar" aria-label="${progress(campaign)} percent funded" aria-valuenow="${progress(campaign)}" aria-valuemin="0" aria-valuemax="100"><div class="progress-fill" style="width:${progress(campaign)}%"></div></div><div class="card-foot"><span class="donor-count">${Number(campaign.donations).toLocaleString()} Kindwell gifts${campaign.reportedDonations ? ` · ${Number(campaign.reportedDonations).toLocaleString()} reported before Kindwell` : ""}</span><a class="button" href="${href}">Donate</a></div></div></article>`;
}

function setupCardNavigation(root, availableCampaigns) {
  if (!root || root.dataset.cardNavigationBound) return;
  root.dataset.cardNavigationBound = "true";
  if (!document.querySelector("#modal-root")) {
    document.body.insertAdjacentHTML("beforeend", `<div id="modal-root"></div>`);
  }
  const openCardDonation = cardElement => {
    const campaign = availableCampaigns.find(item => item.slug === cardElement.dataset.cardSlug);
    if (campaign) openDonation(campaign);
  };
  root.addEventListener("click", event => {
    if (event.target.closest("a, button, input, select, textarea")) return;
    const cardElement = event.target.closest("[data-card-slug]");
    if (cardElement) openCardDonation(cardElement);
  });
  root.addEventListener("keydown", event => {
    if (event.target !== event.currentTarget && event.target.closest("a, button, input, select, textarea")) return;
    if (event.key !== "Enter" && event.key !== " ") return;
    const cardElement = event.target.closest("[data-card-slug]");
    if (!cardElement) return;
    event.preventDefault();
    openCardDonation(cardElement);
  });
}

async function renderHome() {
  try { campaigns = await request("/api/fundraisers"); } catch { campaigns = []; }
  document.title = "Kindwell — Good things grow together";
  appRoot.innerHTML = `${header()}<main id="home"><section class="hero"><div class="hero-inner"><div class="hero-copy"><span class="eyebrow">Good things grow together</span><h1>Together, We Can Make a Difference.</h1><p>When a neighbor needs a hand, a community can change everything. Find a cause close to your heart and help move it forward.</p><form class="search-form" id="search-form" role="search"><svg class="search-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><circle cx="10.8" cy="10.8" r="6.8"/><path d="m16 16 5 5"/></svg><label class="sr-only" for="campaign-search">Search fundraisers</label><input id="campaign-search" name="q" placeholder="Try “community garden”" autocomplete="off"><button class="button" type="submit">Search</button></form><div class="hero-chips" aria-label="Browse by category"><span>Popular:</span>${["Medical", "Emergency", "Education", "Environment", "Community"].map(c => `<a href="/fundraisers?category=${encodeURIComponent(c)}">${c}</a>`).join("")}</div><div class="hero-actions"><a class="button" href="/fundraisers">Browse fundraisers</a><a class="button button-ghost" href="/#how-it-works">How it works</a></div><div class="hero-note">Every contribution is handled securely by Paymentwings.</div></div><div class="hero-photo"><img src="https://images.unsplash.com/photo-1531206715517-5c0ba140b2b8?auto=format&fit=crop&w=1200&q=85" alt="Neighbors gather around a shared community project" fetchpriority="high"><div class="photo-caption"><span class="caption-icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 21s-8-4.5-8-11a4.5 4.5 0 0 1 8-2.8A4.5 4.5 0 0 1 20 10c0 6.5-8 11-8 11Z"/><path d="M12 7v8m-3-3h6"/></svg></span><span class="caption-text">A little help goes a long way<small>Real people. Local impact. Shared hope.</small></span></div></div></div></section><section class="proof-strip" aria-label="Kindwell impact"><div class="proof-inner"><div class="proof-item"><strong>${campaigns.length}</strong><span>active fundraisers</span></div></div></section><section class="section" id="fundraisers"><div class="page-wrap"><div class="section-heading"><div><span class="eyebrow">Causes to care about</span><h2>Stories moving forward</h2></div><p>Meet the people and projects making good things happen, one thoughtful gift at a time.</p><a class="button view-more-btn" href="/fundraisers">View more <span aria-hidden="true">→</span></a></div><div class="campaign-grid" id="campaign-grid">${campaigns.slice(0, 6).map(card).join("") || `<div class="empty-state">Fundraisers are having trouble loading. Please refresh to try again.</div>`}</div><div class="start-cta"><h3>Have a cause of your own?</h3><p>Tell us your story and we'll help you get started.</p><a class="button" href="/start/1">Start Kindwell fundraiser</a></div></div></section><section class="section categories-section"><div class="page-wrap"><div class="section-heading"><div><span class="eyebrow">Browse by category</span><h2>Find a cause that moves you</h2></div><a class="button view-more-btn" href="/categories">View more <span aria-hidden="true">→</span></a></div><div class="category-grid">${[["Medical","Help with care, recovery and treatment","M12 21s-8-4.5-8-11a4.5 4.5 0 0 1 8-2.8A4.5 4.5 0 0 1 20 10c0 6.5-8 11-8 11Z"],["Emergency","Support neighbors in urgent need","M12 3 2 21h20L12 3Zm0 6v5m0 3v.5"],["Education","Open doors to learning","M3 9l9-5 9 5-9 5-9-5Zm4 3v5c0 0 2.5 2 5 2s5-1 5-2v-5"],["Environment","Protect and restore our shared places","M5 19c0-8 5-13 14-14 0 9-5 14-13 14m0 0c1-4 4-7 8-9"],["Community","Strengthen the places we live","M3 11l9-7 9 7v9H3v-9Zm6 9v-6h6v6"]].map(([n,d,p]) => `<a class="category-tile" href="/fundraisers?category=${n}"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true"><path d="${p}"/></svg><h3>${n}</h3><p>${d}</p></a>`).join("")}</div></div></section><section class="how-section" id="how-it-works"><div class="page-wrap"><div class="how-heading"><span class="eyebrow">Simple by design</span><h2>Giving should feel good.</h2><p>A few easy steps can help a good thing go a little further.</p></div><div class="steps"><article class="step"><span class="step-number">01</span><h3>Find your reason</h3><p>Explore community-led fundraisers and choose a story that matters to you.</p></article><article class="step"><span class="step-number">02</span><h3>Give with confidence</h3><p>Choose an amount and complete your gift through Paymentwings's secure checkout.</p></article><article class="step"><span class="step-number">03</span><h3>Be part of the good</h3><p>Confirmed gifts are recorded transparently and shared with the campaign community.</p></article></div></div></section><section class="section trust-section"><div class="page-wrap"><div class="section-heading"><div><span class="eyebrow">Giving with confidence</span><h2>Built on transparency</h2></div><p>Plain answers about how your gift is handled.</p></div><div class="trust-grid"><article><h3>Secure payments</h3><p>Card details are entered on Paymentwings's hosted checkout and never touch our servers.</p></article><article><h3>Honest totals</h3><p>Only confirmed payments count. Amounts reported from before Kindwell are labeled separately.</p></article><article><h3>Campaign updates</h3><p>Organizers post updates so you can see how a story moves forward.</p></article></div><div class="faq"><details><summary>Is my donation secure?</summary><p>Yes. Payments are processed by Paymentwings over an encrypted connection.</p></details><details><summary>When does a gift appear on a campaign?</summary><p>After Paymentwings confirms the payment, usually within moments.</p></details><details><summary>Who can I contact for help?</summary><p>Email <a href="mailto:hello@kindwell.care">hello@kindwell.care</a>.</p></details></div></div></section><section class="section why-section"><div class="page-wrap"><div class="section-heading"><div><span class="eyebrow">Why Kindwell</span><h2>Giving made simple and safe</h2></div></div><div class="why-grid"><article><h3>Start a fundraiser</h3><p>Have a cause that needs support? <a href="mailto:hello@kindwell.care?subject=Start%20a%20fundraiser">Tell us your story</a> and our team will help you get started.</p></article><article><h3>Share with your people</h3><p>Every fundraiser has its own page you can share by link, message or social media.</p></article><article><h3>Transparent records</h3><p>Every confirmed gift is recorded in a permanent ledger and reported openly.</p></article><article><h3>Friendly support</h3><p>Questions about a gift or a campaign? Our team is a message away at hello@kindwell.care.</p></article></div></div></section><section class="closing-band" id="about"><div class="closing-inner"><div><h2>Care is something we do together.</h2><p>Kindwell brings thoughtful people closer to causes that matter.</p></div><a class="button" href="/#fundraisers">Explore fundraisers <span aria-hidden="true">→</span></a></div></section></main>${footer()}`;
  const proofItems = document.querySelectorAll(".proof-item");
  document.querySelector(".hero-chips > span").textContent = "Browse:";
  proofItems[0].firstElementChild.textContent = "One act of Kindness Change Lives";
  proofItems[0].lastElementChild.textContent = "1B+ raised over the years";
  document.querySelectorAll(".categories-section .category-tile").forEach(tile => {
    const found = CATEGORY_LIST.find(item => item[0] === tile.querySelector("h3").textContent);
    const svg = tile.querySelector("svg");
    if (found && svg) svg.outerHTML = `<span class="category-emoji" aria-hidden="true">${found[2]}</span>`;
  });
  document.querySelector("#search-form").addEventListener("submit", event => {
    event.preventDefault();
    const query = document.querySelector("#campaign-search").value.trim();
    const grid = document.querySelector("#campaign-grid");
    grid.innerHTML = `<div class="empty-state">Searching fundraisers…</div>`;
    document.querySelector("#fundraisers").scrollIntoView({ behavior: "smooth" });
    const params = new URLSearchParams({ q: query, pageSize: "24", sort: "featured" });
    request(`/api/fundraisers/search?${params}`)
      .then(result => { campaigns = result.items; grid.innerHTML = campaigns.length ? campaigns.map(card).join("") : `<div class="empty-state">No fundraisers matched “${esc(query)}”. Try another search.</div>`; })
      .catch(() => { grid.innerHTML = `<div class="empty-state">Fundraiser search is temporarily unavailable. Please try again.</div>`; });
  });
  setupCardNavigation(document.querySelector("#campaign-grid"), campaigns);
  setupMenu();
}

function setupMenu() {
  const button = document.querySelector(".mobile-menu");
  const nav = document.querySelector(".main-nav");
  button?.addEventListener("click", () => {
    const expanded = button.getAttribute("aria-expanded") === "true";
    button.setAttribute("aria-expanded", String(!expanded));
    nav.classList.toggle("open", !expanded);
  });
  nav?.querySelectorAll("a").forEach(link => link.addEventListener("click", () => { nav.classList.remove("open"); button?.setAttribute("aria-expanded", "false"); }));
}

async function renderDetail(slug) {
  document.title = "Fundraiser | Kindwell";
  let data;
  try { data = await request(`/api/fundraisers/${encodeURIComponent(slug)}`); }
  catch { appRoot.innerHTML = `${header()}<main class="page-wrap page-shell"><div class="auth-panel"><span class="auth-icon">!</span><h1>We couldn't find that fundraiser.</h1><p>It may have ended or the link may have changed.</p><a class="button" href="/#fundraisers">Browse fundraisers</a></div></main>${footer()}`; setupMenu(); return; }
  const campaign = data.campaign;
  const pct = progress(campaign);
  const organizerMarkup = organizerProfileLink(campaign);
  const updatesMarkup = `<section class="campaign-updates"><h2>Updates from this fundraiser</h2>${data.updates.length ? data.updates.map(update => `<article class="campaign-update"><time datetime="${esc(update.date)}">${new Date(update.date).toLocaleDateString()}</time><h3>${esc(update.title)}</h3><p>${esc(update.body)}</p></article>`).join("") : `<p class="donor-count">The organizer hasn't shared any updates yet.</p>`}</section>`;
  appRoot.innerHTML = `${header()}<main class="page-wrap page-shell"><p class="breadcrumbs"><a href="/">Home</a> / <a href="/#fundraisers">Fundraisers</a> / ${esc(campaign.title)}</p><div class="detail-layout"><article class="detail-main"><h1>${esc(campaign.title)}</h1>${organizerMarkup || ""}<img class="detail-cover" src="${esc(campaign.image)}" alt="Image for ${esc(campaign.title)}"><h2>Why this matters</h2><p class="detail-story">${esc(campaign.story)}</p><div class="share-row"><button id="share-button" type="button">Share fundraiser</button><button id="copy-button" type="button">Copy link</button></div></article><aside class="detail-sidebar"><div class="raised-line"><span><strong>${money(campaign.raised)}</strong> raised</span><span>of ${money(campaign.goal)}</span></div><div class="progress-track" role="progressbar" aria-valuenow="${pct}" aria-valuemin="0" aria-valuemax="100" aria-label="${pct} percent funded"><div class="progress-fill" style="width:${pct}%"></div></div><div class="detail-stat">${campaign.donations} confirmed gifts · ${pct}% of goal</div><button class="button" id="open-donate">Donate</button><h3>Recent gifts</h3>${data.recentDonations.length ? data.recentDonations.map(donation => `<div class="donation-row"><span>${esc(donation.name)}</span><strong>${money(donation.amount)}</strong></div>`).join("") : `<p class="donor-count">No gifts have been confirmed through Kindwell yet.</p>`}<p class="trust-note">Some campaign totals may include support received before Kindwell; the donation count and recent gifts here show only donations confirmed through this site. Gifts are processed securely by our payment partner. Kindwell does not store card details. Our payment partner deducts processing fees from the payment; the exact fee depends on country and payment method and is shown at checkout. Refund requests may be submitted to <a href="mailto:hello@kindwell.care">hello@kindwell.care</a> within 30 days and are reviewed individually; our payment partner's processing fees may not be refundable.</p></aside></div></main>${footer()}<div id="modal-root"></div>`;
  appRoot.innerHTML = `${header()}<main class="page-wrap page-shell"><p class="breadcrumbs"><a href="/">Home</a> / <a href="/#fundraisers">Fundraisers</a> / ${esc(campaign.title)}</p><div class="detail-layout"><article class="detail-main"><h1>${esc(campaign.title)}</h1>${organizerMarkup || ""}<img class="detail-cover" src="${esc(campaign.image)}" alt="Image for ${esc(campaign.title)}"><h2>Why this matters</h2><p class="detail-story">${esc(campaign.story)}</p><div class="share-row"><button id="share-button" type="button">Share fundraiser</button><button id="copy-button" type="button">Copy link</button></div></article><aside class="detail-sidebar"><div class="raised-line"><span><strong>${money(campaign.raised)}</strong> raised</span><span>of ${money(campaign.goal)}</span></div><div class="progress-track" role="progressbar" aria-valuenow="${pct}" aria-valuemin="0" aria-valuemax="100" aria-label="${pct} percent funded"><div class="progress-fill" style="width:${pct}%"></div></div><div class="detail-stat">${campaign.donations} gifts confirmed through Kindwell${campaign.reportedDonations ? ` · ${campaign.reportedDonations} reported before Kindwell` : ""} · ${pct}% of goal</div><button class="button" id="open-donate">Donate</button><h3>Recent gifts</h3>${data.recentDonations.length ? data.recentDonations.map(donation => `<div class="donation-row"><span>${esc(donation.name)}</span><strong>${money(donation.amount)}</strong></div>`).join("") : `<p class="donor-count">No gifts have been confirmed through Kindwell yet.</p>`}<p class="trust-note">Your gift is processed over an encrypted connection, and Kindwell never stores your card details. A small processing fee is taken from each gift and shown before you pay. Questions about a donation? Contact us at <a href="mailto:hello@kindwell.care">hello@kindwell.care</a>.</p></aside></div></main>${footer()}<div id="modal-root"></div>`;
  setupMenu();
  document.querySelector(".detail-main").insertAdjacentHTML("beforeend", updatesMarkup);
  document.querySelector("#open-donate").addEventListener("click", () => openDonation(campaign));
  document.querySelector("#share-button").addEventListener("click", async () => {
    if (navigator.share) await navigator.share({ title: campaign.title, url: location.href });
    else await navigator.clipboard.writeText(location.href);
  });
  document.querySelector("#copy-button").addEventListener("click", async event => { await navigator.clipboard.writeText(location.href); event.currentTarget.textContent = "Link copied"; });
}

function openDonation(campaign) {
  const root = document.querySelector("#modal-root");
  document.body.style.overflow = "hidden";
  root.innerHTML = `<div class="modal-backdrop" role="presentation"><section class="auth-wrap" role="dialog" aria-modal="true" aria-labelledby="donate-title"><div class="auth-panel"><button type="button" class="modal-close" aria-label="Close donation window">×</button><h1 id="donate-title">Donate</h1><p>Your gift will support “${esc(campaign.title)}”.</p><form id="donation-form"><label for="donor-currency">Currency</label><select id="donor-currency"><option value="USD">USD</option></select><div class="payment-options" id="amount-options"></div><label class="sr-only" for="custom-amount">Custom gift amount</label><div class="donate-custom"><span id="currency-symbol">$</span><input id="custom-amount" type="number" min="1" max="100000000" step="0.01" placeholder="Enter another amount" inputmode="decimal"></div><p class="secure-copy" id="usd-estimate" aria-live="polite"></p><label for="donor-name">Name (optional)</label><input id="donor-name" maxlength="80" placeholder="Your name"><label for="donor-email">Email (for your payment receipt)</label><input id="donor-email" type="email" maxlength="254" required autocomplete="email" placeholder="you@example.com"><div class="form-checks"><label><input id="anonymous" type="checkbox"> Give anonymously</label></div><button class="button" type="submit">Donate securely</button><p class="secure-copy">Kindwell is your payment assistant. In the next step, you will enter card details securely on our payment partner's own page. Kindwell stores your donation details and receipt email, but never stores card number, CVV, PIN, or OTP.</p><p class="notice" id="donate-error" role="alert"></p></form></div></section></div>`;
  const closeDialog = () => {
    root.innerHTML = "";
    document.body.style.overflow = "";
    document.removeEventListener("keydown", handleEscape);
  };
  const handleEscape = event => { if (event.key === "Escape") closeDialog(); };
  document.addEventListener("keydown", handleEscape);
  const currencySelect = root.querySelector("#donor-currency");
  const amountOptions = root.querySelector("#amount-options");
  const estimate = root.querySelector("#usd-estimate");
  const customInput = root.querySelector("#custom-amount");
  let rates = null;
  const unitsPerUsd = code => code === "USD" ? 1 : Number(rates?.currencies.find(item => item.code === code)?.unitsPerUsd);
  const currentMinor = () => { const custom = Number(customInput.value); return custom > 0 ? Math.round(custom * 100) : Number(amountOptions.querySelector(".selected")?.dataset.amount || 0); };
  const updateEstimate = () => { estimate.textContent = ""; };
  const renderPresets = () => {
    const code = currencySelect.value;
    const rate = unitsPerUsd(code);
    const presets = DONATION_PRESETS[code] || [25, 50, 100, 250].map(value => Math.max(1, Math.round(value * rate)));
    amountOptions.innerHTML = presets.map((value, index) => `<button class="amount-choice${index === 0 ? " selected" : ""}" type="button" data-amount="${value * 100}">${esc(formatCurrency(value * 100, code))}</button>`).join("");
    amountOptions.querySelectorAll(".amount-choice").forEach(button => button.addEventListener("click", () => { amountOptions.querySelectorAll(".amount-choice").forEach(item => item.classList.remove("selected")); button.classList.add("selected"); customInput.value = ""; updateEstimate(); }));
    root.querySelector("#currency-symbol").textContent = new Intl.NumberFormat(undefined, { style: "currency", currency: code }).formatToParts(0).find(part => part.type === "currency")?.value || code;
    customInput.value = "";
    updateEstimate();
  };
  currencySelect.addEventListener("change", renderPresets);
  customInput.addEventListener("input", () => { if (customInput.value) amountOptions.querySelectorAll(".amount-choice").forEach(item => item.classList.remove("selected")); updateEstimate(); });
  renderPresets();
  request("/api/rates").then(data => {
    rates = data;
    currencySelect.innerHTML = data.currencies.map(item => `<option value="${esc(item.code)}">${esc(item.code)}</option>`).join("");
    currencySelect.value = data.suggestedCurrency;
    renderPresets();
  }).catch(() => { rates = null; });
  root.querySelector(".modal-close").addEventListener("click", closeDialog);
  root.querySelector(".modal-backdrop").addEventListener("click", event => { if (event.target.classList.contains("modal-backdrop")) closeDialog(); });
  const cardCheckout = async (amount, button, error) => {
    button.disabled = true; button.textContent = "Opening secure payment…"; error.textContent = "";
    try {
      const result = await request("/api/donations/checkout", { method: "POST", body: JSON.stringify({ slug: campaign.slug, amount, currency: currencySelect.value, name: root.querySelector("#donor-name").value, email: root.querySelector("#donor-email").value, anonymous: root.querySelector("#anonymous").checked }) });
      // Returning with the back button restores this page from cache; reset the button so the donor can retry.
      window.addEventListener("pageshow", () => { button.disabled = false; button.textContent = "Donate securely"; }, { once: true });
      startHostedPaymentAssistant(result, campaign, closeDialog);
    } catch (issue) { error.textContent = issue.message; button.disabled = false; button.textContent = "Donate securely"; }
  };
  root.querySelector("#donation-form").addEventListener("submit", event => {
    event.preventDefault();
    const amount = currentMinor();
    const form = root.querySelector("#donation-form");
    const error = root.querySelector("#donate-error");
    if (!(amount > 0)) { error.textContent = "Please choose or enter a gift amount."; return; }
    error.textContent = "";
    const methods = [["paypal", "PayPal"], ["cashapp", "Cash App"], ["venmo", "Venmo"], ["bank", "Bank transfer"], ["card", "Card"]];
    root.querySelector("#method-picker")?.remove();
    form.hidden = true;
    form.insertAdjacentHTML("afterend", `<div id="method-picker"><h2>How would you like to donate?</h2><p>${esc(formatCurrency(amount, currencySelect.value))} to “${esc(campaign.title)}”</p><div class="method-list">${methods.map(([key, label]) => `<button class="button button-light method-choice" type="button" data-method="${key}">${PAYMENT_ICONS[key]}<span>${label}</span></button>`).join("")}</div><button class="button button-light" type="button" id="method-back">Back</button></div>`);
    const picker = root.querySelector("#method-picker");
    picker.querySelector("#method-back").addEventListener("click", () => { picker.remove(); form.hidden = false; });
    picker.querySelectorAll("[data-method]").forEach(choice => choice.addEventListener("click", () => {
      const method = choice.dataset.method;
      if (method === "card") { picker.remove(); form.hidden = false; cardCheckout(amount, form.querySelector(".button"), error); return; }
      const params = new URLSearchParams({ amount: String(amount), currency: currencySelect.value, slug: campaign.slug, title: campaign.title });
      document.body.style.overflow = "";
      location.href = `/pay/${method}?${params}`;
    }));
  });
}

function startHostedPaymentAssistant(order, campaign, closeDialog) {
  const root = document.querySelector("#modal-root");
  const amountText = formatCurrency(Math.round(order.amount * 100), order.currency);
  const title = order.campaign || campaign.title;
  const panel = body => { root.innerHTML = `<div class="modal-backdrop" role="presentation"><section class="auth-wrap" role="dialog" aria-modal="true" aria-label="Kindwell payment"><div class="auth-panel"><div>${brand()}</div>${body}</div></section></div>`; };
  const again = () => { stop(); closeDialog(); openDonation(campaign); };
  let timer = null, popup = null, polling = false;
  const stop = () => { clearInterval(timer); timer = null; };
  const done = (mark, heading, text, withMore) => {
    stop();
    panel(`<span class="success-mark">${mark}</span><h1>${esc(heading)}</h1><p>${esc(text)}</p><div class="result-actions">${withMore ? `<button class="button" id="more" type="button">Donate more</button>` : `<button class="button" id="more" type="button">Try again</button>`}<button class="button button-light" id="close" type="button">Close</button></div>`);
    root.querySelector("#more").addEventListener("click", again);
    root.querySelector("#close").addEventListener("click", () => { stop(); closeDialog(); });
  };
  const waiting = (note = "") => {
    const providerName = esc(order.provider || "payment partner");
    const brands = (order.brands && order.brands.length ? order.brands : ["Visa", "Mastercard"]).map(brand => `<span>${esc(brand)}</span>`).join("");
    panel(`<h1>Complete your payment</h1><p><strong>${esc(amountText)}</strong> to “${esc(title)}”</p><p class="secure-copy">Enter your card details in the secure ${providerName} window. This page updates by itself the moment ${providerName} confirms your payment. Kindwell never sees your card number, CVV, PIN or one-time code.</p><div class="card-helper"><div class="card-helper-head" id="wait-status">Waiting for payment confirmation…</div><div class="card-badges">${brands}</div><span class="lock-badge"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="4" y="11" width="16" height="9" rx="2"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>PCI-compliant checkout hosted by ${providerName}</span></div><button class="button" id="reopen" type="button">Open secure payment window</button><button class="button button-light" id="cancel" type="button">Cancel</button><p class="notice" id="assistant-error" role="alert">${esc(note)}</p>`);
    root.querySelector("#cancel").addEventListener("click", () => { stop(); closeDialog(); });
    root.querySelector("#reopen").addEventListener("click", open);
  };
  const check = async () => {
    if (polling) return;
    polling = true;
    try {
      const status = await request(`/api/donations/status/${encodeURIComponent(order.txRef)}`);
      if (status.confirmed) { try { popup?.close(); } catch { } done("✓", "Thank you for your donation!", `Your ${formatCurrency(status.originalAmount, status.currency)} gift to ${status.campaign} is confirmed.`, true); }
      else if (status.failed) { try { popup?.close(); } catch { } done("×", "Payment failed", `${order.provider || "Our payment partner"} could not complete this payment. No gift was recorded.`, false); }
    } catch { /* keep waiting; the next poll retries */ }
    polling = false;
  };
  const open = () => {
    popup = window.open(order.url, "kindwell-paymentwings", "width=480,height=760");
    if (!popup) { const note = root.querySelector("#assistant-error"); if (note) note.innerHTML = `Your browser blocked the window. <a href="${esc(order.url)}" target="_blank" rel="noopener">Open secure payment page</a>`; }
    if (!timer) timer = setInterval(check, 3000);
  };
  waiting();
  open();
}

async function renderPeople() {
  const query = new URLSearchParams(location.search);
  const search = query.get("q") || "";
  document.title = "People | Kindwell";
  appRoot.innerHTML = `${header()}<main class="page-wrap page-shell"><section class="section"><div class="section-heading"><div><span class="eyebrow">Community organizers</span><h1>People behind the fundraising</h1></div><p>Search by organizer name or location to find community members and their fundraisers.</p></div><form class="discover-filters" id="person-filters" role="search"><label>Search<input name="q" type="search" maxlength="120" placeholder="Search by name or city" value="${esc(search)}"></label><button class="button" type="submit">Find people</button></form><div id="people-grid" class="campaign-grid"></div></section></main>${footer()}`;
  setupMenu();
  const grid = document.querySelector("#people-grid");
  const form = document.querySelector("#person-filters");
  const load = async () => {
    const params = new URLSearchParams({ q: form.querySelector("input").value.trim() });
    try {
      const result = await request(`/api/people/search?${params}`);
      const items = result.items || [];
      grid.innerHTML = items.length ? items.map(person => `<article class="campaign-card"><div class="card-body"><div class="organizer-line"><a class="organizer-badge" href="/people/${encodeURIComponent(slugify(person.name || ""))}"><img src="${esc(person.profileImage || "https://images.unsplash.com/photo-1524504388940-b1c1722653e1?auto=format&fit=crop&w=900&q=80")}" alt="${esc(person.name)}"><span><strong>${esc(person.name)}</strong><small>${esc(person.location || "Location not shared")}</small></span></a></div><p class="card-summary">${esc(person.bio || "This organizer has not added a bio yet.")}</p><a class="button" href="/people/${encodeURIComponent(slugify(person.name || ""))}">View profile</a></div></article>`).join("") : `<div class="empty-state">No organizer profiles matched that search.</div>`;
      const nextUrl = new URL(location.href);
      if (form.querySelector("input").value) nextUrl.searchParams.set("q", form.querySelector("input").value.trim()); else nextUrl.searchParams.delete("q");
      history.replaceState(null, "", nextUrl);
    } catch {
      grid.innerHTML = `<div class="empty-state">We couldn't load organizer profiles right now.</div>`;
    }
  };
  form.addEventListener("submit", event => { event.preventDefault(); void load(); });
  await load();
}

async function renderPerson(slug) {
  document.title = "Organizer profile | Kindwell";
  let data;
  try { data = await request(`/api/people/${encodeURIComponent(slug)}`); }
  catch {
    appRoot.innerHTML = `${header()}<main class="page-wrap page-shell"><div class="auth-panel"><span class="auth-icon">!</span><h1>We couldn't find that profile.</h1><p>This organizer may not have a public fundraiser page yet.</p><a class="button" href="/fundraisers">Browse fundraisers</a></div></main>${footer()}`;
    setupMenu();
    return;
  }
  const profile = data.profile;
  const fundraisers = data.fundraisers || [];
  appRoot.innerHTML = `${header()}<main class="page-wrap page-shell"><p class="breadcrumbs"><a href="/">Home</a> / <a href="/people">People</a> / ${esc(profile.name)}</p><section class="profile-header"><div class="profile-photo-wrap"><img class="profile-photo" src="${esc(profile.profileImage || "https://images.unsplash.com/photo-1524504388940-b1c1722653e1?auto=format&fit=crop&w=900&q=80")}" alt="${esc(profile.name)}"></div><div class="profile-copy"><span class="eyebrow">Organizer</span><h1>${esc(profile.name)}</h1><p class="profile-location">${esc(profile.location || "Location not shared")}</p>${profile.bio ? `<p class="detail-story">${esc(profile.bio)}</p>` : ""}<a class="button" href="/fundraisers?q=${encodeURIComponent(profile.name)}">View matching fundraisers</a></div></section><section class="section"><div class="section-heading"><div><span class="eyebrow">Fundraisers by this organizer</span><h2>${esc(profile.name)}'s projects</h2></div></div><div class="campaign-grid">${fundraisers.length ? fundraisers.map(card).join("") : `<div class="empty-state">This organizer has not posted a fundraiser yet.</div>`}</div></section></main>${footer()}`;
  setupCardNavigation(document.querySelector(".campaign-grid"), fundraisers);
  setupMenu();
}

async function renderPublicLogin() {
  document.title = "Log in | Kindwell";
  const errors = { not_configured: "That sign-in option isn't set up yet. Please try another one.", failed: "We couldn't sign you in. Please try again.", cancelled: "Sign-in was cancelled." };
  const error = new URLSearchParams(location.search).get("error");
  let me = { user: null, providers: {} };
  try { me = await request("/api/auth/me"); } catch { /* show the signed-out page */ }
  const icons = {
    google: `<svg width="20" height="20" viewBox="0 0 48 48" aria-hidden="true"><path fill="#EA4335" d="M24 9.5c3.5 0 6.6 1.2 9.1 3.6l6.8-6.8C35.8 2.4 30.3 0 24 0 14.6 0 6.5 5.4 2.6 13.2l7.9 6.1C12.4 13.6 17.7 9.5 24 9.5z"/><path fill="#4285F4" d="M46.5 24.5c0-1.6-.1-3.1-.4-4.5H24v9h12.7c-.6 3-2.3 5.5-4.8 7.2l7.5 5.8c4.4-4.1 7.1-10.1 7.1-17.5z"/><path fill="#FBBC05" d="M10.5 28.7a14.5 14.5 0 0 1 0-9.4l-7.9-6.1a24 24 0 0 0 0 21.6l7.9-6.1z"/><path fill="#34A853" d="M24 48c6.5 0 11.9-2.1 15.9-5.8l-7.5-5.8c-2.1 1.4-4.9 2.3-8.4 2.3-6.3 0-11.6-4.1-13.5-9.8l-7.9 6.1C6.500 42.600 14.600 48 24 48z"/></svg>`,
    apple: `<svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M16.4 12.6c0-2.500 2-3.700 2.100-3.800-1.200-1.700-3-1.900-3.600-2-1.500-.2-3 .9-3.800.9-.8 0-2-.9-3.300-.9-1.700 0-3.300 1-4.200 2.500-1.800 3.100-.5 7.700 1.300 10.200.9 1.200 1.900 2.600 3.200 2.500 1.300-.1 1.800-.8 3.300-.8s2 .8 3.300.8c1.400 0 2.300-1.200 3.100-2.400 1-1.400 1.400-2.800 1.400-2.900-.1 0-2.800-1.100-2.800-4.100zM14 5.200c.7-.9 1.200-2 1.100-3.200-1 0-2.300.7-3 1.600-.7.800-1.300 2-1.100 3.100 1.200.1 2.300-.600 3-1.500z"/></svg>`,
    facebook: `<svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true"><path fill="#1877F2" d="M24 12a12 12 0 1 0-13.900 11.900v-8.400h-3V12h3V9.400c0-3 1.800-4.700 4.500-4.700 1.300 0 2.700.2 2.700.2v3h-1.500c-1.500 0-2 .9-2 1.900V12h3.400l-.5 3.500h-2.800v8.400A12 12 0 0 0 24 12z"/></svg>`
  };
  const names = { google: "Google", apple: "Apple", facebook: "Facebook" };
  const body = me.user
    ? `<h1>Welcome, ${esc(me.user.name)}</h1><p>You're signed in with ${esc(names[me.user.provider] || "your account")}.</p><a class="button" href="/start/1">Start Kindwell fundraiser</a><button class="button button-light" id="logout" type="button" style="margin-top:10px">Log out</button>`
    : `<h1>Log in or sign up</h1><p>Welcome to Kindwell. Log in to continue.</p><form id="email-login" class="admin-form"><label for="login-email">Email</label><input id="login-email" type="email" autocomplete="email" required><label for="login-password">Password</label><input id="login-password" type="password" autocomplete="current-password" required><button class="button" type="submit">Log in</button><p style="text-align:right;margin:8px 0 0"><a href="/forgot-password">Forgot password?</a></p></form><div class="auth-divider"><span>or</span></div><div class="social-login">${["google", "apple", "facebook"].map(p => `<a class="social-button social-${p}" href="/auth/${p}" data-provider="${p}">${icons[p]}<span>Continue with ${names[p]}</span></a>`).join("")}</div><p class="notice" id="login-message" role="alert">${esc(errors[error] || "")}</p><p style="margin:14px 0 0">New to Kindwell? <a href="/signup"><strong>Sign up</strong></a></p><p class="secure-copy">By continuing you agree to our <a href="/terms">Terms</a> and <a href="/privacy">Privacy Policy</a>. Kindwell never sees or stores your social account password.</p>`;
  appRoot.innerHTML = `${header()}<main class="auth-wrap"><section class="auth-panel"><div>${brand()}</div>${body}</section></main>${footer()}`;
  document.querySelector("#email-login")?.addEventListener("submit", event => submitAuth(event, "/api/auth/login", () => ({ email: document.querySelector("#login-email").value, password: document.querySelector("#login-password").value }), () => location.assign("/login"), "#login-message"));
  document.querySelector("#logout")?.addEventListener("click", async () => { await request("/api/auth/logout", { method: "POST" }); location.assign("/login"); });
  setupMenu();
}

async function submitAuth(event, url, getBody, onSuccess, noticeSelector) {
  event.preventDefault();
  const form = event.currentTarget;
  const button = form.querySelector("button[type=submit]");
  const notice = document.querySelector(noticeSelector);
  const label = button.textContent;
  button.disabled = true; button.textContent = "Please wait…"; notice.textContent = "";
  try { onSuccess(await request(url, { method: "POST", body: JSON.stringify(getBody()) })); }
  catch (issue) { notice.textContent = issue.message; button.disabled = false; button.textContent = label; }
}

function authPage(title, intro, form) {
  appRoot.innerHTML = `${header()}<main class="auth-wrap"><section class="auth-panel"><div>${brand()}</div><h1>${esc(title)}</h1><p>${intro}</p>${form}</section></main>${footer()}`;
  document.title = `${title} | Kindwell`;
  setupMenu();
}

async function renderSignup() {
  authPage("Create your account", "Join Kindwell to start a fundraiser and keep track of your giving.", `<form id="signup-form" class="admin-form"><label for="su-name">Name</label><input id="su-name" maxlength="80" autocomplete="name" required><label for="su-email">Email</label><input id="su-email" type="email" maxlength="254" autocomplete="email" required><label for="su-password">Password</label><input id="su-password" type="password" minlength="8" maxlength="128" autocomplete="new-password" required placeholder="At least 8 characters"><label for="su-confirm">Confirm password</label><input id="su-confirm" type="password" minlength="8" maxlength="128" autocomplete="new-password" required><button class="button" type="submit">Sign up</button><p class="notice" id="su-message" role="alert"></p></form><p style="margin:14px 0 0">Already have an account? <a href="/login"><strong>Log in</strong></a></p><p class="secure-copy">By signing up you agree to our <a href="/terms">Terms</a> and <a href="/privacy">Privacy Policy</a>.</p>`);
  document.querySelector("#signup-form").addEventListener("submit", event => {
    if (document.querySelector("#su-password").value !== document.querySelector("#su-confirm").value) { event.preventDefault(); document.querySelector("#su-message").textContent = "The passwords don't match."; return; }
    submitAuth(event, "/api/auth/signup", () => ({ name: document.querySelector("#su-name").value, email: document.querySelector("#su-email").value, password: document.querySelector("#su-password").value }), () => location.assign("/login"), "#su-message");
  });
}

async function renderForgotPassword() {
  authPage("Forgot your password?", "Enter your email and we'll send you a link to choose a new one.", `<form id="forgot-form" class="admin-form"><label for="fp-email">Email</label><input id="fp-email" type="email" maxlength="254" autocomplete="email" required placeholder="you@example.com"><button class="button" type="submit">Send reset link</button><p class="notice" id="fp-message" role="alert"></p></form><p style="margin:14px 0 0"><a href="/login">Back to log in</a></p>`);
  document.querySelector("#forgot-form").addEventListener("submit", event => submitAuth(event, "/api/auth/forgot", () => ({ email: document.querySelector("#fp-email").value }), () => {
    document.querySelector("#forgot-form").innerHTML = `<span class="success-mark">✓</span><p>If an account exists for that email, a reset link is on its way. It works for 1 hour. Check your spam folder if you don't see it.</p><a class="button" href="/login">Back to log in</a>`;
  }, "#fp-message"));
}

async function renderResetPassword() {
  const token = new URLSearchParams(location.search).get("token") || "";
  if (!token) { authPage("Reset link needed", "This page needs a valid reset link from your email.", `<a class="button" href="/forgot-password">Request a new link</a>`); return; }
  authPage("Choose a new password", "Pick a password with at least 8 characters.", `<form id="reset-form" class="admin-form"><label for="rp-password">New password</label><input id="rp-password" type="password" minlength="8" maxlength="128" autocomplete="new-password" required><label for="rp-confirm">Confirm new password</label><input id="rp-confirm" type="password" minlength="8" maxlength="128" autocomplete="new-password" required><button class="button" type="submit">Update password</button><p class="notice" id="rp-message" role="alert"></p></form>`);
  document.querySelector("#reset-form").addEventListener("submit", event => {
    if (document.querySelector("#rp-password").value !== document.querySelector("#rp-confirm").value) { event.preventDefault(); document.querySelector("#rp-message").textContent = "The passwords don't match."; return; }
    submitAuth(event, "/api/auth/reset", () => ({ token, password: document.querySelector("#rp-password").value }), () => {
      document.querySelector("#reset-form").innerHTML = `<span class="success-mark">✓</span><p>Your password has been updated. You can log in with it now.</p><a class="button" href="/login">Log in</a>`;
    }, "#rp-message");
  });
}

const CATEGORY_LIST = [
  ["All categories", "", "🌍", "Every fundraiser on Kindwell"],
  ["Medical", "Medical", "🏥", "Care, recovery and treatment"],
  ["Emergency", "Emergency", "🚑", "Neighbors in urgent need"],
  ["Education", "Education", "📚", "Open doors to learning"],
  ["Community", "Community", "🏘️", "Strengthen the places we live"],
  ["Animals", "Animals", "🐾", "Care for animals in need"],
  ["Memorial", "Memorial", "🕊️", "Honor loved ones"],
  ["Environment", "Environment", "🌱", "Protect our shared places"],
  ["Other", "Other", "💛", "Causes that don't fit elsewhere"]
];

async function renderCategories() {
  let featured = [];
  try { featured = (await request("/api/fundraisers/search?sort=featured&pageSize=12")).items; } catch { featured = []; }
  campaigns = featured;
  document.title = "Categories | Kindwell";
  appRoot.innerHTML = `${header()}<main><section class="section"><div class="page-wrap"><div class="section-heading"><div><span class="eyebrow">Browse by category</span><h2>All categories</h2></div><p>Pick a cause you care about to see its fundraisers.</p></div><div class="category-grid category-page-grid">${CATEGORY_LIST.map(([name, value, icon, desc]) => `<a class="category-tile" href="/fundraisers${value ? `?category=${encodeURIComponent(value)}` : ""}"><span class="category-emoji" aria-hidden="true">${icon}</span><h3>${name}</h3><p>${desc}</p></a>`).join("")}</div></div></section><section class="section"><div class="page-wrap"><div class="section-heading"><div><span class="eyebrow">Featured</span><h2>Featured stories</h2></div></div><div class="campaign-grid" id="campaign-grid">${featured.map(card).join("") || `<div class="empty-state">No featured fundraisers yet.</div>`}</div></div></section><section class="section"><div class="page-wrap"><div class="section-heading"><div><span class="eyebrow">Search</span><h2>Search stories</h2></div></div><form class="discover-filters" action="/fundraisers" method="get" role="search"><label>Search<input name="q" type="search" maxlength="120" placeholder="Title, story, or category"></label><button class="button" type="submit">Search stories</button></form></div></section></main>${footer()}`;
  setupMenu();
}

async function renderOngoing() {
  const query = new URLSearchParams(location.search);
  const categories = ["Medical", "Emergency", "Education", "Community", "Animals", "Memorial", "Environment", "Other"];
  const initialSearch = query.get("q") || "";
  const initialCategory = categories.includes(query.get("category")) ? query.get("category") : "";
  const initialSort = ["recent", "most-donated", "closest-to-goal", "highest-raised"].includes(query.get("sort")) ? query.get("sort") : "featured";
  document.title = "Ongoing fundraisers | Kindwell";
  appRoot.innerHTML = `${header()}<main><section class="section"><div class="page-wrap"><div class="section-heading"><div><span class="eyebrow">Happening now</span><h2>Ongoing fundraisers</h2></div><p>Every fundraiser we have posted that is currently open for gifts.</p></div><form class="discover-filters" id="fundraiser-filters" role="search"><label>Search<input name="q" type="search" maxlength="120" placeholder="Title, story, or category" value="${esc(initialSearch)}"></label><label>Category<select name="category"><option value="">All categories</option>${categories.map(category => `<option value="${category}"${initialCategory === category ? " selected" : ""}>${category}</option>`).join("")}</select></label><label>Sort by<select name="sort"><option value="featured"${initialSort === "featured" ? " selected" : ""}>Featured</option><option value="recent"${initialSort === "recent" ? " selected" : ""}>Most recent</option><option value="most-donated"${initialSort === "most-donated" ? " selected" : ""}>Most gifts</option><option value="closest-to-goal"${initialSort === "closest-to-goal" ? " selected" : ""}>Closest to goal</option><option value="highest-raised"${initialSort === "highest-raised" ? " selected" : ""}>Most raised</option></select></label><button class="button" type="submit">Find fundraisers</button></form><p class="discover-status" id="discover-status" role="status" aria-live="polite"></p><div class="campaign-grid" id="campaign-grid"></div><nav class="pagination" id="fundraiser-pagination" aria-label="Fundraiser pages"></nav></div></section></main>${footer()}`;
  const form = document.querySelector("#fundraiser-filters");
  const grid = document.querySelector("#campaign-grid");
  const status = document.querySelector("#discover-status");
  const pagination = document.querySelector("#fundraiser-pagination");
  let currentPage = Math.max(1, Number(query.get("page")) || 1);
  const loadPage = async page => {
    currentPage = page;
    status.textContent = "Loading fundraisers…";
    pagination.innerHTML = "";
    grid.innerHTML = "";
    const params = new URLSearchParams(new FormData(form));
    params.set("pageSize", "24");
    try {
      // Load every page of results so all fundraisers show on one page.
      let result;
      const items = [];
      let next = 1;
      do {
        params.set("page", String(next));
        result = await request(`/api/fundraisers/search?${params}`);
        items.push(...result.items);
        next += 1;
      } while (next <= result.pages);
      campaigns = items;
      grid.innerHTML = campaigns.length ? campaigns.map(card).join("") : `<div class="empty-state">No ongoing fundraisers matched those filters. Try a different search.</div>`;
      status.textContent = `Showing all ${result.total} ongoing fundraisers`;
      const nextUrl = new URL(location.href);
      nextUrl.searchParams.delete("page");
      for (const key of ["q", "category", "sort"]) {
        const value = params.get(key);
        if (value) nextUrl.searchParams.set(key, value);
        else nextUrl.searchParams.delete(key);
      }
      history.replaceState(null, "", nextUrl);
    } catch {
      status.textContent = "Fundraisers are having trouble loading. Please refresh to try again.";
      grid.innerHTML = "";
    }
  };
  form.addEventListener("submit", event => { event.preventDefault(); loadPage(1); });
  form.querySelectorAll("select").forEach(select => select.addEventListener("change", () => loadPage(1)));
  await loadPage(currentPage);
  setupMenu();
}

// Fill these in with the real receiving details before going live.
const BTC_PAYMENT = {
  address: "bc1qyqu0f5ce28s6pa4357r0xc4jz40wt09zsfuety",
  bank: { name: "YOUR_BANK_NAME", account: "YOUR_ACCOUNT_NAME", number: "YOUR_ACCOUNT_NUMBER" }
};
const PAY_METHODS = {
  paypal: { label: "PayPal", steps: ["Open the PayPal app or log in at paypal.com.", "Tap <strong>Crypto</strong> and choose <strong>Bitcoin (BTC)</strong>. Buy BTC if you don't have any yet.", "Tap <strong>Send</strong> / <strong>Transfer</strong> and select <strong>Send to external wallet</strong>.", "Paste the Kindwell BTC wallet address shown below.", "Enter the equivalent of your gift amount and confirm the transfer."] },
  cashapp: { label: "Cash App", steps: ["Open Cash App and tap the <strong>Bitcoin</strong> tab (₿).", "Buy BTC if your balance is not enough.", "Tap <strong>Send</strong> / <strong>Withdraw</strong> and choose <strong>Send Bitcoin</strong>.", "Paste the Kindwell BTC wallet address shown below.", "Enter the equivalent of your gift amount, review, and confirm."] },
  venmo: { label: "Venmo", steps: ["Open the Venmo app and tap the <strong>Crypto</strong> tab.", "Choose <strong>Bitcoin</strong> and buy BTC if you don't have any yet.", "Tap <strong>Transfer</strong> and select <strong>Send to external wallet</strong>.", "Paste the Kindwell BTC wallet address shown below.", "Enter the equivalent of your gift amount and confirm."] },
  bank: { label: "Bank transfer", steps: ["Log in to your banking app or crypto exchange.", "Buy BTC with your bank transfer if you don't have any yet.", "Choose <strong>Withdraw / Send BTC</strong> to an external wallet.", "Paste the Kindwell BTC wallet address shown below.", "Enter the equivalent of your gift amount and confirm."] }
};

function renderPay(method) {
  const info = PAY_METHODS[method];
  if (!info) return renderHome();
  const params = new URLSearchParams(location.search);
  const slug = params.get("slug") || "";
  const title = params.get("title") || "this fundraiser";
  const amount = Number(params.get("amount"));
  const currency = /^[A-Z]{3}$/.test(params.get("currency") || "") ? params.get("currency") : "USD";
  const amountText = amount > 0 ? formatCurrency(amount, currency) : "your gift";
  document.title = `Pay with ${info.label} | Kindwell`;
  const showAddress = esc(BTC_PAYMENT.address);
  appRoot.innerHTML = `${header()}<main class="auth-wrap"><section class="auth-panel" id="pay-panel"><div>${brand()}</div><h1>Pay with ${esc(info.label)}</h1><p>Send <strong>${esc(amountText)}</strong> in Bitcoin (BTC) to support “${esc(title)}”.</p><ol class="pay-steps">${info.steps.map(step => `<li>${step}</li>`).join("")}</ol><label>Kindwell BTC wallet address</label><input id="btc-address" readonly value="${showAddress}"><button class="button button-light" id="copy-address" type="button">Copy address</button><p class="secure-copy">Only send Bitcoin (BTC) to this address. Transfers can't be reversed, so double-check the address before confirming.</p><button class="button" id="made-payment" type="button">I have made payment</button><a class="button button-light" href="${slug ? `/fundraisers/${encodeURIComponent(slug)}` : "/"}">Back</a></section></main>${footer()}`;
  setupMenu();
  document.querySelector("#copy-address").addEventListener("click", async event => { await navigator.clipboard.writeText(BTC_PAYMENT.address); event.currentTarget.textContent = "Address copied"; });
  document.querySelector("#made-payment").addEventListener("click", () => {
    document.querySelector("#pay-panel").innerHTML = `<div>${brand()}</div><span class="success-mark">✓</span><h1>Thank you!</h1><p>We appreciate your generosity toward “${esc(title)}”. Your gift will be confirmed once the Bitcoin transfer is received.</p><a class="button" href="${slug ? `/fundraisers/${encodeURIComponent(slug)}` : "/fundraisers"}">Donate more</a>`;
  });
}

async function renderThankYou() {
  document.title = "Gift status | Kindwell";
  const params = new URLSearchParams(location.search);
  const txRef = params.get("tx_ref");
  appRoot.innerHTML = `${header()}<main class="auth-wrap"><section class="auth-panel"><div>${brand()}</div><span class="success-mark">✓</span><h1>Checking your gift</h1><p id="payment-status">We're confirming your payment with our payment partner. This can take a moment.</p><a class="button" href="/">Back to Kindwell</a></section></main>${footer()}`;
  setupMenu();
  const message = document.querySelector("#payment-status");
  const mark = document.querySelector(".success-mark");
  if (!txRef) { message.textContent = "We couldn't verify a payment from this link. If you completed a gift, check your receipt."; return; }
  if (params.get("status") === "cancelled") { message.textContent = "Your payment was cancelled. No gift was recorded."; mark.textContent = "×"; return; }
  try {
    const check = () => request(`/api/donations/status/${encodeURIComponent(txRef)}`);
    let status = await check();
    for (let attempt = 0; !status.confirmed && !status.failed && attempt < 4; attempt++) { await new Promise(resolve => setTimeout(resolve, 1400)); status = await check(); }
    if (status.failed) { message.textContent = "This payment was not completed. No gift was recorded."; mark.textContent = "×"; return; }
    message.textContent = status.confirmed ? `Your ${formatCurrency(status.originalAmount, status.currency)} gift to ${status.campaign} is confirmed. Thank you for showing up.` : "Your payment is still being confirmed. Your gift will appear once our payment partner confirms it; no payment is shown as complete before then.";
    mark.textContent = status.confirmed ? "✓" : "…";
  } catch { message.textContent = "We couldn't verify the payment just yet. Your gift will appear after our payment partner confirms it."; }
}

async function renderAdmin() {
  document.title = "Administrator | Kindwell";
  appRoot.innerHTML = `${header()}<main class="auth-wrap"><section class="auth-panel"><div>${brand()}</div><h1>Administrator sign in</h1><p>Private access for the Kindwell administrator.</p><form id="admin-login" class="admin-form"><label for="admin-email">Email</label><input id="admin-email" type="email" autocomplete="username" required><label for="admin-password">Password</label><input id="admin-password" type="password" autocomplete="current-password" required><button class="button" type="submit">Sign in securely</button><p class="notice" id="admin-error" role="alert"></p></form></section></main>${footer()}`;
  setupMenu();
  try { await request("/api/admin/fundraisers"); await renderDashboard(); return; }
  catch (issue) {
    if (!issue.message.includes("(401)")) document.querySelector("#admin-error").textContent = issue.message;
  }
  document.querySelector("#admin-login").addEventListener("submit", async event => {
    event.preventDefault();
    const error = document.querySelector("#admin-error");
    try {
      await request("/api/admin/login", { method: "POST", body: JSON.stringify({ email: document.querySelector("#admin-email").value, password: document.querySelector("#admin-password").value }) });
      await renderDashboard();
    } catch (issue) { error.textContent = issue.message; }
  });
}

async function renderDashboard() {
  let items = [];
  let donations = [];
  let donationPageData;
  let overview;
  try { [items, donationPageData, overview] = await Promise.all([request("/api/admin/fundraisers"), request("/api/admin/donations"), request("/api/admin/overview")]); donations = donationPageData.items; }
  catch { appRoot.innerHTML = `${header()}<main class="page-wrap page-shell"><p>Administrator session expired. <a href="/admin">Sign in again</a></p></main>${footer()}`; return; }
  appRoot.innerHTML = `${header()}<main class="admin-layout"><div class="page-wrap"><div class="admin-top"><div><span class="eyebrow">Private workspace</span><h1>Campaign dashboard</h1></div><button class="button button-light button-small" id="admin-logout">Sign out</button></div><section class="admin-overview" aria-label="Platform overview"><article><span>Published fundraisers</span><strong>${Number(overview.publishedFundraisers).toLocaleString()}</strong></article><article><span>Draft fundraisers</span><strong>${Number(overview.draftFundraisers).toLocaleString()}</strong></article><article><span>Confirmed gifts</span><strong>${Number(overview.confirmedDonations).toLocaleString()}</strong></article><article><span>Confirmed gross</span><strong>${money(overview.confirmedGross)}</strong></article><article><span>Pending payments</span><strong>${Number(overview.pendingPayments).toLocaleString()}</strong></article><article><span>Failed payments</span><strong>${Number(overview.failedPayments).toLocaleString()}</strong></article></section><p class="admin-finance-note">Confirmed gross is not platform profit. Processor fees, refunds, reserves, and beneficiary payouts are not yet tracked in this dashboard.</p><div class="admin-tabs"><button class="active" data-tab="campaigns">Fundraisers</button><button data-tab="donations">Donations</button></div><section id="campaigns-tab"><div class="admin-grid"><div class="admin-panel"><h2 id="editor-heading">Add a fundraiser</h2><form class="admin-form" id="campaign-form"><input id="campaign-id" type="hidden"><label for="campaign-title">Campaign title</label><input id="campaign-title" maxlength="100" required><label for="campaign-summary">Short description</label><textarea id="campaign-summary" maxlength="220" required></textarea><label for="campaign-story">Campaign story</label><textarea id="campaign-story" maxlength="12000" required></textarea><label for="campaign-category">Category</label><select id="campaign-category" required>${["Medical", "Emergency", "Education", "Community", "Animals", "Memorial", "Environment", "Other"].map(category => `<option value="${category}">${category}</option>`).join("")}</select><label for="campaign-organizer-name">Organizer name</label><input id="campaign-organizer-name" maxlength="100" required><label for="campaign-organizer-country">Organizer country</label><select id="campaign-organizer-country"><option value="">Choose a country</option></select><div id="organizer-region-wrap" hidden><label for="campaign-organizer-region">State / region</label><select id="campaign-organizer-region"><option value="">Choose a state or region</option></select></div><label for="campaign-organizer-city">City (optional)</label><input id="campaign-organizer-city" maxlength="80" placeholder="City"><label for="campaign-organizer-profile-image">Profile photo URL</label><input id="campaign-organizer-profile-image" type="text" inputmode="url" placeholder="https://…"><label for="profile-image-upload">Or upload a profile photo</label><input id="profile-image-upload" type="file" accept="image/jpeg,image/png,image/webp"><label for="campaign-organizer-bio">Profile bio</label><textarea id="campaign-organizer-bio" maxlength="500" placeholder="A brief intro for the organizer page."></textarea><label for="campaign-image">Cover image</label><input id="campaign-image" type="text" inputmode="url" required placeholder="https://…"><label for="image-upload">Or upload an image</label><input id="image-upload" type="file" accept="image/jpeg,image/png,image/webp"><label for="campaign-goal">Fundraising goal (USD)</label><input id="campaign-goal" type="number" min="1" max="1000000" step="1" required><div class="form-checks"><label><input id="campaign-published" type="checkbox"> Published</label><label><input id="campaign-featured" type="checkbox"> Featured</label></div><button class="button" type="submit">Save fundraiser</button><button class="button button-light" id="cancel-edit" type="button" hidden>Cancel editing</button><p class="notice" id="campaign-notice" role="alert"></p></form></div><div class="admin-panel"><h2>Manage fundraisers</h2><div class="admin-list">${items.map(item => `<div class="admin-campaign"><img src="${esc(item.image)}" alt=""><div><strong>${esc(item.title)}</strong><small>${item.published ? "Published" : "Draft"} · ${money(item.raised)} raised</small></div><div><button type="button" data-edit="${item.id}">Edit</button><button type="button" data-update="${item.id}">Post update</button><button type="button" data-delete="${item.id}">Delete</button></div></div>`).join("") || `<p class="donor-count">No fundraisers yet.</p>`}</div></div></div></section><section id="donations-tab" hidden><div class="admin-panel"><h2>Donation records</h2><div class="admin-table-wrap"><table class="admin-table"><thead><tr><th>Campaign</th><th>Donor</th><th>Gift</th><th>Status</th><th>Date</th></tr></thead><tbody>${donations.map(item => `<tr><td>${esc(item.campaign)}</td><td>${esc(item.donor)}</td><td>${money(item.amount)}</td><td class="status ${esc(item.status)}">${esc(item.status)}</td><td>${new Date(item.date).toLocaleDateString()}</td></tr>`).join("") || `<tr><td colspan="5">No donation records yet.</td></tr>`}</tbody></table></div></div></section></div></main>${footer()}`;
  setupLocationPicker(document.querySelector("#campaign-organizer-country"), document.querySelector("#organizer-region-wrap"), document.querySelector("#campaign-organizer-region"), document.querySelector("#campaign-organizer-city"), "");
  document.querySelector("#donations-tab .admin-panel h2").insertAdjacentHTML("afterend", `<form id="payment-filters" class="report-filters"><label>Status <select id="payment-status"><option value="">All statuses</option><option value="succeeded">Confirmed</option><option value="pending">Pending</option><option value="failed">Failed</option></select></label><label>From <input id="payment-from" type="date"></label><label>To <input id="payment-to" type="date"></label><button class="button button-small" type="submit">Filter history</button></form>`);
  document.querySelector("#donations-tab tbody").id = "payment-history";
  document.querySelector("#donations-tab .admin-table-wrap").insertAdjacentHTML("afterend", `<div class="report-presets" id="payment-pagination"></div><p class="notice" id="payment-notice" role="status"></p>`);
  document.querySelector(".admin-tabs").insertAdjacentHTML("beforeend", `<button data-tab="reports">Financial reports</button>`);
  document.querySelector("#donations-tab").insertAdjacentHTML("afterend", `<section id="reports-tab" hidden><div class="admin-panel"><div class="report-heading"><div><h2>Financial reports</h2><p>Totals are based on recorded donation attempts. Only Kindwell-confirmed gifts count toward confirmed gross.</p></div><button class="button button-light button-small" id="report-export" type="button" disabled>Export CSV</button></div><div class="report-presets"><button type="button" data-report-range="week">This week</button><button type="button" data-report-range="last-week">Last week</button><button type="button" data-report-range="month">This month</button><button type="button" data-report-range="last-month">Last month</button><button type="button" data-report-range="all">All time</button></div><form class="report-filters" id="report-filters"><label>From <input id="report-from" type="date"></label><label>To <input id="report-to" type="date"></label><button class="button button-small" type="submit">Apply range</button></form><p class="notice" id="report-notice" role="status"></p><div class="admin-overview report-overview" id="report-summary"></div><div class="admin-table-wrap"><table class="admin-table"><thead><tr><th>Date (UTC)</th><th>Confirmed gifts</th><th>Confirmed gross</th><th>Pending</th><th>Failed</th></tr></thead><tbody id="report-daily"></tbody></table></div></div></section>`);
  document.querySelector("#reports-tab .admin-panel").insertAdjacentHTML("beforeend", `<h3>Payment ledger</h3><p class="admin-finance-note">Each confirmed payment is journaled as processor clearing debit and campaign funds payable credit. This does not represent a settlement or beneficiary payout.</p><div class="admin-table-wrap"><table class="admin-table"><thead><tr><th>Date (UTC)</th><th>Donation</th><th>Campaign</th><th>Currency</th><th>Amount</th><th>Debit</th><th>Credit</th></tr></thead><tbody id="ledger-entries"></tbody></table></div><div class="report-presets" id="ledger-pagination"></div>`);
  const loadPayments = async (page = 1) => {
    const params = new URLSearchParams({ page: String(page), pageSize: "50" });
    const status = document.querySelector("#payment-status").value;
    const from = document.querySelector("#payment-from").value;
    const to = document.querySelector("#payment-to").value;
    if (status) params.set("status", status);
    if (from) params.set("from", from);
    if (to) params.set("to", to);
    const notice = document.querySelector("#payment-notice");
    notice.textContent = "Loading payment history…";
    try {
      const result = await request(`/api/admin/donations?${params}`);
      document.querySelector("#payment-history").innerHTML = result.items.map(item => `<tr><td>${esc(item.campaign)}</td><td>${esc(item.donor)}</td><td>${money(item.amount)}</td><td class="status ${esc(item.status)}">${esc(item.status)}</td><td>${new Date(item.date).toLocaleString()}</td></tr>`).join("") || `<tr><td colspan="5">No payment records match this filter.</td></tr>`;
      const pagination = document.querySelector("#payment-pagination");
      pagination.innerHTML = result.pages > 1 ? `<button type="button" data-payment-page="${Math.max(1, result.page - 1)}" ${result.page <= 1 ? "disabled" : ""}>Previous</button><span>Page ${result.page} of ${result.pages} · ${result.total} records</span><button type="button" data-payment-page="${Math.min(result.pages, result.page + 1)}" ${result.page >= result.pages ? "disabled" : ""}>Next</button>` : `<span>${result.total} payment records</span>`;
      pagination.querySelectorAll("[data-payment-page]").forEach(button => button.addEventListener("click", () => loadPayments(Number(button.dataset.paymentPage))));
      notice.textContent = "Payment attempts are shown separately from confirmed ledger entries.";
    } catch (issue) {
      notice.textContent = issue.message;
      document.querySelector("#payment-history").replaceChildren();
    }
  };
  document.querySelector("#payment-filters").addEventListener("submit", event => {
    event.preventDefault();
    loadPayments(1);
  });
  void loadPayments(1);
  document.querySelector("#campaign-goal").insertAdjacentHTML("afterend", `<label for="campaign-reported-raised">Raised before Kindwell (USD, organizer-reported)</label><input id="campaign-reported-raised" type="number" min="0" max="1000000" step="1" value="0"><label for="campaign-reported-donations">Gifts before Kindwell (organizer-reported)</label><input id="campaign-reported-donations" type="number" min="0" max="1000000" step="1" value="0"><p class="donor-count">Enter only prior gifts you can substantiate. These figures are labeled as organizer-reported publicly and kept separate from Kindwell-confirmed gifts.</p>`);
  document.querySelector("#campaigns-tab").insertAdjacentHTML("beforeend", `<section class="admin-panel update-manager"><h2>Post a campaign update</h2><form class="admin-form" id="campaign-update-form"><label for="update-campaign">Fundraiser</label><select id="update-campaign" required>${items.map(item => `<option value="${item.id}">${esc(item.title)}</option>`).join("")}</select><label for="update-title">Update title</label><input id="update-title" maxlength="120" required><label for="update-body">Update</label><textarea id="update-body" maxlength="5000" required></textarea><button class="button" type="submit"${items.length ? "" : " disabled"}>Publish update</button><p class="notice" id="update-notice" role="alert"></p></form></section>`);
  document.querySelector("#admin-logout").addEventListener("click", async () => { try { await request("/api/admin/logout", { method: "POST", body: "{}" }); } finally { location.assign("/admin"); } });
  document.querySelector(".admin-tabs").insertAdjacentHTML("beforeend", `<button data-tab="finance">Currency finance</button>`);
  document.querySelector("#reports-tab").insertAdjacentHTML("afterend", `<section id="finance-tab" hidden><div class="admin-panel"><div class="report-heading"><div><h2>Currency finance</h2><p><strong>Estimated USD</strong> is the donor's amount multiplied by the exchange rate recorded when checkout started. It is not money received or converted. <strong>Settlement</strong> shows only what our payment partner reported for each confirmed transaction.</p></div><a class="button button-light button-small" id="finance-export" href="/api/admin/finance/export.csv">Export CSV</a></div><form class="report-filters" id="finance-filters"><label>Mode <select id="finance-mode"><option value="">Current key</option><option value="test">Test</option><option value="live">Live</option><option value="all">All</option></select></label><label>Search <input id="finance-q" maxlength="100" placeholder="Reference, campaign, donor"></label><label>Status <select id="finance-status"><option value="">All</option><option value="succeeded">Successful</option><option value="pending">Pending</option><option value="failed">Failed</option><option value="refunded">Refunded</option></select></label><label>Currency <input id="finance-currency" maxlength="3" size="4" placeholder="NGN"></label><label>From <input id="finance-from" type="date"></label><label>To <input id="finance-to" type="date"></label><button class="button button-small" type="submit">Apply</button></form><p class="notice" id="finance-notice" role="status"></p><div class="admin-overview" id="finance-summary"></div><div id="finance-tables"></div><h3>Payment history</h3><div class="admin-table-wrap"><table class="admin-table"><thead><tr><th>Date</th><th>Campaign / donor</th><th>Reference</th><th>Status</th><th>Original</th><th>Rate (per 1 USD)</th><th>USD estimate</th><th>Charged</th><th>Provider fee</th><th>Settled (reported)</th><th>Mode</th></tr></thead><tbody id="finance-payments"></tbody></table></div><div class="report-presets" id="finance-pagination"></div></div></section>`);
  const dash = "—";
  const loadFinance = async (page = 1) => {
    const notice = document.querySelector("#finance-notice");
    notice.textContent = "Loading financial data…";
    const params = new URLSearchParams();
    for (const [key, id] of [["mode", "finance-mode"], ["q", "finance-q"], ["status", "finance-status"], ["currency", "finance-currency"], ["from", "finance-from"], ["to", "finance-to"]]) {
      const value = document.querySelector(`#${id}`).value.trim();
      if (value) params.set(key, value);
    }
    const summaryParams = new URLSearchParams();
    if (params.get("mode")) summaryParams.set("mode", params.get("mode"));
    const listParams = new URLSearchParams(params);
    listParams.set("page", String(page));
    listParams.set("pageSize", "25");
    try {
      const [summary, payments] = await Promise.all([request(`/api/admin/finance?${summaryParams}`), request(`/api/admin/finance/payments?${listParams}`)]);
      const usd = cents => formatCurrency(cents, "USD");
      const rate = summary.rates;
      const rateText = !rate.available ? "Unavailable" : `${new Date(rate.updatedAt).toLocaleString()}${rate.stale ? " (stale)" : ""}`;
      document.querySelector("#finance-summary").innerHTML = `<article><span>Lifetime (est. USD)</span><strong>${usd(summary.lifetime.usdEquivalent)}</strong></article><article><span>Today (est. USD)</span><strong>${usd(summary.today.usdEquivalent)}</strong></article><article><span>Last 7 days (est. USD)</span><strong>${usd(summary.last7Days.usdEquivalent)}</strong></article><article><span>Successful</span><strong>${summary.statuses.succeeded}</strong></article><article><span>Pending</span><strong>${summary.statuses.pending}</strong></article><article><span>Failed</span><strong>${summary.statuses.failed}</strong></article><article><span>Refunded</span><strong>${summary.statuses.refunded}</strong></article><article><span>Rates last updated</span><strong style="font-size:15px">${esc(rateText)}</strong></article>`;
      const currencyRows = summary.byCurrency.map(row => `<tr><td>${esc(row.currency)}</td><td>${row.count}</td><td>${formatCurrency(row.originalMinor, row.currency)}</td><td>${usd(row.usdEquivalent)}</td><td>${row.transactionsWithFeeData ? formatCurrency(row.providerFeesMinor, row.currency) : "Not reported"}</td></tr>`).join("") || `<tr><td colspan="5">No successful payments in this mode yet.</td></tr>`;
      const monthRows = summary.months.map(row => `<tr><td>${esc(row.month)}</td><td>${row.count}</td><td>${usd(row.usdEquivalent)}</td></tr>`).join("") || `<tr><td colspan="3">No successful payments yet.</td></tr>`;
      const settlementRows = summary.settlements.map(row => `<tr><td>${row.currency === "unreported" ? "Currency not reported" : esc(row.currency)}</td><td>${row.count}</td><td>${row.currency === "unreported" ? (row.settledMinor / 100).toLocaleString(undefined, { minimumFractionDigits: 2 }) : formatCurrency(row.settledMinor, row.currency)}</td></tr>`).join("") || `<tr><td colspan="3">Our payment partner has not reported settlement amounts for any payment yet.</td></tr>`;
      document.querySelector("#finance-tables").innerHTML = `<h3>Donations by original currency</h3><div class="admin-table-wrap"><table class="admin-table"><thead><tr><th>Currency</th><th>Payments</th><th>Original total</th><th>USD estimate</th><th>Provider fees</th></tr></thead><tbody>${currencyRows}</tbody></table></div><h3>Monthly totals (estimated USD)</h3><div class="admin-table-wrap"><table class="admin-table"><thead><tr><th>Month</th><th>Payments</th><th>USD estimate</th></tr></thead><tbody>${monthRows}</tbody></table></div><h3>Settlement reported by our payment partner</h3><div class="admin-table-wrap"><table class="admin-table"><thead><tr><th>Settlement currency</th><th>Payments</th><th>Settled amount</th></tr></thead><tbody>${settlementRows}</tbody></table></div><p class="admin-finance-note">${esc(summary.currencyConversion.explanation)}</p>`;
      document.querySelector("#finance-payments").innerHTML = payments.items.map(row => `<tr><td>${new Date(row.date).toLocaleString()}</td><td>${esc(row.campaign)}<br><small>${esc(row.donor)}</small>${row.email ? `<br><small>${esc(row.email)}</small>` : ""}</td><td><small>${esc(row.reference || dash)}</small></td><td class="status ${esc(row.status)}">${esc(row.status)}</td><td>${formatCurrency(row.originalMinor, row.currency)}</td><td>${row.currency === "USD" ? "1" : esc(row.exchangeRate || dash)}</td><td>≈ ${usd(row.usdEquivalentCents)}</td><td>${row.chargedMinor == null ? dash : formatCurrency(row.chargedMinor, row.chargedCurrency || row.currency)}</td><td>${row.providerFeeMinor == null ? dash : formatCurrency(row.providerFeeMinor, row.chargedCurrency || row.currency)}</td><td>${row.settledMinor == null ? dash : formatCurrency(row.settledMinor, row.settlementCurrency || row.currency) + (row.settlementCurrency ? "" : " (currency unreported)")}</td><td>${esc(row.mode || "unknown")}</td></tr>`).join("") || `<tr><td colspan="11">No payments match these filters.</td></tr>`;
      const pagination = document.querySelector("#finance-pagination");
      pagination.innerHTML = payments.pages > 1 ? `<button type="button" data-finance-page="${Math.max(1, payments.page - 1)}" ${payments.page <= 1 ? "disabled" : ""}>Previous</button><span>Page ${payments.page} of ${payments.pages} · ${payments.total} records</span><button type="button" data-finance-page="${Math.min(payments.pages, payments.page + 1)}" ${payments.page >= payments.pages ? "disabled" : ""}>Next</button>` : `<span>${payments.total} payment records</span>`;
      pagination.querySelectorAll("[data-finance-page]").forEach(button => button.addEventListener("click", () => loadFinance(Number(button.dataset.financePage))));
      document.querySelector("#finance-export").href = `/api/admin/finance/export.csv?${params}`;
      notice.textContent = `Showing ${summary.mode} mode (configured key: ${summary.configuredMode}).`;
    } catch (issue) { notice.textContent = issue.message; }
  };
  let financeLoaded = false;
  document.querySelector("#finance-filters").addEventListener("submit", event => { event.preventDefault(); void loadFinance(1); });
  document.querySelectorAll(".admin-tabs button").forEach(button => button.addEventListener("click", () => {
    document.querySelector("#finance-tab").hidden = button.dataset.tab !== "finance";
    if (button.dataset.tab === "finance" && !financeLoaded) { financeLoaded = true; void loadFinance(1); }
  }));
  document.querySelectorAll(".admin-tabs button").forEach(button => button.addEventListener("click", () => {
    document.querySelectorAll(".admin-tabs button").forEach(item => item.classList.toggle("active", item === button));
    document.querySelector("#campaigns-tab").hidden = button.dataset.tab !== "campaigns";
    document.querySelector("#donations-tab").hidden = button.dataset.tab !== "donations";
    document.querySelector("#reports-tab").hidden = button.dataset.tab !== "reports";
  }));
  let reportData = null;
  let ledgerPage = 1;
  let reportRange = { from: null, to: null };
  const loadReport = async (from, to, page = 1) => {
    reportRange = { from, to };
    ledgerPage = page;
    const params = new URLSearchParams();
    if (from) params.set("from", from);
    if (to) params.set("to", to);
    const ledgerParams = new URLSearchParams(params);
    ledgerParams.set("page", String(page));
    ledgerParams.set("pageSize", "50");
    const notice = document.querySelector("#report-notice");
    notice.textContent = "Loading report…";
    document.querySelector("#report-export").disabled = true;
    try {
      const [nextReport, ledger] = await Promise.all([
        request(`/api/admin/reports${params.size ? `?${params}` : ""}`),
        request(`/api/admin/ledger?${ledgerParams}`)
      ]);
      reportData = nextReport;
      const summary = reportData.summary;
      document.querySelector("#report-summary").innerHTML = `<article><span>Confirmed gifts</span><strong>${Number(summary.confirmedDonations).toLocaleString()}</strong></article><article><span>Confirmed gross (USD)</span><strong>${money(summary.confirmedGross)}</strong></article><article><span>Pending payments · ${money(summary.pendingAmount)}</span><strong>${Number(summary.pendingPayments).toLocaleString()}</strong></article><article><span>Failed payments · ${money(summary.failedAmount)}</span><strong>${Number(summary.failedPayments).toLocaleString()}</strong></article>`;
      document.querySelector("#report-daily").innerHTML = reportData.daily.map(row => `<tr><td>${esc(row.date)}</td><td>${Number(row.confirmedDonations).toLocaleString()}</td><td>${money(row.confirmedGross)}</td><td>${Number(row.pendingPayments).toLocaleString()}</td><td>${Number(row.failedPayments).toLocaleString()}</td></tr>`).join("") || `<tr><td colspan="5">No donation records in this period.</td></tr>`;
      document.querySelector("#ledger-entries").innerHTML = ledger.items.map(item => `<tr><td>${new Date(item.date).toLocaleString("en-US", { timeZone: "UTC" })}</td><td>#${item.donationId}</td><td>${esc(item.campaign)}</td><td>${esc(item.currency.toUpperCase())}</td><td>${money(item.amount)}</td><td>${esc(item.debitAccount)}</td><td>${esc(item.creditAccount)}</td></tr>`).join("") || `<tr><td colspan="7">No confirmed ledger entries in this period.</td></tr>`;
      const pagination = document.querySelector("#ledger-pagination");
      pagination.innerHTML = ledger.pages > 1 ? `<button type="button" data-ledger-page="${Math.max(1, ledger.page - 1)}" ${ledger.page <= 1 ? "disabled" : ""}>Previous</button><span>Page ${ledger.page} of ${ledger.pages} · ${ledger.total} entries</span><button type="button" data-ledger-page="${Math.min(ledger.pages, ledger.page + 1)}" ${ledger.page >= ledger.pages ? "disabled" : ""}>Next</button>` : `<span>${ledger.total} ledger entries</span>`;
      pagination.querySelectorAll("[data-ledger-page]").forEach(button => button.addEventListener("click", () => loadReport(reportRange.from, reportRange.to, Number(button.dataset.ledgerPage))));
      notice.textContent = "Report refreshed. Pending and failed amounts are not confirmed revenue.";
      document.querySelector("#report-export").disabled = reportData.daily.length === 0;
    } catch (issue) {
      reportData = null;
      notice.textContent = issue.message;
      document.querySelector("#report-summary").replaceChildren();
      document.querySelector("#report-daily").replaceChildren();
      document.querySelector("#ledger-entries").replaceChildren();
    }
  };
  document.querySelector("#report-filters").addEventListener("submit", event => {
    event.preventDefault();
    loadReport(document.querySelector("#report-from").value || null, document.querySelector("#report-to").value || null);
  });
  document.querySelectorAll("[data-report-range]").forEach(button => button.addEventListener("click", () => {
    const today = new Date();
    const todayUtc = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate()));
    const formatDate = date => date.toISOString().slice(0, 10);
    const weekStart = new Date(todayUtc);
    weekStart.setUTCDate(weekStart.getUTCDate() - ((weekStart.getUTCDay() + 6) % 7));
    const monthStart = new Date(Date.UTC(todayUtc.getUTCFullYear(), todayUtc.getUTCMonth(), 1));
    const range = button.dataset.reportRange;
    let from = "";
    let to = formatDate(todayUtc);
    if (range === "week") from = formatDate(weekStart);
    else if (range === "last-week") {
      const lastWeekStart = new Date(weekStart);
      lastWeekStart.setUTCDate(lastWeekStart.getUTCDate() - 7);
      const lastWeekEnd = new Date(weekStart);
      lastWeekEnd.setUTCDate(lastWeekEnd.getUTCDate() - 1);
      from = formatDate(lastWeekStart);
      to = formatDate(lastWeekEnd);
    } else if (range === "month") from = formatDate(monthStart);
    else if (range === "last-month") {
      from = formatDate(new Date(Date.UTC(todayUtc.getUTCFullYear(), todayUtc.getUTCMonth() - 1, 1)));
      to = formatDate(new Date(Date.UTC(todayUtc.getUTCFullYear(), todayUtc.getUTCMonth(), 0)));
    }
    document.querySelector("#report-from").value = from;
    document.querySelector("#report-to").value = range === "all" ? "" : to;
    loadReport(from || null, range === "all" ? null : to);
  }));
  document.querySelector("#report-export").addEventListener("click", () => {
    if (!reportData) return;
    const quote = value => `"${String(value).replace(/"/g, '""')}"`;
    const rows = [["Date (UTC)", "Confirmed gifts", "Confirmed gross (USD cents)", "Pending payments", "Failed payments"], ...reportData.daily.map(row => [row.date, row.confirmedDonations, row.confirmedGross, row.pendingPayments, row.failedPayments])];
    const blob = new Blob([rows.map(row => row.map(quote).join(",")).join("\r\n")], { type: "text/csv;charset=utf-8" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = "kindwell-financial-report.csv";
    link.click();
    URL.revokeObjectURL(link.href);
  });
  loadReport(null, null);
  document.querySelectorAll("[data-edit]").forEach(button => button.addEventListener("click", () => {
    const item = items.find(campaign => campaign.id === Number(button.dataset.edit));
    if (!item) return;
    document.querySelector("#editor-heading").textContent = "Edit fundraiser";
    document.querySelector("#campaign-id").value = item.id;
    document.querySelector("#campaign-title").value = item.title;
    document.querySelector("#campaign-summary").value = item.summary;
    document.querySelector("#campaign-story").value = item.story;
    document.querySelector("#campaign-category").value = item.category;
    document.querySelector("#campaign-organizer-name").value = item.organizerName || "";
    setupLocationPicker(document.querySelector("#campaign-organizer-country"), document.querySelector("#organizer-region-wrap"), document.querySelector("#campaign-organizer-region"), document.querySelector("#campaign-organizer-city"), item.organizerLocation || "");
    document.querySelector("#campaign-organizer-profile-image").value = item.organizerProfileImage || "";
    document.querySelector("#campaign-organizer-bio").value = item.organizerBio || "";
    document.querySelector("#campaign-image").value = item.image;
    document.querySelector("#campaign-goal").value = item.goal / 100;
    document.querySelector("#campaign-reported-raised").value = item.reportedRaised / 100;
    document.querySelector("#campaign-reported-donations").value = item.reportedDonations;
    document.querySelector("#campaign-published").checked = item.published;
    document.querySelector("#campaign-featured").checked = item.featured;
    document.querySelector("#cancel-edit").hidden = false;
    document.querySelector("#campaign-form").scrollIntoView({ behavior: "smooth" });
  }));
  document.querySelectorAll("[data-update]").forEach(button => button.addEventListener("click", () => {
    document.querySelector("#update-campaign").value = button.dataset.update;
    document.querySelector("#update-title").focus();
    document.querySelector("#campaign-update-form").scrollIntoView({ behavior: "smooth" });
  }));
  document.querySelectorAll("[data-delete]").forEach(button => button.addEventListener("click", async () => {
    if (!confirm("Delete this fundraiser and its donation records? This cannot be undone.")) return;
    try { await request(`/api/admin/fundraisers/${button.dataset.delete}`, { method: "DELETE" }); await renderDashboard(); }
    catch (issue) { alert(issue.message); }
  }));
  document.querySelector("#cancel-edit").addEventListener("click", () => renderDashboard());
  document.querySelector("#image-upload").addEventListener("change", async event => {
    const file = event.target.files[0]; if (!file) return;
    const form = new FormData(); form.append("image", file);
    try { const uploaded = await request("/api/admin/uploads", { method: "POST", body: form }); document.querySelector("#campaign-image").value = uploaded.url; }
    catch (issue) { document.querySelector("#campaign-notice").textContent = issue.message; }
  });
  document.querySelector("#profile-image-upload").addEventListener("change", async event => {
    const file = event.target.files[0]; if (!file) return;
    const form = new FormData(); form.append("image", file);
    try { const uploaded = await request("/api/admin/uploads", { method: "POST", body: form }); document.querySelector("#campaign-organizer-profile-image").value = uploaded.url; }
    catch (issue) { document.querySelector("#campaign-notice").textContent = issue.message; }
  });
  document.querySelector("#campaign-form").addEventListener("submit", async event => {
    event.preventDefault();
    const notice = document.querySelector("#campaign-notice"); notice.textContent = "";
    const input = {
      title: document.querySelector("#campaign-title").value,
      summary: document.querySelector("#campaign-summary").value,
      story: document.querySelector("#campaign-story").value,
      image: document.querySelector("#campaign-image").value,
      category: document.querySelector("#campaign-category").value,
      organizerName: document.querySelector("#campaign-organizer-name").value,
      organizerLocation: [document.querySelector("#campaign-organizer-city").value.trim(), document.querySelector("#campaign-organizer-region").value, document.querySelector("#campaign-organizer-country").value].filter(Boolean).join(", "),
      organizerProfileImage: document.querySelector("#campaign-organizer-profile-image").value,
      organizerBio: document.querySelector("#campaign-organizer-bio").value,
      goal: Math.round(Number(document.querySelector("#campaign-goal").value) * 100),
      reportedRaised: Math.round(Number(document.querySelector("#campaign-reported-raised").value) * 100),
      reportedDonationCount: Math.round(Number(document.querySelector("#campaign-reported-donations").value)),
      published: document.querySelector("#campaign-published").checked,
      featured: document.querySelector("#campaign-featured").checked
    };
    const id = document.querySelector("#campaign-id").value;
    try { await request(id ? `/api/admin/fundraisers/${id}` : "/api/admin/fundraisers", { method: id ? "PUT" : "POST", body: JSON.stringify(input) }); await renderDashboard(); }
    catch (issue) { notice.textContent = issue.message; }
  });
  document.querySelector("#campaign-update-form").addEventListener("submit", async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const button = form.querySelector("button[type=submit]");
    const notice = document.querySelector("#update-notice");
    button.disabled = true;
    button.textContent = "Publishing…";
    notice.textContent = "";
    try {
      await request(`/api/admin/fundraisers/${encodeURIComponent(form.querySelector("#update-campaign").value)}/updates`, {
        method: "POST",
        body: JSON.stringify({ title: form.querySelector("#update-title").value, body: form.querySelector("#update-body").value })
      });
      form.reset();
      notice.textContent = "Update published. It now appears on the fundraiser page.";
    } catch (issue) {
      notice.textContent = issue.message;
    } finally {
      button.disabled = false;
      button.textContent = "Publish update";
    }
  });
}

async function renderInfoPage(kind) {
  const isPrivacy = kind === "privacy";
  const title = isPrivacy ? "Privacy, with care" : "Terms of service";
  appRoot.innerHTML = `${header()}<main class="page-wrap page-shell"><div class="detail-main"><span class="eyebrow">Kindwell policies</span><h1>${title}</h1><p class="detail-story">${isPrivacy ? "Kindwell collects only the information needed to operate this service. Donation payments are handled by our payment partner; Kindwell never receives or stores your full card number or security code. We retain a donor name (or anonymous preference), gift amount, campaign, and payment status for transparent recordkeeping. We do not sell personal information. Contact hello@kindwell.care with privacy questions or requests." : "Kindwell provides community fundraising pages operated by a single administrator. Gifts are processed by our payment partner and are subject to its terms and payment processing charges. A gift is recorded as confirmed only after the payment provider verifies it. Refund requests are reviewed individually by contacting hello@kindwell.care. Campaign stories and goals are maintained by the Kindwell administrator. Kindwell may update these terms as the service evolves."}</p></div></main>${footer()}`;
  document.title = `${title} | Kindwell`; setupMenu();
}

async function renderStart(step) {
  const questions = [
    { key: "category", title: "What is your fundraiser for?", hint: "Pick the category that fits best.", field: () => `<label for="a">Category</label><select id="a" required><option value="">Choose a category</option>${["Medical", "Emergency", "Education", "Community", "Animals", "Memorial", "Environment", "Other"].map(c => `<option>${c}</option>`).join("")}</select>` },
    { key: "beneficiary", title: "Who are you raising money for?", hint: "This helps us understand your story.", field: () => `<label for="a">Beneficiary</label><select id="a" required><option value="">Choose one</option>${["Myself", "My family", "A friend or someone I know", "A community or charity"].map(c => `<option>${c}</option>`).join("")}</select>` },
    { key: "reason", title: "Why do you want to start fundraising?", hint: "Share what happened and how the money will help.", field: () => `<label for="a">Your reason</label><textarea id="a" maxlength="2000" minlength="20" required placeholder="Tell us your story…"></textarea>` },
    { key: "goal", title: "How much do you hope to raise?", hint: "Enter your goal in US dollars. You can change it later.", field: () => `<label for="a">Goal (USD)</label><input id="a" type="number" min="1" max="100000000" step="1" inputmode="numeric" required placeholder="5000">` },
    { key: "contact", title: "Where can we reach you?", hint: "We'll use this to follow up about your fundraiser.", field: () => `<label for="a">Your name</label><input id="a" maxlength="80" autocomplete="name" required><label for="b">Email</label><input id="b" type="email" maxlength="254" autocomplete="email" required placeholder="you@example.com"><label for="c">When would you like to start?</label><select id="c" required><option value="">Choose one</option>${["Right away", "Within a month", "Just exploring"].map(c => `<option>${c}</option>`).join("")}</select>` }
  ];
  const saved = JSON.parse(sessionStorage.getItem("kindwell-start") || "{}");
  const save = () => sessionStorage.setItem("kindwell-start", JSON.stringify(saved));
  document.title = "Start a fundraiser | Kindwell";

  if (step === "done") {
    sessionStorage.removeItem("kindwell-start");
    appRoot.innerHTML = `${header()}<main class="auth-wrap"><section class="auth-panel"><span class="success-mark">✓</span><h1>Thank you!</h1><p>We've received your answers and will be in touch by email about your fundraiser.</p><a class="button" href="/">Back to Kindwell</a></section></main>${footer()}`;
    setupMenu();
    return;
  }

  const index = Number(step) - 1;
  const question = questions[index];
  if (!question) { location.replace("/start/1"); return; }
  // Steps can't be skipped: send the donor back to the first unanswered one.
  const firstMissing = questions.findIndex((q, i) => i < index && q.key !== "contact" && !saved[q.key]);
  if (firstMissing !== -1) { location.replace(`/start/${firstMissing + 1}`); return; }

  const last = index === questions.length - 1;
  const dots = questions.map((_, i) => `<span style="flex:1;height:5px;border-radius:5px;background:${i <= index ? "#2f6f4f" : "#e0e7df"}"></span>`).join("");
  appRoot.innerHTML = `${header()}<main class="auth-wrap"><section class="auth-panel"><div style="display:flex;gap:6px;margin-bottom:10px">${dots}</div><p style="font-weight:700;color:#48574d;margin-bottom:8px">Step ${index + 1} of ${questions.length}</p><h1>${esc(question.title)}</h1><p>${esc(question.hint)}</p><form id="start-form" class="admin-form">${question.field()}<button class="button" type="submit">${last ? "Submit" : "Continue"}</button>${index > 0 ? `<a class="button button-light" style="width:100%;margin-top:10px" href="/start/${index}">Back</a>` : ""}<p class="notice" id="start-error" role="alert"></p></form></section></main>${footer()}`;

  const a = document.querySelector("#a"), b = document.querySelector("#b"), c = document.querySelector("#c");
  if (question.key === "contact") { a.value = saved.name || ""; b.value = saved.email || ""; c.value = saved.timeline || ""; }
  else a.value = saved[question.key] ?? "";
  a.focus();

  document.querySelector("#start-form").addEventListener("submit", async event => {
    event.preventDefault();
    const error = document.querySelector("#start-error");
    const button = event.currentTarget.querySelector("button[type=submit]");
    if (question.key === "contact") { saved.name = a.value.trim(); saved.email = b.value.trim(); saved.timeline = c.value; }
    else saved[question.key] = question.key === "goal" ? Number(a.value) : a.value.trim();
    save();
    if (!last) { location.assign(`/start/${index + 2}`); return; }
    button.disabled = true; button.textContent = "Submitting…"; error.textContent = "";
    try {
      await request("/api/start-requests", { method: "POST", body: JSON.stringify(saved) });
      location.assign("/start/done");
    } catch (issue) { error.textContent = issue.message; button.disabled = false; button.textContent = "Submit"; }
  });
  setupMenu();
}

async function start() {
  await loadCsrf();
  const pathname = decodeURIComponent(location.pathname);
  if (pathname.startsWith("/start/")) return renderStart(pathname.slice("/start/".length).replace(/\/$/, ""));
  if (pathname === "/login") return renderPublicLogin();
  if (pathname === "/signup") return renderSignup();
  if (pathname === "/forgot-password") return renderForgotPassword();
  if (pathname === "/reset-password") return renderResetPassword();
  if (pathname === "/fundraisers" || pathname === "/fundraisers/") return renderOngoing();
  if (pathname === "/people" || pathname === "/people/") return renderPeople();
  if (pathname === "/categories" || pathname === "/categories/") return renderCategories();
  if (pathname === "/admin" || pathname === "/admin/") return renderAdmin();
  if (pathname === "/thank-you") return renderThankYou();
  if (pathname.startsWith("/pay/")) return renderPay(pathname.slice("/pay/".length).replace(/\/$/, ""));
  if (pathname === "/privacy") return renderInfoPage("privacy");
  if (pathname === "/terms") return renderInfoPage("terms");
  if (pathname.startsWith("/fundraisers/")) return renderDetail(pathname.slice("/fundraisers/".length));
  if (pathname.startsWith("/people/")) return renderPerson(pathname.slice("/people/".length));
  return renderHome();
}

start();
