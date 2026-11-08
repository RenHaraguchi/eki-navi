"use strict";

// 現地で看板の位置と向きを地図に記録し、signs.json の形で書き出す。
// 座標は app.js と同じく x = 東、z = 北（メートル）。SVG は下向きが正なので y = -z で描く。
// 画面は 地図 → 写真 → 一覧 の3つ。地図では看板の印を下から出し、ドラッグで位置、つまみで向きを決める。
const $ = (id) => document.getElementById(id);
const SVG_NS = "http://www.w3.org/2000/svg";
// 階は ?floor=B1 のように選ぶ。地下1階は、構内図から作った JR の改札の中の画像を地図の下に敷く。
// 画像の位置は左上の角（x, z）と幅・高さ（メートル。構内図と eki-navi の共通座標は同じ）
const FLOORS = {
  "2F": { ordinal: 2, label: "2階", code: "2F", size: "hanging-large", image: null },
  B1: {
    ordinal: -1,
    label: "地下1階",
    code: "B1",
    size: "hanging-thin",
    image: { href: "/static/survey_b1.png", left: -43.113, top: 129.2, width: 148.026, height: 127.1 },
  },
};
// 看板の大きさは入力させず、よくある大きさから選ぶ。値は signs.json にある看板の実測値。
// heightMeters は、アプリが看板までの距離を求めるのに使う（縦の長さが違うと距離がその割合でずれる）
const SIZES = [
  { key: "hanging-thin", width: 3.0, height: 0.3, centerHeight: 2.7, mount: "hanging", label: "吊り下げ（細長）" }, // S-B1-H02
  { key: "hanging-large", width: 6.5, height: 0.6, centerHeight: 3.5, mount: "hanging", label: "吊り下げ（大）" }, // S-2F-R01-A
];
// 好きな大きさ。幅と高さだけを入れてもらい、取り付けと床から中心までは吊り下げ（細長）と同じにする
const CUSTOM_SIZE = { key: "custom", centerHeight: 2.7, mount: "hanging", label: "好きな大きさ" };
const FLOOR_CODE = (new URLSearchParams(location.search).get("floor") || "B1").toUpperCase();
const FLOOR = FLOORS[FLOOR_CODE] || FLOORS.B1;
const STORAGE_KEY = `ekinavi-survey-v1${FLOOR.code === "2F" ? "" : "-" + FLOOR.code}`;
const TAP_TOLERANCE_PX = 8;
const MAP_MARGIN_METERS = 15;
const FOCUS_RADIUS_METERS = 200;
// 看板の印の大きさ（画面の幅に対する割合）。板は小さく描き、指でつかむ範囲だけは広く取る
// 印は地図と一緒に拡大縮小する。大きさは、地図全体を映したときの画面のピクセルで決める
const MARKER_SCALE = 1 / 45; // 板の幅（地図全体を映したときの画面の幅に対する割合）
const MARKER_HIT_PX = 22; // 板とつまみをつかめる最小の半径（今の画面のピクセル。縮小しても指でつかめるように）
const MARKER_KNOB_PX = 40; // 板の真ん中からつまみまで（地図全体を映したときの画面のピクセル）
const MIN_VIEW_METERS = 8; // いちばん拡大したときの画面の幅
const MARKER_RISE_MS = 380;
const DEFAULT_AZIMUTH = 180; // 印を出したときの表の向き（南）
// 写真は長辺をこの大きさに縮めて保存する
const PHOTO_MAX_PX = 1600;
const PHOTO_QUALITY = 0.85;

const state = {
  view: { x: 0, y: 0, w: 100, h: 100 },
  home: null, // 地図全体を映したときの view。これより縮小しない
  sign: null, // {x, z}
  azimuth: DEFAULT_AZIMUTH,
  gates: [],
  knownSigns: [],
  records: [], // 開くたびに空から始める
};

// ZIP に入る signs.json の中身を、保存の画面に表示する（読むだけ）
function renderOutput() {
  $("output").textContent = JSON.stringify({ signs: state.records.map(orderedRecord) }, null, 2);
}

function saveRecords() {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state.records));
  } catch {
    // 保存できなくても、この画面を閉じるまでは使える
  }
}

async function api(path, options) {
  const response = await fetch(path, options);
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

function showStatus(element, text, isError) {
  element.textContent = text;
  element.classList.toggle("error", Boolean(isError));
}

// ---- 画面の切り替え ----

const SCREENS = ["map", "photo", "list"];

// 今の画面より前の画面は左へ、後の画面は右へ片付ける。一覧へは地図から直接も行くので、写真の画面は右へ戻す
let currentScreen = "map";

function showScreen(name, push = true) {
  currentScreen = name;
  const index = SCREENS.indexOf(name);
  SCREENS.forEach((other, i) => {
    const element = $(`screen-${other}`);
    const isBehind = i < index && !(name === "list" && other === "photo");
    element.hidden = other !== name && !isBehind;
    element.classList.toggle("behind", isBehind);
  });
  if (push) {
    history.pushState({ screen: name }, "", location.search);
  }
  if (name === "map") {
    requestAnimationFrame(applyView);
  }
}

// 保存の画面から地図へ戻るときは「リセットしていい？」と聞く（← のボタンでも、端末の戻る操作でも）。
// 端末の戻る操作は先に画面が移るので、リセットしなかったら保存の画面に戻す
let skipAskOnBack = false;
window.addEventListener("popstate", async (event) => {
  const leaving = currentScreen;
  const target = event.state?.screen || "map";
  if (leaving === "list" && target === "map" && !skipAskOnBack && !(await confirmReset())) {
    history.pushState({ screen: "list" }, "", location.search);
    return;
  }
  skipAskOnBack = false;
  showScreen(target, false);
});
document.querySelectorAll(".back").forEach((button) => button.addEventListener("click", () => history.back()));

// ---- 地図の表示 ----

const map = $("survey-map");
const imageLayer = svgElement("g", {});
const networkLayer = svgElement("g", {});
const overlayLayer = svgElement("g", {});
const markerLayer = svgElement("g", {});
map.append(imageLayer, networkLayer, overlayLayer, markerLayer);

function applyView() {
  const { x, y, w, h } = state.view;
  map.setAttribute("viewBox", `${x} ${y} ${w} ${h}`);
  drawOverlay();
  drawMarker();
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

function fitTo(points, margin = MAP_MARGIN_METERS) {
  const xs = points.map(([x]) => x);
  const zs = points.map(([, z]) => z);
  const minX = Math.min(...xs) - margin;
  const maxX = Math.max(...xs) + margin;
  const minZ = Math.min(...zs) - margin;
  const maxZ = Math.max(...zs) + margin;
  // 表示枠の縦横比にそろえて、全体が入るようにする
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

// 画像のある階では、画像の範囲の中だけを扱う
function insideImage(point) {
  if (!FLOOR.image) {
    return true;
  }
  const { left, top, width, height } = FLOOR.image;
  return point.x >= left && point.x <= left + width && point.z <= top && point.z >= top - height;
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

// 画像全体が画面に収まる大きさにして、真ん中に置く。画面より画像のほうが横長なら上下に、縦長なら左右に余白が出る
function fitImage() {
  const { left, top, width, height } = FLOOR.image;
  const rect = map.getBoundingClientRect();
  const aspect = rect.width / rect.height || 1;
  const w = aspect > width / height ? height * aspect : width;
  const h = w / aspect;
  state.view = { x: left + width / 2 - w / 2, y: -top + height / 2 - h / 2, w, h };
}

// 地図を動かせるのは画像の範囲の中だけ（画像の外は見せない）
function clampView() {
  if (!FLOOR.image) {
    return;
  }
  const { left, top, width, height } = FLOOR.image;
  // 画面のほうが画像より広い向きは、画像を真ん中に置く（動かさない）
  state.view.x = state.view.w >= width ? left + width / 2 - state.view.w / 2 : clamp(state.view.x, left, left + width - state.view.w);
  state.view.y = state.view.h >= height ? -top + height / 2 - state.view.h / 2 : clamp(state.view.y, -top, -top + height - state.view.h);
}

function drawOverlay() {
  overlayLayer.replaceChildren();
  const unit = state.view.w / 40;
  for (const gate of state.gates) {
    overlayLayer.append(svgElement("circle", { cx: gate.x, cy: -gate.z, r: unit * 0.35, class: "gate-dot" }));
    // 画像のある階は画像で場所が分かるので、改札の名前は出さない
    if (!FLOOR.image) {
      const label = svgElement("text", { x: gate.x + unit * 0.6, y: -gate.z + unit * 0.35, class: "gate-label", "font-size": unit * 1.1 });
      label.textContent = `${gate.operator} ${gate.name}`;
      overlayLayer.append(label);
    }
  }
  // 記録した看板と signs.json にもうある看板は、置いたときと同じ板の形で残す（既存のものは薄く）
  for (const sign of state.knownSigns.filter(insideImage)) {
    drawSaved(sign, "saved known");
  }
  for (const record of state.records) {
    drawSaved(record, "saved");
  }
}

// 印の大きさ。地図全体を映したときの画面のピクセルで決め、地図と一緒に拡大縮小する
function markerSizes() {
  const home = state.home || state.view;
  const bp = home.w / (map.getBoundingClientRect().width || 1); // 地図全体を映したときの 1px が何 m か
  return { s: home.w * MARKER_SCALE, bp, px: svgUnitsPerPixel() || bp, knob: MARKER_KNOB_PX * bp };
}

// 記録した看板: 板と、表の向きへの短い線（表と裏を記録したら、線は両側に出る）
function drawSaved(sign, className) {
  const { s, bp } = markerSizes();
  const group = svgElement("g", { class: className, transform: `translate(${sign.x} ${-sign.z}) rotate(${sign.azimuthDeg})` });
  group.append(
    svgElement("path", { d: `M0 ${-s * 0.12} V${-s * 0.55}`, class: "tick", "stroke-width": 1.5 * bp }),
    svgElement("rect", { x: -s / 2, y: -s * 0.12, width: s, height: s * 0.24, rx: s * 0.05, class: "board" }),
  );
  overlayLayer.append(group);
}

// 看板の印: 板（横長の四角）と、表の向きを示す点線と、向きを変えるつまみ。
// 北を上にした形を作り、表の向き（北=0 時計回り）だけ回す。SVG は y が下向きなので回す向きはそのまま
function drawMarker() {
  markerLayer.replaceChildren();
  if (!state.sign) {
    return;
  }
  const { s, bp, px, knob } = markerSizes();
  const group = svgElement("g", {
    class: "marker",
    transform: `translate(${state.sign.x} ${-state.sign.z}) rotate(${state.azimuth})`,
  });
  group.append(
    svgElement("circle", { cx: 0, cy: 0, r: Math.max(MARKER_HIT_PX * px, s * 0.6), class: "hit", "data-handle": "move" }),
    svgElement("path", { d: `M0 0 V${-knob + 6 * bp}`, class: "face", "stroke-width": 1.5 * bp, "stroke-dasharray": `${3 * bp} ${2.5 * bp}` }),
    svgElement("rect", { x: -s / 2, y: -s * 0.12, width: s, height: s * 0.24, rx: s * 0.05, class: "board", "stroke-width": bp, "data-handle": "move" }),
    svgElement("circle", { cx: 0, cy: -knob, r: Math.max(MARKER_HIT_PX * px, 10 * bp), class: "hit", "data-handle": "rotate" }),
    svgElement("circle", { cx: 0, cy: -knob, r: 6 * bp, class: "knob", "stroke-width": 2 * bp, "data-handle": "rotate" }),
  );
  markerLayer.append(group);
}

function toMapPoint(clientX, clientY) {
  const matrix = map.getScreenCTM();
  if (!matrix) {
    return null;
  }
  return new DOMPoint(clientX, clientY).matrixTransform(matrix.inverse());
}

function setSign(x, z) {
  if (FLOOR.image) {
    const { left, top, width, height } = FLOOR.image;
    x = clamp(x, left, left + width);
    z = clamp(z, top - height, top);
  }
  state.sign = { x: round1(x), z: round1(z) };
  $("to-photo").disabled = false;
  $("add-sign").classList.add("placed");
  drawMarker();
}

// 看板の印を、画面の下から地図の真ん中まで上げる
function riseMarker() {
  const { x, y, w, h } = state.view;
  const from = { x: x + w / 2, z: -(y + h) };
  const to = { x: x + w / 2, z: -(y + h / 2) };
  state.azimuth = DEFAULT_AZIMUTH;
  const started = performance.now();
  const step = (now) => {
    const t = Math.min(1, (now - started) / MARKER_RISE_MS);
    const ease = 1 - (1 - t) ** 3;
    setSign(from.x + (to.x - from.x) * ease, from.z + (to.z - from.z) * ease);
    if (t < 1) {
      requestAnimationFrame(step);
    }
  };
  requestAnimationFrame(step);
}

$("add-sign").addEventListener("click", riseMarker);

// ドラッグ: 印の板なら位置、つまみなら向き、ほかは地図の移動。2本指とホイールで拡大縮小（印も一緒に）。
// 動かさずに離したら、そこへ印を置く
const pointers = new Map();
let gesture = null;

function svgUnitsPerPixel() {
  return state.view.w / map.getBoundingClientRect().width;
}

map.addEventListener("pointerdown", (event) => {
  map.setPointerCapture(event.pointerId);
  pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
  const handle = pointers.size === 1 && state.sign ? event.target.closest?.("[data-handle]")?.dataset.handle || null : null;
  let offset = null;
  if (handle === "move") {
    const p = toMapPoint(event.clientX, event.clientY);
    offset = { x: state.sign.x - p.x, z: state.sign.z + p.y };
  }
  gesture = { moved: pointers.size > 1, start: { x: event.clientX, y: event.clientY }, handle, offset };
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
      const p = toMapPoint(current.x, current.y);
      if (gesture.handle === "move" && p) {
        setSign(p.x + gesture.offset.x, -p.y + gesture.offset.z);
        markerLayer.firstChild?.classList.add("dragging");
      } else if (gesture.handle === "rotate" && p) {
        state.azimuth = normalizeDeg((Math.atan2(p.x - state.sign.x, -(p.y + state.sign.z)) * 180) / Math.PI);
        drawMarker();
      } else {
        const unit = svgUnitsPerPixel();
        state.view.x -= (current.x - previous.x) * unit;
        state.view.y -= (current.y - previous.y) * unit;
        clampView();
        applyView();
      }
    }
  } else if (pointers.size === 2) {
    const [a, b] = [...pointers.entries()].map(([id, p]) => (id === event.pointerId ? current : p));
    const [pa, pb] = [...pointers.values()];
    const before = Math.hypot(pa.x - pb.x, pa.y - pb.y);
    const after = Math.hypot(a.x - b.x, a.y - b.y);
    if (before > 0 && after > 0) {
      const p = toMapPoint((a.x + b.x) / 2, (a.y + b.y) / 2);
      if (p) {
        zoom(before / after, p.x, p.y);
      }
    }
  }
  pointers.set(event.pointerId, current);
});

function endPointer(event) {
  if (!pointers.has(event.pointerId)) {
    return;
  }
  pointers.delete(event.pointerId);
  if (gesture && !gesture.moved && !gesture.handle && pointers.size === 0 && event.type === "pointerup") {
    const p = toMapPoint(event.clientX, event.clientY);
    if (p) {
      setSign(p.x, -p.y);
    }
  }
  if (pointers.size === 0) {
    gesture = null;
    drawMarker();
  }
}

map.addEventListener("pointerup", endPointer);
map.addEventListener("pointercancel", endPointer);
// 地図全体より小さくはしない。いちばん拡大して画面の幅が MIN_VIEW_METERS まで
function zoom(factor, cx, cy) {
  const { x, y, w, h } = state.view;
  const maxW = state.home ? state.home.w : w;
  const nextW = clamp(w * factor, Math.min(MIN_VIEW_METERS, maxW), maxW);
  const k = nextW / w;
  state.view = { x: cx - (cx - x) * k, y: cy - (cy - y) * k, w: w * k, h: h * k };
  clampView();
  applyView();
}

// ホイール（トラックパッドのピンチを含む）で拡大縮小する。ページごと拡大されないようにする
map.addEventListener(
  "wheel",
  (event) => {
    event.preventDefault();
    const p = toMapPoint(event.clientX, event.clientY);
    if (p) {
      zoom(Math.exp(event.deltaY * (event.ctrlKey ? 0.01 : 0.002)), p.x, p.y);
    }
  },
  { passive: false },
);
// 窓の大きさが変わったら、画像全体が収まるように合わせ直す
window.addEventListener("resize", () => {
  if (FLOOR.image) {
    fitImage();
  }
  state.home = { ...state.view };
  applyView();
});

$("to-photo").addEventListener("click", () => {
  $("azimuth").value = state.azimuth;
  showScreen("photo");
});

// ---- 写真 ----

function loadImage(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const image = new Image();
      image.onload = () => resolve(image);
      image.onerror = () => reject(new Error("写真を開けませんでした"));
      image.src = reader.result;
    };
    reader.onerror = () => reject(new Error("写真を読み込めませんでした"));
    reader.readAsDataURL(file);
  });
}

// 長辺を PHOTO_MAX_PX に縮めた JPEG の data URL にする
async function shrinkPhoto(file) {
  const image = await loadImage(file);
  const scale = Math.min(1, PHOTO_MAX_PX / Math.max(image.naturalWidth, image.naturalHeight));
  const canvas = document.createElement("canvas");
  canvas.width = Math.round(image.naturalWidth * scale);
  canvas.height = Math.round(image.naturalHeight * scale);
  canvas.getContext("2d").drawImage(image, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL("image/jpeg", PHOTO_QUALITY);
}

// ---- 写真の保存（IndexedDB） ----
// 写真は大きいので localStorage（5MB ほど）ではなく IndexedDB に、看板の imageName ごとに入れる
const PHOTO_DB = "ekinavi-survey-photos";
const PHOTO_STORE = "photos";

function openPhotoDb() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(PHOTO_DB, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(PHOTO_STORE);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function photoStore(mode, action) {
  const db = await openPhotoDb();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(PHOTO_STORE, mode);
    const result = action(tx.objectStore(PHOTO_STORE));
    tx.oncomplete = () => resolve(result?.result);
    tx.onerror = () => reject(tx.error);
  });
}

const putPhoto = (key, dataUrl) => photoStore("readwrite", (store) => store.put(dataUrl, key));
const deletePhoto = (key) => photoStore("readwrite", (store) => store.delete(key));
const getPhoto = (key) => photoStore("readonly", (store) => store.get(key));
const clearPhotos = () => photoStore("readwrite", (store) => store.clear());

const photoData = { A: "", B: "" }; // 縮めた写真（data URL）。記録するときに IndexedDB に入れる
const photoNames = { A: "", B: "" }; // 写真のファイル名（メモに残す。あとで参照画像を作るときに探せるように）

function clearFace(face) {
  const box = document.querySelector(`.face[data-face="${face}"]`);
  box.querySelector(".photo").value = "";
  box.querySelector(".preview").hidden = true;
  box.querySelector(".camera-icon").style.display = "";
  box.querySelector(".camera").classList.remove("has-photo");
  showStatus(box.querySelector(".status"), "", false);
  photoNames[face] = "";
  photoData[face] = "";
}

for (const box of document.querySelectorAll(".face")) {
  const face = box.dataset.face;
  const input = box.querySelector(".photo");
  const status = box.querySelector(".status");
  input.addEventListener("change", async () => {
    const file = input.files[0];
    if (!file) {
      return;
    }
    photoNames[face] = file.name;
    showStatus(status, "", false);
    let dataUrl;
    try {
      dataUrl = await shrinkPhoto(file);
    } catch (error) {
      showStatus(status, error.message, true);
      return;
    }
    const preview = box.querySelector(".preview");
    preview.src = dataUrl;
    photoData[face] = dataUrl;
    preview.hidden = false;
    box.querySelector(".camera-icon").style.display = "none";
    box.querySelector(".camera").classList.add("has-photo");
    // 板の文字（text）はここでは読まない。保存した写真から後で読む
  });
}

// ---- 大きさ ----

// 板の形を、縦横の比と大きさの比のまま小さく描いたボタンを並べる。選んだ大きさは、次の看板でもそのまま使う
function renderSizes() {
  const box = $("sizes");
  // 2つとも同じ縮尺で描き、大きさの違いが見て分かるようにする（大きいほうの板が 44px）
  const k = 44 / Math.max(...SIZES.map((item) => item.width));
  for (const size of SIZES) {
    const icon = svgElement("svg", { viewBox: "0 0 48 48", "aria-hidden": "true" });
    const w = size.width * k;
    const h = Math.max(size.height * k, 2.5);
    icon.append(svgElement("rect", { x: 24 - w / 2, y: 24 - h / 2, width: w, height: h, rx: 1 }));
    box.append(sizeButton(size.key, `${size.label} 幅 ${size.width} m、高さ ${size.height} m`, icon, `${size.width}×${size.height}`));
  }
  const icon = svgElement("svg", { viewBox: "0 0 48 48", "aria-hidden": "true" });
  icon.append(svgElement("rect", { x: 6, y: 18, width: 36, height: 12, rx: 1, class: "custom" }));
  box.append(sizeButton(CUSTOM_SIZE.key, CUSTOM_SIZE.label, icon, "□×□"));
  for (const id of ["custom-width", "custom-height"]) {
    $(id).addEventListener("input", () => {
      selectSize(CUSTOM_SIZE.key);
      showStatus($("form-status"), "", false);
    });
  }
}

function sizeButton(key, ariaLabel, icon, caption) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "size";
  button.dataset.key = key;
  button.setAttribute("role", "radio");
  button.setAttribute("aria-label", ariaLabel);
  const label = document.createElement("span");
  label.textContent = caption;
  button.append(icon, label);
  button.addEventListener("click", () => {
    selectSize(key);
    if (key === CUSTOM_SIZE.key && !$("custom-width").value) {
      $("custom-width").focus();
    }
  });
  return button;
}

function selectSize(key) {
  const custom = key === CUSTOM_SIZE.key;
  const size = custom
    ? { ...CUSTOM_SIZE, width: Number($("custom-width").value) || "", height: Number($("custom-height").value) || "" }
    : SIZES.find((item) => item.key === key) || SIZES[0];
  $("width").value = size.width;
  $("height").value = size.height;
  $("center-height").value = size.centerHeight;
  $("mount").value = size.mount;
  $("custom-size").hidden = !custom;
  for (const button of document.querySelectorAll(".size")) {
    button.setAttribute("aria-checked", String(button.dataset.key === size.key));
  }
}

// ---- 記録 ----

// 看板ごとの固有の記号。見分けやすいよう、I と O を除いたアルファベット3文字をランダムに選ぶ
const CODE_LETTERS = "ABCDEFGHJKLMNPQRSTUVWXYZ";

function randomCode() {
  const values = crypto.getRandomValues(new Uint32Array(3));
  return [...values].map((v) => CODE_LETTERS[v % CODE_LETTERS.length]).join("");
}

// ID は S-<階>-<3文字>（表と裏は末尾に -A / -B）。記録済みと signs.json の看板と重ならないものを選ぶ
function suggestId() {
  const used = new Set([...state.records, ...state.knownSigns].map((record) => record.id));
  for (;;) {
    const base = `S-${FLOOR.code}-${randomCode()}`;
    if (![base, `${base}-A`, `${base}-B`].some((id) => used.has(id))) {
      return base;
    }
  }
}

function imageNameOf(id) {
  return `sign_${id.replace(/^S-/i, "").toLowerCase().replace(/-/g, "_")}`;
}

// 表は必ず記録する。裏は写真か文字があるときだけ、反対向きの看板として記録する
function buildRecords() {
  const id = $("sign-id").value.trim();
  // 名前は階と3文字の記号（例: 地下1階の看板 NKW）。板の文字（text）は写真から後で入れる
  const name = $("name").value.trim() || `${FLOOR.label}の看板 ${id.split("-")[2] || ""}`.trim();
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
  const photoNote = (face) => (photoNames[face] ? `写真 ${photoNames[face]}` : "");
  const hasBack = Boolean(photoNames.B);
  if (!hasBack) {
    return [{ id, name, imageName: imageNameOf(id), ...base, azimuthDeg: azimuth, note: [note, photoNote("A")].filter(Boolean).join("。") }];
  }
  const faces = [
    { suffix: "A", label: "表", azimuthDeg: azimuth, extra: photoNote("A") },
    { suffix: "B", label: "裏", azimuthDeg: normalizeDeg(azimuth + 180), extra: ["裏面", photoNote("B")].filter(Boolean).join("。") },
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
  const keys = ["id", "name", "text", "imageName", "x", "z", "ordinal", "azimuthDeg", "widthMeters", "heightMeters", "centerHeightMeters", "mount", "correctionYawDeg", "correctionX", "correctionZ", "confidence", "note"];
  // 文字のない看板（裏面など）は text を書かない
  return Object.fromEntries(keys.filter((key) => key !== "text" || record.text).map((key) => [key, record[key]]));
}

function resetForNext() {
  // 次の看板に備えて、位置と写真と名前を空にする。大きさや高さは似ていることが多いので残す
  state.sign = null;
  state.azimuth = DEFAULT_AZIMUTH;
  $("to-photo").disabled = true;
  $("add-sign").classList.remove("placed");
  $("azimuth").value = "";
  $("name").value = "";
  $("note").value = "";
  clearFace("A");
  clearFace("B");
  $("sign-id").value = suggestId();
  drawMarker();
}

$("sign-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const status = $("form-status");
  if (!state.sign) {
    showStatus(status, "地図に看板を置いてください。", true);
    return;
  }
  if (!(Number($("width").value) > 0 && Number($("height").value) > 0)) {
    showStatus(status, "幅と高さを入れてください。", true);
    $("custom-width").focus();
    return;
  }
  if (!photoData.A && !photoData.B && (await ask("写真なしで記録していい？", "やめる", "記録する")) !== "yes") {
    return;
  }
  const added = buildRecords();
  const used = new Set(state.records.map((record) => record.id));
  if (added.some((record) => used.has(record.id))) {
    // ID は自動で付けるので、重なったら付け直す
    $("sign-id").value = suggestId();
    added.splice(0, added.length, ...buildRecords());
  }
  // 写真は imageName をキーにして保存する（裏の記録は -B で終わる。表だけの記録は表の写真）
  try {
    for (const record of added) {
      const data = photoData[record.id.endsWith("-B") ? "B" : "A"];
      if (data) {
        await putPhoto(record.imageName, data);
      }
    }
  } catch {
    showStatus(status, "写真を保存できませんでした。", true);
    return;
  }
  state.records.push(...added);
  saveRecords();
  renderOutput();
  showStatus(status, "", false);
  resetForNext();
  drawOverlay();
  // 写真の画面を履歴から外して一覧へ進む（戻るで地図に戻れるように）
  showStatus($("copy-status"), "", false);
  history.replaceState({ screen: "list" }, "", location.search);
  showScreen("list", false);
});

// ---- 確認 ----

// 確認を出し、"yes"（右のボタン）、"no"（左のボタン）、""（外側を押して閉じた）のどれかを返す
const confirmDialog = $("confirm-dialog");

function ask(title, noLabel, yesLabel, danger = false) {
  $("confirm-title").textContent = title;
  $("confirm-no").textContent = noLabel;
  $("confirm-yes").textContent = yesLabel;
  $("confirm-yes").className = `pill ${danger ? "danger" : "primary"}`;
  confirmDialog.returnValue = "";
  confirmDialog.showModal();
  return new Promise((resolve) => {
    confirmDialog.addEventListener("close", () => resolve(confirmDialog.returnValue), { once: true });
  });
}

// 外側（背景）を押したら閉じる
confirmDialog.addEventListener("click", (event) => {
  if (event.target === confirmDialog) {
    confirmDialog.close("");
  }
});

function resetRecords() {
  for (const record of state.records) {
    deletePhoto(record.imageName).catch(() => {});
  }
  state.records = [];
  saveRecords();
  renderOutput();
  drawOverlay();
  showStatus($("copy-status"), "", false);
  $("sign-id").value = suggestId();
}

// 保存の画面から出るときに「リセットしていい？」と聞く。リセットなら記録と写真を消して true。
// 残す・外側を押したときは false で、画面は移らない（保存の画面のまま）
async function confirmReset() {
  if (state.records.length === 0) {
    return true;
  }
  if ((await ask("リセットしていい？", "残す", "リセット", true)) !== "yes") {
    return false;
  }
  resetRecords();
  return true;
}

// 保存の画面の ←: リセットしたら地図へ戻る
$("list-back").addEventListener("click", async () => {
  if (!(await confirmReset())) {
    return;
  }
  skipAskOnBack = true;
  history.back();
});

// ＋: リセットしたら地図に戻って、次の看板の印を出す
$("add-another").addEventListener("click", async () => {
  if (!(await confirmReset())) {
    return;
  }
  history.replaceState({ screen: "map" }, "", location.search);
  showScreen("map", false);
  requestAnimationFrame(riseMarker);
});

// ---- ZIP ----
// 外部のライブラリを読み込めない（CSP）ので、圧縮しない ZIP（store）を自前で作る。写真は JPEG なので圧縮しても縮まない

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n += 1) {
    let c = n;
    for (let k = 0; k < 8; k += 1) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const b of bytes) {
    crc = CRC_TABLE[(crc ^ b) & 0xff] ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

// files: [{ name, bytes }]。名前は UTF-8（フラグ 0x0800）
function buildZip(files, date = new Date()) {
  const encoder = new TextEncoder();
  const time = (date.getHours() << 11) | (date.getMinutes() << 5) | Math.floor(date.getSeconds() / 2);
  const day = ((date.getFullYear() - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate();
  const parts = [];
  const central = [];
  let offset = 0;
  for (const file of files) {
    const name = encoder.encode(file.name);
    const crc = crc32(file.bytes);
    const size = file.bytes.length;
    const local = new DataView(new ArrayBuffer(30));
    local.setUint32(0, 0x04034b50, true);
    local.setUint16(4, 20, true);
    local.setUint16(6, 0x0800, true);
    local.setUint16(8, 0, true);
    local.setUint16(10, time, true);
    local.setUint16(12, day, true);
    local.setUint32(14, crc, true);
    local.setUint32(18, size, true);
    local.setUint32(22, size, true);
    local.setUint16(26, name.length, true);
    parts.push(local.buffer, name, file.bytes);
    const entry = new DataView(new ArrayBuffer(46));
    entry.setUint32(0, 0x02014b50, true);
    entry.setUint16(4, 20, true);
    entry.setUint16(6, 20, true);
    entry.setUint16(8, 0x0800, true);
    entry.setUint16(10, 0, true);
    entry.setUint16(12, time, true);
    entry.setUint16(14, day, true);
    entry.setUint32(16, crc, true);
    entry.setUint32(20, size, true);
    entry.setUint32(24, size, true);
    entry.setUint16(28, name.length, true);
    entry.setUint32(42, offset, true);
    central.push(entry.buffer, name);
    offset += 30 + name.length + size;
  }
  const centralSize = central.reduce((sum, part) => sum + (part.byteLength ?? part.length), 0);
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true);
  end.setUint16(8, files.length, true);
  end.setUint16(10, files.length, true);
  end.setUint32(12, centralSize, true);
  end.setUint32(16, offset, true);
  return new Blob([...parts, ...central, end.buffer], { type: "application/zip" });
}

function dataUrlToBytes(dataUrl) {
  const binary = atob(dataUrl.split(",")[1]);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

// 端末に ZIP で保存する。signs.json（リポジトリの signs.json と同じ形）と、photos/<imageName>.jpg。
// 写真のファイル名は imageName と同じなので、Unity の参照画像のフォルダにそのまま入れられる
$("download").addEventListener("click", async () => {
  const status = $("copy-status");
  if (state.records.length === 0) {
    showStatus(status, "まだ記録がありません。", true);
    return;
  }
  // ファイル名の日時は端末の時刻（日本時間）
  const now = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}`;
  const files = [{ name: "signs.json", bytes: new TextEncoder().encode($("output").textContent + "\n") }];
  for (const record of state.records) {
    try {
      const data = await getPhoto(record.imageName);
      if (data) {
        files.push({ name: `photos/${record.imageName}.jpg`, bytes: dataUrlToBytes(data) });
      }
    } catch {
      // 写真が読めなくても、看板の記録は保存する
    }
  }
  const url = URL.createObjectURL(buildZip(files, now));
  const link = document.createElement("a");
  link.href = url;
  link.download = `signs_${FLOOR.code}_${stamp}.zip`;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
  showStatus(status, `保存しました（写真 ${files.length - 1} 枚）`, false);
});

async function main() {
  // 開くたびに、前の記録と写真を消してから始める（保存は毎回 ZIP で端末に残す）
  saveRecords();
  await clearPhotos().catch(() => {});
  renderOutput();
  document.title = `案内板の記録（${FLOOR.label}）`;
  map.setAttribute("aria-label", `${FLOOR.label}の通路の地図`);
  document.querySelectorAll(".floors a").forEach((link) => {
    if (link.dataset.floor === FLOOR.code) {
      link.setAttribute("aria-current", "page");
    }
  });
  history.replaceState({ screen: "map" }, "", location.search);
  renderSizes();
  selectSize(FLOOR.size);
  if (FLOOR.image) {
    const { href, left, top, width, height } = FLOOR.image;
    imageLayer.append(svgElement("image", { href, x: left, y: -top, width, height, preserveAspectRatio: "none", class: "survey-image" }));
  }
  const [floorMap, station] = await Promise.all([api(`/v1/map?ordinal=${FLOOR.ordinal}`), api("/v1/station")]);
  state.knownSigns = station.signs.filter((sign) => sign.ordinal === FLOOR.ordinal);
  $("sign-id").value = suggestId();
  // 画像のある階は画像だけを出す。通路の線と改札は、画像のない階だけに描く
  if (!FLOOR.image) {
    state.gates = floorMap.gates;
    for (const line of floorMap.lines) {
      networkLayer.append(svgElement("path", { d: toPath(line), class: "network", "stroke-width": 3, "vector-effect": "non-scaling-stroke" }));
    }
  }
  if (FLOOR.image) {
    // 画像を画面の高さいっぱいに映す（縦長の画面では左右が切れるので、画像の中だけ左右に動かせる）
    const { left, top, width, height } = FLOOR.image;
    fitImage();
  } else {
    fitTo(focusPoints(floorMap.lines, floorMap.gates));
  }
  state.home = { ...state.view };
  applyView();
}

main().catch((error) => showStatus($("form-status"), error.message, true));
