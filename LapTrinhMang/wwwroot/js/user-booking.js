// ====== CONFIG / STATE ======
const cfg = window.BOOKING_CFG || {};
const baseUrl = cfg.baseUrl || location.origin;
const loggedName = cfg.loggedName || "";
const loggedPhone = cfg.loggedPhone || "";

const el = (id) => document.getElementById(id);
const state = {
    selectedTable: null,
    slotCache: new Map(), // key: `${date}|${tableNo}` => slots[]
};

const pad2 = (n) => String(n).padStart(2, "0");
const toDateValue = (d) => `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;

const showMsg = (type, msg) => {
    const err = el("dlgErr"), ok = el("dlgOk");
    err.style.display = "none"; ok.style.display = "none";
    if (!type) return;
    if (type === "err") { err.textContent = msg; err.style.display = "block"; }
    if (type === "ok") { ok.textContent = msg; ok.style.display = "block"; }
};

const typeClass = (type) => {
    const t = (type || "").toLowerCase();
    if (t.includes("vvip")) return "vvip";
    if (t.includes("vip")) return "vip";
    return "";
};

// ====== API ======
async function apiGetTables() {
    const res = await fetch(`${baseUrl}/api/user/tables`);
    if (!res.ok) throw new Error("GET tables failed: " + res.status);
    const data = await res.json();
    return Array.isArray(data) ? data : [];
}

async function apiGetSlots(tableNo, dateVal) {
    const key = `${dateVal}|${tableNo}`;
    if (state.slotCache.has(key)) return state.slotCache.get(key);

    try {
        const res = await fetch(`${baseUrl}/api/user/tables/${tableNo}/slots?date=${encodeURIComponent(dateVal)}`);
        if (!res.ok) return [];
        const data = await res.json();
        const slots = Array.isArray(data) ? data : [];
        state.slotCache.set(key, slots);
        return slots;
    } catch {
        return [];
    }
}

// ====== RENDER ======
function renderCards(list, dateVal) {
    const filter = el("filter").value;
    const items = filter ? list.filter(x => x.dayStatus === filter) : list;

    el("grid").innerHTML = items.map(t => {
        const full = t.dayStatus === "Full";
        const badge = full ? `<span class="badge full">Hết suất</span>` : `<span class="badge ok">Available</span>`;
        const typePill = `<span class="typePill ${typeClass(t.type)}">${t.type}</span>`;

        return `
      <div class="card">
        <div class="cardHead">
          <div class="tableNo">Bàn ${t.tableNumber}</div>
          ${badge}
        </div>

        <div class="row">Sức chứa: <b>${t.capacity}</b></div>
        <div class="row">Loại: ${typePill}</div>
        <div class="row">Suất trống (${dateVal}): <b>${t.slotCount}</b></div>

        <div class="cardActions">
          <button class="cardBtn" ${full ? "disabled" : ""} onclick="openBook(${t.tableNumber})">
            ${full ? "Hết suất" : "Đặt bàn"}
          </button>
        </div>
      </div>
    `;
    }).join("");
}

// ====== MAIN LOAD ======
async function loadTables() {
    const dateVal = el("pageDate").value;
    if (!dateVal) return;

    const tables = await apiGetTables();

    const enriched = await Promise.all(tables.map(async (t) => {
        const slots = await apiGetSlots(t.tableNumber, dateVal);
        const slotCount = slots.length;
        return {
            tableNumber: t.tableNumber,
            capacity: t.capacity,
            type: t.type || "Normal",
            slotCount,
            dayStatus: slotCount > 0 ? "Available" : "Full"
        };
    }));

    renderCards(enriched, dateVal);
}

// ====== POPUP ======
async function loadSlotsForPopup() {
    const dateVal = el("pageDate").value;
    el("dlgDateLabel").textContent = dateVal || "";

    const sel = el("dlgSlot");
    sel.innerHTML = `<option value="">-- Chọn suất giờ --</option>`;

    if (!state.selectedTable || !dateVal) return;

    const slots = await apiGetSlots(state.selectedTable, dateVal);

    if (!slots.length) {
        showMsg("err", "Ngày này không còn suất trống.");
        return;
    }

    for (const s of slots) {
        const start = new Date(s.startLocal);
        const end = new Date(s.endLocal);

        const label =
            `${start.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` +
            ` - ` +
            `${end.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`;

        const opt = document.createElement("option");
        opt.value = JSON.stringify(s);
        opt.textContent = label;
        sel.appendChild(opt);
    }

    showMsg("", "");
}

async function openBook(tableNo) {
    state.selectedTable = tableNo;
    el("dlgTableNo").textContent = tableNo;

    // prefill
    el("dlgName").value = loggedName || "";
    el("dlgPhone").value = loggedPhone || "";
    el("dlgSlot").value = "";

    showMsg("", "");
    el("bookDlg").showModal();

    await loadSlotsForPopup();
}

function closeDlg() {
    el("bookDlg").close();
    state.selectedTable = null;
    showMsg("", "");
}

async function submitBooking() {
    showMsg("", "");

    const name = (el("dlgName").value || "").trim();
    const phone = (el("dlgPhone").value || "").trim();
    const slotVal = el("dlgSlot").value;

    if (!state.selectedTable) return showMsg("err", "Chưa chọn bàn.");
    if (!name) return showMsg("err", "Tên bị trống.");
    if (phone.length < 8) return showMsg("err", "SĐT không hợp lệ.");
    if (!slotVal) return showMsg("err", "Bạn chưa chọn suất giờ.");

    let slot;
    try { slot = JSON.parse(slotVal); }
    catch { return showMsg("err", "Suất giờ không hợp lệ."); }

    const body = {
        tableNumber: state.selectedTable,
        customerName: name,      // ✅ gửi tên người dùng nhập
        phone: phone,
        startTime: slot.startLocal,
        hours: slot.hours
    };

    const btn = el("dlgSubmitBtn");
    btn.disabled = true;

    try {
        const res = await fetch(`${baseUrl}/api/user/reservations`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body)
        });

        const text = await res.text();
        if (!res.ok) {
            let msg = text;
            try { msg = JSON.parse(text).message || text; } catch { }
            return showMsg("err", msg);
        }

        // reservation changed => clear cache
        state.slotCache.clear();

        showMsg("ok", "Đã gửi yêu cầu đặt bàn!");
        await loadTables();
        await loadSlotsForPopup();
        setTimeout(closeDlg, 450);
    }
    catch (e) {
        showMsg("err", "Lỗi mạng/JS: " + e);
    }
    finally {
        btn.disabled = false;
    }
}

async function logout() {
    await fetch(`${baseUrl}/api/auth/logout`, { method: "POST" });
    location.href = "/AuthView/Login";
}

// expose for onclick
window.openBook = openBook;

// ====== SignalR ======
const connection = new signalR.HubConnectionBuilder()
    .withUrl(baseUrl + "/hubs/booking")
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Information)
    .build();

const onRealtime = async () => {
    state.slotCache.clear();
    await loadTables();
    if (el("bookDlg").open) await loadSlotsForPopup();
};

connection.on("TableUpdated", onRealtime);
connection.on("ReservationCreated", onRealtime);
connection.on("ReservationUpdated", onRealtime);
connection.on("ReservationCanceled", onRealtime);

// ====== INIT ======
document.addEventListener("DOMContentLoaded", async () => {
    const todayVal = toDateValue(new Date());
    el("pageDate").min = todayVal;
    el("pageDate").value = todayVal;

    el("btnLogout").addEventListener("click", logout);
    el("filter").addEventListener("change", loadTables);

    el("pageDate").addEventListener("change", async () => {
        state.slotCache.clear();
        await loadTables();
        if (el("bookDlg").open) await loadSlotsForPopup();
    });

    el("dlgCloseX").addEventListener("click", closeDlg);
    el("dlgCancel").addEventListener("click", closeDlg);
    el("dlgSubmitBtn").addEventListener("click", submitBooking);

    await loadTables();

    try { await connection.start(); }
    catch { /* vẫn chạy bằng HTTP bình thường */ }
});
