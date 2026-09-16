/* The browser and headless exporter share this renderer. Fusions prefer cached PNG data. */
"use strict";
async function renderEmoji(canvas, recipe, size = 512) {
  const emoji = recipe.emoji ?? recipe.Emoji;
  const top = recipe.top ?? recipe.Top;
  const bottom = recipe.bottom ?? recipe.Bottom;
  const style = recipe.style ?? recipe.Style ?? "card";
  if (typeof emoji !== "string" || !emoji.trim() || emoji.length > 64 ||
      !/^#[0-9a-f]{6}$/i.test(top) || !/^#[0-9a-f]{6}$/i.test(bottom) || ![256, 512, 1024].includes(size)) {
    throw new Error("请输入 emoji、有效颜色和支持的尺寸。");
  }
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext("2d");
  ctx.scale(size / 512, size / 512);
  if (style !== "card" && style !== "icon") throw new Error("未知图案样式。");
  const bg = ctx.createLinearGradient(0, 0, 0, 512);
  bg.addColorStop(0, top); bg.addColorStop(1, bottom);
  if (style === "card") {
  ctx.fillStyle = bg; ctx.fillRect(0, 0, 512, 512);
  ctx.fillStyle = "#ffffff12"; ctx.beginPath(); ctx.arc(256, 250, 177, 0, 2 * Math.PI); ctx.fill();
  ctx.strokeStyle = "#ffffff2e"; ctx.lineWidth = 2; ctx.beginPath(); ctx.arc(256, 250, 187, 0, 2 * Math.PI); ctx.stroke();
  }
  const fusion = recipe.fusionDataUrl ?? recipe.FusionDataUrl ?? recipe.fusionSource ?? recipe.FusionSource;
  if (fusion) {
    if (typeof fusion !== "string" || (!fusion.startsWith("data:image/png;base64,") && !fusion.startsWith("https://"))) throw new Error("融合图必须是本地 PNG 数据或 HTTPS 来源。");
    const image = new Image(); image.crossOrigin = "anonymous";
    await new Promise((resolve, reject) => { image.onload = resolve; image.onerror = () => reject(new Error("融合图载入失败。")); image.src = fusion; });
    const edge = style === "icon" ? 350 : 310, offset = (512 - edge) / 2;
    ctx.drawImage(image, offset, offset, edge, edge);
  } else {
    ctx.font = '220px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
    ctx.textAlign = "center"; ctx.textBaseline = "middle";
    const width = ctx.measureText(emoji).width;
    if (width > 390) ctx.font = `${Math.floor(220 * 390 / width)}px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif`;
    ctx.fillStyle = "#fff"; ctx.fillText(emoji, 256, 258);
  }
  return canvas;
}
globalThis.renderEmoji = renderEmoji;
if (typeof document !== "undefined" && document.getElementById("preview")) {
  const $ = id => document.getElementById(id);
  let recipes = {}, activeFusion;
  async function refresh() {
    try { await renderEmoji($("preview"), { emoji: $("emoji").value, top: $("top").value, bottom: $("bottom").value, style: $("style").value, fusionSource: activeFusion }, Number($("size").value)); $("status").textContent = ""; }
    catch (error) { $("status").textContent = error.message; }
  }
  for (const id of ["emoji", "top", "bottom", "size", "style"]) $(id).addEventListener("input", () => { if (id === "emoji") activeFusion = undefined; refresh(); });
  $("download").addEventListener("click", async () => {
    await refresh(); if ($("status").textContent) return;
    $("preview").toBlob(blob => {
      if (!blob) { $("status").textContent = "导出失败，请重试。"; return; }
      const url = URL.createObjectURL(blob), link = document.createElement("a");
      link.href = url; link.download = ($("name").value.replace(/[^\p{L}\p{N}_-]/gu, "_") || "emoji-card") + ".png";
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
      $("status").textContent = "PNG 已生成。";
    }, "image/png");
  });
  $("recipes").addEventListener("change", async () => {
    try {
      const file = $("recipes").files[0]; if (!file) return;
      if (file.size > 1024 * 1024) throw new Error("配方文件过大。");
      recipes = JSON.parse(await file.text());
      const keys = Object.keys(recipes).sort(); if (!keys.length) throw new Error("配方文件为空。");
      $("cards").replaceChildren(...keys.map(id => new Option(id, id)));
      $("cards").hidden = false; $("cards").dispatchEvent(new Event("change"));
    } catch (error) { $("status").textContent = error.message; }
  });
  $("cards").addEventListener("change", () => {
    const id = $("cards").value, r = recipes[id];
    activeFusion = r.fusionSource ?? r.FusionSource;
    $("emoji").value = r.emoji ?? r.Emoji ?? ""; $("top").value = r.top ?? r.Top ?? "#000000";
    $("bottom").value = r.bottom ?? r.Bottom ?? "#000000"; $("style").value = r.style ?? r.Style ?? "card"; $("name").value = id; refresh();
  });
  refresh();
}
