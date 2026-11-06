/* Nova Enhance browser add-on — background. Talks to the Nova Enhance app on this PC. Designed by A.HAMROUNI */
// The chrome.* namespace with callbacks works in Chrome, Edge and Firefox alike.
var B = chrome;
var IS_FIREFOX = typeof browser !== "undefined";
if (typeof NOVA === "undefined" && typeof importScripts === "function") { try { importScripts("config.js"); } catch (e) { } }

function api(path, body) {
  return fetch("http://127.0.0.1:" + NOVA.port + "/api/" + path, {
    method: body ? "POST" : "GET",
    headers: body ? { "X-Nova": "1", "X-Nova-Key": NOVA.key, "Content-Type": "application/json" } : { "X-Nova": "1", "X-Nova-Key": NOVA.key },
    body: body ? JSON.stringify(body) : undefined
  }).then(function (r) { return r.json(); }).catch(function () {
    return { error: "Nova Enhance is not running on this PC — start it from the Start menu." };
  });
}

var MODES = [["grammar", "✦ Fix grammar"], ["shorten", "Shorten"], ["professional", "Professional"], ["informal", "Informal"], ["expand", "Expand"]];
var MAIN = [["English", "English"], ["Arabic", "العربية"], ["French", "Français"], ["German", "Deutsch"]];
var OTHER = [["Spanish", "Español"], ["Italian", "Italiano"], ["Turkish", "Türkçe"], ["Urdu", "اردو Urdu"], ["Hindi", "हिन्दी"], ["Chinese", "中文"], ["Russian", "Русский"], ["Portuguese", "Português"]];

function menus() {
  B.contextMenus.removeAll(function () {
    var ctx = ["selection", "editable"];
    B.contextMenus.create({ id: "nova", title: "✦ Nova Enhance", contexts: ctx });
    MODES.forEach(function (m) { B.contextMenus.create({ id: "m:" + m[0], parentId: "nova", title: m[1] + (m[0] === "grammar" ? "   (Alt+G)" : ""), contexts: ctx }); });
    B.contextMenus.create({ id: "s1", parentId: "nova", type: "separator", contexts: ctx });
    MAIN.forEach(function (l) { B.contextMenus.create({ id: "t:" + l[0], parentId: "nova", title: "🌐 Translate → " + l[1], contexts: ctx }); });
    B.contextMenus.create({ id: "other", parentId: "nova", title: "🌐 Other languages", contexts: ctx });
    OTHER.forEach(function (l) { B.contextMenus.create({ id: "t:" + l[0], parentId: "other", title: l[1], contexts: ctx }); });
    B.contextMenus.create({ id: "s2", parentId: "nova", type: "separator", contexts: ctx });
    B.contextMenus.create({ id: "putback", parentId: "nova", title: "↩ Put back original", contexts: ctx });
    B.contextMenus.create({ id: "panel", parentId: "nova", title: "✦ Open Nova panel", contexts: ctx });
  });
}

function send(tabId, msg, frameId) {
  return new Promise(function (res) {
    try { B.tabs.sendMessage(tabId, msg, { frameId: frameId || 0 }, function (r) { void (B.runtime.lastError); res(r); }); } catch (e) { res(null); }
  });
}

/** Enhances the selection in the page and puts the result back in place (or shows it when the page can't be edited). */
function run(tab, mode, lang, fallbackText, frameId) {
  if (!tab) return;
  var f = frameId || 0;
  send(tab.id, { t: "grab" }, f).then(function (sel) {
    var text = sel && sel.text ? sel.text : (fallbackText || "");
    if (!text || !text.replace(/\s/g, "").length) { send(tab.id, { t: "toast", kind: "bad", text: "Select some text first" }, f); return; }
    send(tab.id, { t: "toast", kind: "busy", text: mode === "translate" ? "🌐 Translating…" : "✦ Working…" }, f);
    api("enhance", { mode: mode, text: text, lang: lang || "", host: "browser" }).then(function (r) {
      if (r.error) { send(tab.id, { t: "toast", kind: "bad", text: "⚠ " + r.error }, f); return; }
      send(tab.id, { t: "replace", text: r.result, original: text, label: r.label, ai: r.ai }, f);
    });
  });
}

function openPanel(tab) {
  try {
    if (IS_FIREFOX) browser.sidebarAction.open();
    else if (B.sidePanel && tab) B.sidePanel.open({ tabId: tab.id });
  } catch (e) { }
}

B.contextMenus.onClicked.addListener(function (info, tab) {
  var id = String(info.menuItemId);
  if (id === "panel") { openPanel(tab); return; }
  if (id === "putback") { send(tab.id, { t: "putback" }, info.frameId); return; }
  if (id.indexOf("m:") === 0) run(tab, id.substring(2), "", info.selectionText, info.frameId);
  else if (id.indexOf("t:") === 0) run(tab, "translate", id.substring(2), info.selectionText, info.frameId);
});

B.commands.onCommand.addListener(function (cmd) {
  B.tabs.query({ active: true, currentWindow: true }, function (tabs) {
    var tab = tabs && tabs[0];
    if (cmd === "fix-grammar") run(tab, "grammar", "");
    else if (cmd === "translate") run(tab, "translate", "");
  });
});

// The toolbar button opens the panel: side panel in Chrome/Edge, sidebar in Firefox.
if (IS_FIREFOX) {
  var act = browser.browserAction || browser.action;
  if (act) act.onClicked.addListener(function () { try { browser.sidebarAction.toggle(); } catch (e) { } });
} else if (B.sidePanel && B.sidePanel.setPanelBehavior) {
  try { B.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }); } catch (e) { }
}

// Messages from the panel.
B.runtime.onMessage.addListener(function (msg, sender, reply) {
  if (msg && msg.t === "api") { api(msg.path, msg.body).then(reply); return true; }
  if (msg && msg.t === "replaceInTab") {
    B.tabs.query({ active: true, currentWindow: true }, function (tabs) { if (tabs[0]) send(tabs[0].id, { t: "replace", text: msg.text, original: msg.original, label: msg.label, ai: msg.ai }).then(reply); else reply(null); });
    return true;
  }
  if (msg && msg.t === "fromTab") {
    B.tabs.query({ active: true, currentWindow: true }, function (tabs) { if (tabs[0]) send(tabs[0].id, msg.msg).then(function (r) { reply(r ? Object.assign({ url: tabs[0].url, title: tabs[0].title }, r) : { url: tabs[0].url, title: tabs[0].title }); }); else reply(null); });
    return true;
  }
});

// Nova rewrites this add-on's folder when it updates; Chrome/Edge then reload it from the folder.
function checkVersion() {
  api("ping").then(function (r) {
    var mine = B.runtime.getManifest().version;
    if (r && r.addon && r.addon !== mine && !IS_FIREFOX) { try { B.runtime.reload(); } catch (e) { } }
  });
}

B.runtime.onInstalled.addListener(function () { menus(); });
if (B.runtime.onStartup) B.runtime.onStartup.addListener(function () { menus(); checkVersion(); });
if (B.alarms) { B.alarms.create("nova-version", { periodInMinutes: 30 }); B.alarms.onAlarm.addListener(function (a) { if (a.name === "nova-version") checkVersion(); }); }
if (IS_FIREFOX) menus();
