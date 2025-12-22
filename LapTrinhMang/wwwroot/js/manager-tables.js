const cfg = window.MANAGER_CFG || {};
const baseUrl = cfg.baseUrl || location.origin;

const el = (id) => document.getElementById(id);
let currentTableNumber = null;

// ===== Auth helpers (JWT OR Cookie) =====
function getToken() {
    return localStorage.getItem("token") || "";
}
async function apiFetch(url, opts = {}) {
    const token = getToken();
    const headers = { ...(opts.headers || {}) };
    if (token) headers["Authorization"] = `Bearer ${token}`;
    return fetch(url, { ...opts, headers, credentials: "include" });
}

// ===== Date helpers =====
function todayISO() {
    const d = new Date();
    const pad = (n) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
function getSelectedDate() {
    const v = el("pageDate")?.value;
    return v || todayISO();
}

// ===== UI helpers =====
function badgeClass(status) {
    if (status === "Full") return "full";
    if (status === "Pending") return "pending";
    if (status === "Reserved") return "reserved";
    return "ok";
}
function badgeText(t) {
    if (t.status === "Full") return "Hết suất";
    if (t.status === "Pending") return `Pending (${t.pendingCount || 0})`;
    if (t.status === "Reserved") return `Reserved (${t.approvedCount || 0})`;
    return "Available";
}
function typeClass(type) {
    const t = (type || "").toLowerCase();
    if (t.includes("vvip")) return "vvip";
    if (t.includes("vip")) return "vip";
    return "";
}
function fmtDateTime(x) {
    if (!x) return "";
    return new Date(x).toLocaleString();
}

// ===== API =====
async function apiGetTables(dateVal) {
    const qs = new URLSearchParams();
    qs.set("date", dateVal);

    const res = await apiFetch(`${baseUrl}/api/manager/tables?${qs.toString()}`);
    if (res.status === 401) throw new Error("401_UNAUTHORIZED");
    if (!res.ok) throw new Error(`GET /api/manager/tables ${res.status}`);
    const data = await res.json();
    return Array.isArray(data) ? data : [];
}

async function apiGetReservations(tableNo, status, dateVal) {
    const qs = new URLSearchParams();
    qs.set("tableNumber", tableNo);
    qs.set("date", dateVal);
    if (status) qs.set("status", status);

    const res = await apiFetch(`${baseUrl}/api/manager/reservations?${qs.toString()}`);
    if (res.status === 401) throw new Error("401_UNAUTHORIZED");
    if (!res.ok) throw new Error(`GET reservations ${res.status} ${await res.text()}`);
    const data = await res.json();
    return Array.isArray(data) ? data : [];
}

async function apiApprove(id) {
    const res = await apiFetch(`${baseUrl}/api/manager/reservations/${id}/approve`, { method: "PUT" });
    if (res.status === 401) throw new Error("401_UNAUTHORIZED");
    if (!res.ok) throw new Error(await res.text());
}
async function apiCancel(id) {
    const res = await apiFetch(`${baseUrl}/api/manager/reservations/${id}/cancel`, { method: "PUT" });
    if (res.status === 401) throw new Error("401_UNAUTHORIZED");
    if (!res.ok) throw new Error(await res.text());
}

// ===== Render tables =====
function renderTables(items, dateVal) {
    const filter = el("filter").value;
    const list = filter ? items.filter(x => x.status === filter) : items;

    el("grid").innerHTML = list.map(t => {
        const badge = `<span class="badge ${badgeClass(t.status)}">${badgeText(t)}</span>`;
        const type = t.type || "Normal";
        const typePill = `<span class="typePill ${typeClass(type)}">${type}</span>`;

        const slotLine = `Suất trống (${dateVal}): <b>${t.slotCount ?? "-"}</b>`;
        const orderLine = `Đơn: <b>Pending ${t.pendingCount ?? 0}</b> | <b>Approved ${t.approvedCount ?? 0}</b>`;

        const note =
            (t.pendingCount > 0)
                ? `<div class="row" style="color:#b45309">Có đơn chờ duyệt → bấm “Xem đơn”</div>`
                : (t.approvedCount > 0)
                    ? `<div class="row" style="color:#dc2626">Có đơn đã duyệt</div>`
                    : `<div class="row">Không có đơn</div>`;

        return `
      <div class="card">
        <div class="cardHead">
          <div class="tableNo">Bàn ${t.tableNumber}</div>
          ${badge}
        </div>

        <div class="row">Sức chứa: <b>${t.capacity}</b></div>
        <div class="row">Loại: ${typePill}</div>
        <div class="row">${slotLine}</div>
        <div class="row">${orderLine}</div>
        ${note}

        <div class="cardActions">
          <button class="cardBtn" type="button" data-open-res="${t.tableNumber}">Xem đơn</button>
        </div>
      </div>
    `;
    }).join("");
}

async function loadTables() {
    try {
        const dateVal = getSelectedDate();
        const data = await apiGetTables(dateVal);
        renderTables(data, dateVal);
    } catch (e) {
        if (String(e.message || e) === "401_UNAUTHORIZED") {
            location.href = "/AuthView/Login";
            return;
        }
        console.error(e);
    }
}

// ===== Dialog reservations =====
function pill(status) {
    const s = (status || "").toLowerCase();
    if (s === "pending") return `<span class="pill pending">Pending</span>`;
    if (s === "approved") return `<span class="pill approved">Approved</span>`;
    if (s === "canceled") return `<span class="pill canceled">Canceled</span>`;
    return `<span class="pill canceled">${status}</span>`;
}

function setHint(msg) { el("resHint").textContent = msg || ""; }

function openResDialog(tableNo) {
    currentTableNumber = tableNo;
    el("resTitle").textContent = `Đơn đặt bàn - Bàn ${tableNo}`;
    el("resFilter").value = "";
    setHint("");
    el("resDlg").showModal();
    reloadReservations();
}
function closeResDialog() {
    el("resDlg").close();
    currentTableNumber = null;
}

async function reloadReservations() {
    if (!currentTableNumber) return;

    try {
        setHint("Đang tải...");
        const status = el("resFilter").value;
        const dateVal = getSelectedDate();

        const data = await apiGetReservations(currentTableNumber, status, dateVal);

        if (!data.length) {
            el("resList").innerHTML =
                `<div class="resItem" style="border-style:dashed;">Không có đơn trong ngày ${dateVal}.</div>`;
            setHint("");
            return;
        }

        el("resList").innerHTML = data.map(r => {
            const start = fmtDateTime(r.startTime);
            const end = fmtDateTime(r.endTime);

            const canApprove = r.status === "Pending";
            const canCancel = r.status !== "Canceled";

            return `
        <div class="resItem">
          <div class="resHead">
            <div class="resId">#${r.id} - Bàn ${r.tableNumber}</div>
            ${pill(r.status)}
          </div>

          <div class="row" style="margin-top:8px; color:var(--text);">
            <b>${r.customerName}</b> (${r.phone})
          </div>
          <div class="row">${start} → ${end}</div>

          <div class="resActions">
            <button class="actionBtn primary" type="button" data-approve="${r.id}" ${canApprove ? "" : "disabled"}>Duyệt</button>
            <button class="actionBtn" type="button" data-cancel="${r.id}" ${canCancel ? "" : "disabled"}>Hủy</button>
          </div>
        </div>
      `;
        }).join("");

        setHint("");
    } catch (e) {
        if (String(e.message || e) === "401_UNAUTHORIZED") {
            location.href = "/AuthView/Login";
            return;
        }
        setHint(String(e));
    }
}

// ===== Logout =====
async function logout() {
    try { await apiFetch(`${baseUrl}/api/auth/logout`, { method: "POST" }); } catch { }
    localStorage.removeItem("token");
    localStorage.removeItem("role");
    localStorage.removeItem("fullName");
    localStorage.removeItem("phone");
    location.href = "/AuthView/Login";
}

// ===== SignalR =====
const token = getToken();
const hubUrl = baseUrl + "/hubs/booking";
const hubOpts = token ? { accessTokenFactory: () => getToken() } : { withCredentials: true };

const connection = new signalR.HubConnectionBuilder()
    .withUrl(hubUrl, hubOpts)
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Information)
    .build();

async function onRealtime() {
    await loadTables();
    if (currentTableNumber) await reloadReservations();
}

// ===== Init =====
document.addEventListener("DOMContentLoaded", async () => {
    // date init
    if (el("pageDate")) {
        el("pageDate").value = todayISO();
        el("pageDate").addEventListener("change", async () => {
            await loadTables();
            if (el("resDlg")?.open) await reloadReservations();
        });
    }

    el("btnLogout").addEventListener("click", logout);
    el("filter").addEventListener("change", loadTables);

    // open dialog delegation
    el("grid").addEventListener("click", (e) => {
        const btn = e.target.closest("[data-open-res]");
        if (!btn) return;
        openResDialog(Number(btn.getAttribute("data-open-res")));
    });

    // dialog controls
    el("resCloseX").addEventListener("click", closeResDialog);
    el("btnResClose").addEventListener("click", closeResDialog);
    el("btnResReload").addEventListener("click", reloadReservations);
    el("resFilter").addEventListener("change", reloadReservations);

    // approve/cancel delegation
    el("resList").addEventListener("click", async (e) => {
        const a = e.target.closest("[data-approve]");
        const c = e.target.closest("[data-cancel]");
        try {
            if (a) { await apiApprove(Number(a.getAttribute("data-approve"))); await reloadReservations(); await loadTables(); }
            if (c) { await apiCancel(Number(c.getAttribute("data-cancel"))); await reloadReservations(); await loadTables(); }
        } catch (err) {
            if (String(err.message || err) === "401_UNAUTHORIZED") { location.href = "/AuthView/Login"; return; }
            alert(String(err));
        }
    });

    await loadTables();

    // realtime
    connection.on("TableUpdated", onRealtime);
    connection.on("ReservationCreated", onRealtime);
    connection.on("ReservationUpdated", onRealtime);
    connection.on("ReservationCanceled", onRealtime);

    try { await connection.start(); } catch { }
});
