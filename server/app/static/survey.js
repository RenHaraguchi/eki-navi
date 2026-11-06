"use strict";

// 現地で看板の位置と向きを地図に記録し、signs.json の形で書き出す。
// 座標は app.js と同じく x = 東、z = 北（メートル）。SVG は下向きが正なので y = -z で描く。
const $ = (id) => document.getElementById(id);
const SVG_NS = "http://www.w3.org/2000/svg";
const FLOOR = { ordinal: 2, label: "2階", code: "2F" };
const STORAGE_KEY = "ekinavi-survey-v1";
const TAP_TOLERANCE_PX = 8;
const MAP_MARGIN_METERS = 15;
const MIN_VIEW_METERS = 8;
const MAX_VIEW_METERS = 1200;
const FOCUS_RADIUS_METERS = 200;

const state = {
  view: { x: 0, y: 0, w: 100, h: 100 },
  sign: null, // {x, z}
  viewer: null, // {x, z}
  gates: [],
  knownSigns: [],
  records: loadRecords(),
};

function loadRecords() {
  try {
    const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) || "[]");
    return Array.isArray(saved) ? saved : [];
  } catch {
    return [];
  }
}

function saveRecords() {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state.records));
  } catch {
    // 保存できなくても、この画面を閉じるまでは使える
  }
}

async function api(path) {
  const response = await fetch(path);
  const data = await response.json();
  if (!response.ok) {
    throw new Error(data.error ? data.error.message : "読み込みに失敗しました");
  }
  return data;
}

function svgElement(name, attributes) {
  const element = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes)) {
    element.setAttribute(key, value);
  }
  return element;
}

function toPath(points) {
  return points.map(([x, z], i) => `${i === 0 ? "M" : "L"}${x.toFixed(2)} ${(-z).toFixed(2)}`).join(" ");
}

function round1(value) {
  return Math.round(value * 10) / 10;
}

function normalizeDeg(deg) {
  const value = ((deg % 360) + 360) % 360;
  return round1(value) === 360 ? 0 : round1(value);
}

// 看板から見ている人への方位。看板の表はこちらを向いている
function bearing(from, to) {
  return normalizeDeg((Math.atan2(to.x - from.x, to.z - from.z) * 180) / Math.PI);
}

// ---- 地図の表示 ----

const map = $("survey-map");
const networkLayer = svgElement("g", {});
const overlayLayer = svgElement("g", {});
map.append(networkLayer, overlayLayer);

function applyView() {
  const { x, y, w, h } = state.view;
  map.setAttribute("viewBox", `${x} ${y} ${w} ${h}`);
  drawOverlay();
  $("scale").textContent = `画面の幅はおよそ ${Math.round(w)} m です。拡大するほど正確に置けます。`;
}

function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
}

// 2階には 800 m 離れた西武新宿駅の改札もあるので、改札が集まっている所の周りだけを最初に映す
function focusPoints(lines, gates) {
  const points = lines.flat();
  if (gates.length === 0) {
    return points;
  }
  const cx = median(gates.map((gate) => gate.x));
  const cz = median(gates.map((gate) => gate.z));
  const near = points.filter(([x, z]) => Math.hypot(x - cx, z - cz) <= FOCUS_RADIUS_METERS);
  return near.length > 0 ? near : points;
}

function fitTo(points) {
  const xs = points.map(([x]) => x);
  const zs = points.map(([, z]) => z);
  const minX = Math.min(...xs) - MAP_MARGIN_METERS;
  const maxX = Math.max(...xs) + MAP_MARGIN_METERS;
  const minZ = Math.min(...zs) - MAP_MARGIN_METERS;
  const maxZ = Math.max(...zs) + MAP_MARGIN_METERS;
  // 表示枠の縦横比にそろえて、地図全体が入るようにする
  const rect = map.getBoundingClientRect();
  const aspect = rect.height / rect.width || 1;
  let w = maxX - minX;
  let h = maxZ - minZ;
  if (h / w > aspect) {
    w = h / aspect;
  } else {
    h = w * aspect;
  }
  state.view = { x: (minX + maxX) / 2 - w / 2, y: -(minZ + maxZ) / 2 - h / 2, w, h };
}

function zoom(factor, centerX, centerY) {
  const { x, y, w, h } = state.view;
  const nextW = Math.min(MAX_VIEW_METERS, Math.max(MIN_VIEW_METERS, w * factor));
  const applied = nextW / w;
  const cx = centerX ?? x + w / 2;
  const cy = centerY ?? y + h / 2;
  state.view = { x: cx - (cx - x) * applied, y: cy - (cy - y) * applied, w: w * applied, h: h * applied };
  applyView();
}

function drawTriangle(layer, point, azimuthDeg, size, className) {
  const rad = (azimuthDeg * Math.PI) / 180;
  const dx = Math.sin(rad);
  const dz = Math.cos(rad);
  const tip = [point.x + dx * size, point.z + dz * size];
  const left = [point.x - dz * size * 0.5, point.z + dx * size * 0.5];
  const right = [point.x + dz * size * 0.5, point.z - dx * size * 0.5];
  const corners = [tip, left, right].map(([x, z]) => `${x.toFixed(2)},${(-z).toFixed(2)}`).join(" ");
  layer.append(svgElement("polygon", { points: corners, class: className }));
}

function drawOverlay() {
  overlayLayer.replaceChildren();
  const unit = state.view.w / 40;
  for (const gate of state.gates) {
    overlayLayer.append(svgElement("circle", { cx: gate.x, cy: -gate.z, r: unit * 0.35, class: "gate-dot" }));
    const label = svgElement("text", { x: gate.x + unit * 0.6, y: -gate.z + unit * 0.35, class: "gate-label", "font-size": unit * 1.1 });
    label.textContent = `${gate.operator} ${gate.name}`;
    overlayLayer.append(label);
  }
  for (const sign of state.knownSigns) {
    drawTriangle(overlayLayer, sign, sign.azimuthDeg, unit * 1.2, "known-sign");
  }
  for (const record of state.records) {
    drawTriangle(overlayLayer, record, record.azimuthDeg, unit * 1.2, "recorded-sign");
  }
  if (state.sign && state.viewer) {
    overlayLayer.append(svgElement("path", { d: toPath([[state.sign.x, state.sign.z], [state.viewer.x, state.viewer.z]]), class: "sight", "stroke-width": unit * 0.15 }));
  }
  if (state.viewer) {
    overlayLayer.append(svgElement("circle", { cx: state.viewer.x, cy: -state.viewer.z, r: unit * 0.6, class: "viewer" }));
  }
  if (state.sign) {
    const azimuth = Number($("azimuth").value);
    if ($("azimuth").value !== "" && Number.isFinite(azimuth)) {
      drawTriangle(overlayLayer, state.sign, azimuth, unit * 2, "picked-sign");
    }
    overlayLayer.append(svgElement("circle", { cx: state.sign.x, cy: -state.sign.z, r: unit * 0.5, class: "picked-dot" }));
  }
}

function describePick() {
  const picked = $("picked");
  picked.classList.remove("error");
  if (!state.sign) {
    picked.textContent = "まだ位置を決めていません。";
    return;
  }
  const where = `看板の位置: 東 ${state.sign.x} m、北 ${state.sign.z} m`;
  const azimuth = $("azimuth").value;
  picked.textContent = azimuth === "" ? `${where}。次に ② で表を見ている位置をタップしてください。` : `${where}、表の向き ${azimuth} 度`;
}

function onTap(clientX, clientY) {
  const matrix = map.getScreenCTM();
  if (!matrix) {
    return;
  }
  const point = new DOMPoint(clientX, clientY).matrixTransform(matrix.inverse());
  const tapped = { x: round1(point.x), z: round1(-point.y) };
  const mode = document.querySelector('input[name="mode"]:checked').value;
  if (mode === "sign") {
    state.sign = tapped;
    if (state.viewer) {
      $("azimuth").value = bearing(state.sign, state.viewer);
    }
    // 続けて向きを決めてもらう
    document.querySelector('input[name="mode"][value="viewer"]').checked = true;
  } else {
    state.viewer = tapped;
    if (state.sign) {
      $("azimuth").value = bearing(state.sign, state.viewer);
    }
  }
  describePick();
  drawOverlay();
}

// ドラッグで移動、2本指で拡大縮小、動かさずに離したらタップとして扱う
const pointers = new Map();
let gesture = null;

function svgUnitsPerPixel() {
  return state.view.w / map.getBoundingClientRect().width;
}

map.addEventListener("pointerdown", (event) => {
  map.setPointerCapture(event.pointerId);
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
  gesture = { moved: pointers.size > 1, start: { x: event.clientX, y: event.clientY }, pinch: null };
});

map.addEventListener("pointermove", (event) => {
  const previous = pointers.get(event.pointerId);
  if (!previous || !gesture) {
    return;
  }
  const current = { x: event.clientX, y: event.clientY };
  if (pointers.size === 1) {
    if (Math.hypot(current.x - gesture.start.x, current.y - gesture.start.y) > TAP_TOLERANCE_PX) {
      gesture.moved = true;
    }
    if (gesture.moved) {
      const unit = svgUnitsPerPixel();
      state.view.x -= (current.x - previous.x) * unit;
      state.view.y -= (current.y - previous.y) * unit;
      applyView();
    }
  } else if (pointers.size === 2) {
    const [a, b] = [...pointers.entries()].map(([id, p]) => (id === event.pointerId ? current : p));
    const [pa, pb] = [...pointers.values()];
    const before = Math.hypot(pa.x - pb.x, pa.y - pb.y);
    const after = Math.hypot(a.x - b.x, a.y - b.y);
    if (before > 0 && after > 0) {
      const rect = map.getBoundingClientRect();
      const unit = svgUnitsPerPixel();
      const cx = state.view.x + ((a.x + b.x) / 2 - rect.left) * unit;
      const cy = state.view.y + ((a.y + b.y) / 2 - rect.top) * unit;
      zoom(before / after, cx, cy);
    }
  }
  pointers.set(event.pointerId, current);
});

function endPointer(event) {
  if (!pointers.has(event.pointerId)) {
    return;
  }
  pointers.delete(event.pointerId);
  if (gesture && !gesture.moved && pointers.size === 0 && event.type === "pointerup") {
    onTap(event.clientX, event.clientY);
  }
  if (pointers.size === 0) {
    gesture = null;
  }
}

map.addEventListener("pointerup", endPointer);
map.addEventListener("pointercancel", endPointer);
map.addEventListener(
  "wheel",
  (event) => {
    event.preventDefault();
    const rect = map.getBoundingClientRect();
    const unit = svgUnitsPerPixel();
    zoom(event.deltaY > 0 ? 1.2 : 1 / 1.2, state.view.x + (event.clientX - rect.left) * unit, state.view.y + (event.clientY - rect.top) * unit);
  },
  { passive: false },
);
$("zoom-in").addEventListener("click", () => zoom(1 / 1.5));
$("zoom-out").addEventListener("click", () => zoom(1.5));
$("azimuth").addEventListener("input", () => {
  // 手で向きを直したら、② の位置は向きと合わなくなるので消す
  state.viewer = null;
  describePick();
  drawOverlay();
});

// ---- 記録 ----

function suggestId() {
  const used = new Set(state.records.map((record) => record.id));
  for (let n = 1; ; n += 1) {
    const base = `S-${FLOOR.code}-H${String(n).padStart(2, "0")}`;
    if (![base, `${base}-A`, `${base}-B`].some((id) => used.has(id))) {
      return base;
    }
  }
}

function imageNameOf(id) {
  return `sign_${id.replace(/^S-/i, "").toLowerCase().replace(/-/g, "_")}`;
}

function buildRecords() {
  const id = $("sign-id").value.trim();
  const name = $("name").value.trim();
  const note = $("note").value.trim();
  const base = {
    x: state.sign.x,
    z: state.sign.z,
    ordinal: FLOOR.ordinal,
    widthMeters: Number($("width").value),
    heightMeters: Number($("height").value),
    centerHeightMeters: Number($("center-height").value),
    mount: $("mount").value,
    correctionYawDeg: 0,
    correctionX: 0,
    correctionZ: 0,
    confidence: $("confidence").value,
  };
  const azimuth = normalizeDeg(Number($("azimuth").value));
  if (!$("both-faces").checked) {
    return [{ id, name, imageName: imageNameOf(id), ...base, azimuthDeg: azimuth, note }];
  }
  const faces = [
    { suffix: "A", label: "表", azimuthDeg: azimuth, extra: "" },
    { suffix: "B", label: "裏", azimuthDeg: normalizeDeg(azimuth + 180), extra: "裏面。表とは別に正面から撮った写真を使う" },
  ];
  return faces.map((face) => {
    const faceId = `${id}-${face.suffix}`;
    return {
      id: faceId,
      name: `${name}（${face.label}）`,
      imageName: imageNameOf(faceId),
      ...base,
      azimuthDeg: face.azimuthDeg,
      note: [note, face.extra].filter(Boolean).join("。"),
    };
  });
}

// signs.schema.json のキーの順にそろえる
function orderedRecord(record) {
  const keys = ["id", "name", "imageName", "x", "z", "ordinal", "azimuthDeg", "widthMeters", "heightMeters", "centerHeightMeters", "mount", "correctionYawDeg", "correctionX", "correctionZ", "confidence", "note"];
  return Object.fromEntries(keys.map((key) => [key, record[key]]));
}

function renderRecords() {
  const list = $("records");
  list.replaceChildren();
  for (const record of state.records) {
    const item = document.createElement("li");
    const text = document.createElement("span");
    text.textContent = `${record.id} ${record.name}（東 ${record.x} m、北 ${record.z} m、向き ${record.azimuthDeg} 度、幅 ${record.widthMeters} m）`;
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "secondary";
    remove.textContent = "消す";
    remove.addEventListener("click", () => {
      state.records = state.records.filter((other) => other !== record);
      saveRecords();
      renderRecords();
      drawOverlay();
    });
    item.append(text, remove);
    list.append(item);
  }
  if (state.records.length === 0) {
    const item = document.createElement("li");
    item.className = "hint";
    item.textContent = "まだありません。";
    list.append(item);
  }
  $("output").value = state.records.length ? JSON.stringify(state.records.map(orderedRecord), null, 2) : "";
}

function showStatus(element, text, isError) {
  element.textContent = text;
  element.classList.toggle("error", Boolean(isError));
}

$("sign-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const status = $("form-status");
  if (!state.sign) {
    showStatus(status, "地図で ① 看板の位置をタップしてください。", true);
    return;
  }
  if ($("azimuth").value === "") {
    showStatus(status, "地図で ② 表を見ている位置をタップするか、向きを数字で入れてください。", true);
    return;
  }
  const added = buildRecords();
  const used = new Set(state.records.map((record) => record.id));
  if (added.some((record) => used.has(record.id))) {
    showStatus(status, "同じ ID の記録があります。ID を変えてください。", true);
    return;
  }
  state.records.push(...added);
  saveRecords();
  showStatus(status, `${added.map((record) => record.id).join("、")} を記録しました。`, false);
  // 次の看板に備えて、位置と向き、名前を空にする。大きさや高さは似ていることが多いので残す
  state.sign = null;
  state.viewer = null;
  $("azimuth").value = "";
  $("name").value = "";
  $("note").value = "";
  $("sign-id").value = suggestId();
  document.querySelector('input[name="mode"][value="sign"]').checked = true;
  describePick();
  renderRecords();
  drawOverlay();
});

$("copy").addEventListener("click", async () => {
  const output = $("output");
  const status = $("copy-status");
  if (!output.value) {
    showStatus(status, "まだ記録がありません。", true);
    return;
  }
  try {
    await navigator.clipboard.writeText(output.value);
  } catch {
    // http で開いたスマホなど、クリップボードの API が使えないとき
    output.focus();
    output.select();
    if (!document.execCommand("copy")) {
      showStatus(status, "自動でコピーできませんでした。選択された文字を長押ししてコピーしてください。", true);
      return;
    }
  }
  showStatus(status, "コピーしました。", false);
});

$("clear").addEventListener("click", () => {
  if (state.records.length === 0 || !window.confirm("記録した看板を全部消しますか？")) {
    return;
  }
  state.records = [];
  saveRecords();
  renderRecords();
  drawOverlay();
});

async function main() {
  $("floor-label").textContent = FLOOR.label;
  $("sign-id").value = suggestId();
  renderRecords();
  const [floorMap, station, site] = await Promise.all([
    api(`/v1/map?ordinal=${FLOOR.ordinal}`),
    api("/v1/station"),
    api("/v1/site"),
  ]);
  $("attribution").textContent = site.attribution;
  state.gates = floorMap.gates;
  state.knownSigns = station.signs.filter((sign) => sign.ordinal === FLOOR.ordinal);
  for (const line of floorMap.lines) {
    networkLayer.append(svgElement("path", { d: toPath(line), class: "network", "stroke-width": 3, "vector-effect": "non-scaling-stroke" }));
  }
  fitTo(focusPoints(floorMap.lines, floorMap.gates));
  applyView();
}

main().catch((error) => showStatus($("picked"), error.message, true));
