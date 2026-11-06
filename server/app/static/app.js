"use strict";

// 利用者の入力とサーバーの文字列は textContent で入れ、HTML として解釈させない
const $ = (id) => document.getElementById(id);
const SVG_NS = "http://www.w3.org/2000/svg";
const MAP_MARGIN_METERS = 25;
const MOVE_LABELS = { stairs: "階段", escalator: "エスカレーター", elevator: "エレベーター", slope: "坂" };
const TURN_LABELS = { left: "左へ", right: "右へ" };
const mapCache = new Map();

async function api(path, body) {
  const options = body
    ? { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }
    : {};
  const response = await fetch(path, options);
  const data = await response.json();
  if (!response.ok) {
    throw new Error(data.error ? data.error.message : "読み込みに失敗しました");
  }
  return data;
}

function option(value, label) {
  const element = document.createElement("option");
  element.value = value;
  element.textContent = label;
  return element;
}

function gateLabel(gate) {
  return `${gate.floor} ${gate.operator} ${gate.name}`;
}

function showError(element, error) {
  element.textContent = error.message;
  element.classList.add("error");
}

function showText(element, text) {
  element.textContent = text;
  element.classList.remove("error");
}

function svgElement(name, attributes) {
  const element = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes)) {
    element.setAttribute(key, value);
  }
  return element;
}

// z は北が正なので、画面の下向きが正の SVG では符号を反転する
function toPath(points) {
  return points.map(([x, z], i) => `${i === 0 ? "M" : "L"}${x.toFixed(2)} ${(-z).toFixed(2)}`).join(" ");
}

async function floorLines(ordinal) {
  if (!mapCache.has(ordinal)) {
    const map = await api(`/v1/map?ordinal=${encodeURIComponent(ordinal)}`);
    mapCache.set(ordinal, map.lines);
  }
  return mapCache.get(ordinal);
}

function bounds(points) {
  const xs = points.map(([x]) => x);
  const zs = points.map(([, z]) => z);
  return {
    minX: Math.min(...xs) - MAP_MARGIN_METERS,
    maxX: Math.max(...xs) + MAP_MARGIN_METERS,
    minZ: Math.min(...zs) - MAP_MARGIN_METERS,
    maxZ: Math.max(...zs) + MAP_MARGIN_METERS,
  };
}

function splitByFloor(points) {
  const startFloor = points[0].ordinal;
  const parts = [];
  for (let i = 1; i < points.length; i += 1) {
    const sameFloor = points[i - 1].ordinal === startFloor && points[i].ordinal === startFloor;
    const last = parts[parts.length - 1];
    if (last && last.sameFloor === sameFloor) {
      last.points.push([points[i].x, points[i].z]);
    } else {
      parts.push({ sameFloor, points: [[points[i - 1].x, points[i - 1].z], [points[i].x, points[i].z]] });
    }
  }
  return parts;
}

function drawSign(map, sign, scale) {
  const size = 3 * scale;
  const rad = (sign.azimuthDeg * Math.PI) / 180;
  const dx = Math.sin(rad);
  const dz = Math.cos(rad);
  const tip = [sign.x + dx * size, sign.z + dz * size];
  const left = [sign.x - dz * size * 0.5, sign.z + dx * size * 0.5];
  const right = [sign.x + dz * size * 0.5, sign.z - dx * size * 0.5];
  const corners = [tip, left, right].map(([x, z]) => `${x.toFixed(2)},${(-z).toFixed(2)}`).join(" ");
  map.append(svgElement("polygon", { points: corners, class: "sign" }));
}

async function drawRoute(route, signs) {
  const map = $("map");
  map.replaceChildren();
  const routePoints = route.points.map((p) => [p.x, p.z]);
  const box = bounds(routePoints);
  const width = box.maxX - box.minX;
  const height = box.maxZ - box.minZ;
  map.setAttribute("viewBox", `${box.minX} ${-box.maxZ} ${width} ${height}`);
  const scale = Math.max(width, height) / 100;

  for (const line of await floorLines(route.points[0].ordinal)) {
    map.append(svgElement("path", { d: toPath(line), class: "network", "stroke-width": 0.6 * scale }));
  }
  // 下地は出発の階だけなので、別の階を通る区間は破線で分ける
  for (const part of splitByFloor(route.points)) {
    const className = part.sameFloor ? "route" : "route other-floor";
    map.append(svgElement("path", { d: toPath(part.points), class: className, "stroke-width": 2.2 * scale }));
  }
  const [startX, startZ] = routePoints[0];
  map.append(svgElement("circle", { cx: startX, cy: -startZ, r: 1.8 * scale, class: "start", "stroke-width": 0.8 * scale }));
  const [goalX, goalZ] = routePoints[routePoints.length - 1];
  map.append(svgElement("circle", { cx: goalX, cy: -goalZ, r: 2.4 * scale, class: "goal" }));
  for (const item of route.signsOnRoute) {
    const sign = signs.get(item.signId);
    if (sign) {
      drawSign(map, sign, scale);
    }
  }
}

function renderSteps(route) {
  const steps = $("steps");
  steps.replaceChildren();
  // distanceFromStart は出発からの合計なので、前の曲がり角からの距離に直して出す
  let previous = 0;
  const items = route.turns.map((turn) => {
    const meters = Math.round(turn.distanceFromStart - previous);
    previous = turn.distanceFromStart;
    return {
      at: turn.distanceFromStart,
      text: `${meters} m 進んで${TURN_LABELS[turn.direction]}（${Math.round(turn.angleDeg)} 度）`,
    };
  });
  // エスカレーターは階ごとに区間が分かれるので、種類ごとに1回だけ出す
  for (const kind of new Set(route.verticalMoves.map((move) => move.kind))) {
    items.push({ at: Number.MAX_VALUE, text: `途中で${MOVE_LABELS[kind] || kind}を使います` });
  }
  items.sort((a, b) => a.at - b.at);
  for (const item of items) {
    const element = document.createElement("li");
    element.textContent = item.text;
    steps.append(element);
  }
}

async function showRoute(toGateId, signs) {
  const summary = $("route-summary");
  try {
    const route = await api("/v1/route", { fromSignId: $("sign").value, toGateId, sameFloor: $("same-floor").checked });
    const crosses = route.points.some((point) => point.ordinal !== route.points[0].ordinal);
    const note = crosses ? "。破線は別の階です" : "";
    showText(summary, `${route.start.name} から ${route.goal.floor} ${route.goal.name} まで、約 ${Math.round(route.lengthMeters)} m${note}`);
    await drawRoute(route, signs);
    renderSteps(route);
  } catch (error) {
    showError(summary, error);
  }
}

function renderCandidates(result, signs) {
  const list = $("candidates");
  list.replaceChildren();
  for (const candidate of result.candidates) {
    const item = document.createElement("li");
    const button = document.createElement("button");
    button.type = "button";
    const distance = candidate.lengthMeters >= 0 ? `、約 ${Math.round(candidate.lengthMeters)} m` : "";
    button.textContent = `${candidate.floor} ${candidate.operator} ${candidate.name}${distance}`;
    button.addEventListener("click", () => showRoute(candidate.gateId, signs));
    item.append(button);
    list.append(item);
  }
}

const STATUS_MESSAGES = {
  ok: "行き先が決まりました。",
  ambiguous: "複数の候補があります。選んでください。",
  out_of_scope: "この行き先には対応していません。一覧から選んでください。",
  unavailable: "判定できませんでした。一覧から選んでください。",
};

async function decideDestination(signs) {
  const status = $("destination-status");
  try {
    const result = await api("/v1/destination", { text: $("text").value, fromSignId: $("sign").value });
    showText(status, `${STATUS_MESSAGES[result.status]}（判定: ${result.engine}、${result.latencyMs} ms）`);
    renderCandidates(result, signs);
    if (result.status === "ok" && result.candidates.length > 0) {
      await showRoute(result.candidates[0].gateId, signs);
    }
  } catch (error) {
    showError(status, error);
  }
}

function showLink(id, url) {
  if (!url) {
    return false;
  }
  const link = $(id);
  link.href = url;
  link.hidden = false;
  return true;
}

async function main() {
  const [site, station] = await Promise.all([api("/v1/site"), api("/v1/station")]);
  $("attribution").textContent = site.attribution;
  $("version").textContent = site.packVersion;
  const hasVideo = showLink("video-link", site.videoUrl);
  const hasApk = showLink("apk-link", site.apkUrl);
  $("links-pending").hidden = hasVideo || hasApk;

  const signs = new Map(station.signs.map((sign) => [sign.id, sign]));
  $("sign").replaceChildren(...station.signs.map((sign) => option(sign.id, sign.name)));
  $("gate").replaceChildren(...station.gates.map((gate) => option(gate.id, gateLabel(gate))));

  $("destination-form").addEventListener("submit", (event) => {
    event.preventDefault();
    decideDestination(signs);
  });
  $("route-form").addEventListener("submit", (event) => {
    event.preventDefault();
    showRoute($("gate").value, signs);
  });
}

main().catch((error) => showError($("route-summary"), error));
