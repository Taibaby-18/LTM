// ====== CONFIG / STATE ======
// Lấy thông tin từ biến toàn cục được định nghĩa trong View
const cfg = window.BOOKING_CFG || {};
const baseUrl = cfg.baseUrl || location.origin;
const loggedName = cfg.loggedName || "";
const loggedPhone = cfg.loggedPhone || "";
const loggedEmail = cfg.loggedEmail || ""; // ✅ Thêm dòng này

const el = (id) => document.getElementById(id);
const state = {
    selectedTable: null,
    slotCache: new Map(), // key: `${date}|${tableNo}` => slots[]
};

// Hàm show thông báo lỗi/thành công (đã khớp với bootstrap alert trong view mới)
const showMsg = (type, msg) => {
    const err = el("dlgErr"), ok = el("dlgOk");

    // Ẩn hết trước
    if (err) err.classList.add("d-none");
    if (ok) ok.classList.add("d-none");

    if (!type) return;

    if (type === "err" && err) {
        err.textContent = msg;
        err.classList.remove("d-none");
    }
    if (type === "ok" && ok) {
        ok.textContent = msg;
        ok.classList.remove("d-none");
    }
};

// ====== API ======
async function apiGetTables() {
    try {
        const res = await fetch(`${baseUrl}/api/user/tables`);
        if (!res.ok) throw new Error("GET tables failed: " + res.status);
        const data = await res.json();
        return Array.isArray(data) ? data : [];
    } catch (e) {
        console.error(e);
        return [];
    }
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

// ====== RENDER (Giao diện mới) ======
function renderCards(list, dateVal) {
    const filter = el("filter").value;
    const items = filter ? list.filter(x => x.dayStatus === filter) : list;

    const grid = el("grid");
    if (!grid) return;

    if (items.length === 0) {
        grid.innerHTML = `<div class="col-12 text-center text-muted py-5">Không tìm thấy bàn nào phù hợp.</div>`;
        return;
    }

    grid.innerHTML = items.map(t => {
        const full = t.dayStatus === "Full";

        // Badge trạng thái
        const badge = full
            ? `<span class="badge bg-danger rounded-pill"><i class="fas fa-times-circle me-1"></i>Hết chỗ</span>`
            : `<span class="badge bg-success rounded-pill"><i class="fas fa-check-circle me-1"></i>Còn chỗ</span>`;

        // Badge loại bàn
        let typeBadgeClass = "bg-secondary";
        if (t.type === "VIP") typeBadgeClass = "bg-warning text-dark";
        if (t.type === "VVIP") typeBadgeClass = "bg-danger text-white";
        const typePill = `<span class="badge ${typeBadgeClass} ms-1">${t.type}</span>`;

        // Button style
        const btnClass = full ? "btn-secondary disabled" : "btn-outline-primary";
        const btnText = full ? "Đã kín lịch" : "Đặt bàn ngay";

        // HTML chuẩn Bootstrap Card
        return `
        <div class="card shadow-sm border-0 h-100 table-card">
            <div class="card-body d-flex flex-column">
                <div class="d-flex justify-content-between align-items-start mb-3">
                    <div>
                        <h5 class="card-title fw-bold text-primary mb-1" style="font-family: 'Playfair Display', serif;">
                            Bàn ${t.tableNumber}
                        </h5>
                        ${typePill}
                    </div>
                    ${badge}
                </div>

                <div class="mb-3 text-muted small flex-grow-1">
                    <div class="d-flex align-items-center mb-1">
                        <i class="fas fa-users me-2 text-secondary" style="width:20px"></i>
                        <span>Sức chứa: <strong>${t.capacity} người</strong></span>
                    </div>
                    <div class="d-flex align-items-center">
                        <i class="far fa-clock me-2 text-secondary" style="width:20px"></i>
                        <span>Suất trống: <strong>${t.slotCount}</strong></span>
                    </div>
                </div>

                <button class="btn ${btnClass} w-100 fw-bold mt-auto" 
                        onclick="window.openBook(${t.tableNumber})" ${full ? "disabled" : ""}>
                    ${btnText}
                </button>
            </div>
        </div>
        `;
    }).join("");
}

// ====== MAIN LOAD ======
async function loadTables() {
    const dateInput = el("pageDate");
    if (!dateInput) return;

    const dateVal = dateInput.value;
    if (!dateVal) return;

    const tables = await apiGetTables();

    // Lấy thông tin slot của từng bàn song song
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

// ====== POPUP LOGIC ======
async function loadSlotsForPopup() {
    const dateVal = el("pageDate").value;
    const labelDate = el("dlgDateLabel");
    if (labelDate) labelDate.textContent = dateVal ? `Ngày ${dateVal.split('-').reverse().join('/')}` : "";

    const sel = el("dlgSlot");
    sel.innerHTML = `<option value="">Đang tải suất...</option>`;
    sel.disabled = true;

    if (!state.selectedTable || !dateVal) return;

    const slots = await apiGetSlots(state.selectedTable, dateVal);

    sel.innerHTML = `<option value="">-- Chọn khung giờ --</option>`;
    sel.disabled = false;

    if (!slots.length) {
        showMsg("err", "Rất tiếc, ngày này bàn đã kín lịch.");
        return;
    }

    // Render danh sách giờ
    for (const s of slots) {
        const start = new Date(s.startLocal);
        const end = new Date(s.endLocal);

        // Format giờ: 17:00 - 19:00
        const timeStr = (d) => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
        const label = `${timeStr(start)} - ${timeStr(end)} (${s.hours} tiếng)`;

        const opt = document.createElement("option");
        opt.value = JSON.stringify(s); // Lưu object slot vào value
        opt.textContent = label;
        sel.appendChild(opt);
    }

    showMsg("", ""); // Xóa thông báo lỗi cũ
}

async function openBook(tableNo) {
    state.selectedTable = tableNo;

    const titleEl = el("dlgTableNo");
    if (titleEl) titleEl.textContent = `số ${tableNo}`;

    // Prefill thông tin user
    if (el("dlgName")) el("dlgName").value = loggedName || "";
    if (el("dlgPhone")) el("dlgPhone").value = loggedPhone || "";
    if (el("dlgEmail")) el("dlgEmail").value = loggedEmail || ""; // ✅ Điền Email vào ô input

    if (el("dlgSlot")) el("dlgSlot").value = "";

    showMsg("", "");

    const dlg = el("bookDlg");
    if (dlg) dlg.showModal();

    await loadSlotsForPopup();
}

function closeDlg() {
    const dlg = el("bookDlg");
    if (dlg) dlg.close();
    state.selectedTable = null;
    showMsg("", "");
}

async function submitBooking() {
    showMsg("", "");

    const nameEl = el("dlgName");
    const phoneEl = el("dlgPhone");
    const slotEl = el("dlgSlot");

    const name = (nameEl.value || "").trim();
    const phone = (phoneEl.value || "").trim();
    const slotVal = slotEl.value;

    if (!state.selectedTable) return showMsg("err", "Vui lòng chọn bàn lại.");
    if (!name) return showMsg("err", "Vui lòng nhập tên khách hàng.");
    if (phone.length < 8) return showMsg("err", "Số điện thoại không hợp lệ.");
    if (!slotVal) return showMsg("err", "Vui lòng chọn khung giờ ăn.");

    let slot;
    try { slot = JSON.parse(slotVal); }
    catch { return showMsg("err", "Dữ liệu suất giờ bị lỗi."); }

    // ✅ Backend mới không cần gửi Email trong body JSON nữa (nó tự tra cứu theo ID)
    // Nhưng gửi CustomerName và Phone là bắt buộc
    const body = {
        tableNumber: state.selectedTable,
        customerName: name,
        phone: phone,
        startTime: slot.startLocal,
        hours: slot.hours
    };

    const btn = el("dlgSubmitBtn");
    btn.disabled = true;
    const originalText = btn.innerHTML;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin me-2"></i>Đang xử lý...';

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

        // Thành công -> Xóa cache để load lại dữ liệu mới nhất
        state.slotCache.clear();

        showMsg("ok", "🎉 Đặt bàn thành công! Vui lòng kiểm tra Email.");

        await loadTables();

        // Đóng popup sau 1.5s
        setTimeout(() => {
            closeDlg();
            // Reset nút
            btn.disabled = false;
            btn.innerHTML = originalText;
        }, 1500);
    }
    catch (e) {
        showMsg("err", "Lỗi kết nối: " + e);
        btn.disabled = false;
        btn.innerHTML = originalText;
    }
}

// Expose hàm ra global để gọi trong onlick="" của HTML
window.openBook = openBook;

// ====== SignalR (Realtime) ======
const connection = new signalR.HubConnectionBuilder()
    .withUrl(baseUrl + "/hubs/booking")
    .withAutomaticReconnect()
    .build();

const onRealtime = async () => {
    // Khi có tín hiệu thay đổi, xóa cache và load lại bàn
    state.slotCache.clear();
    await loadTables();

    // Nếu popup đang mở thì load lại cả dropdown giờ
    const dlg = el("bookDlg");
    if (dlg && dlg.open) await loadSlotsForPopup();
};

connection.on("TableUpdated", onRealtime);
connection.on("ReservationCreated", onRealtime);
connection.on("ReservationUpdated", onRealtime);
connection.on("ReservationCanceled", onRealtime);

// ====== INIT ======
document.addEventListener("DOMContentLoaded", async () => {
    // Set ngày mặc định là hôm nay
    const dateInput = el("pageDate");
    if (dateInput) {
        const pad2 = (n) => String(n).padStart(2, "0");
        const d = new Date();
        const todayVal = `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;

        dateInput.min = todayVal;
        dateInput.value = todayVal;

        // Sự kiện đổi ngày -> Load lại bảng
        dateInput.addEventListener("change", async () => {
            state.slotCache.clear();
            await loadTables();
            const dlg = el("bookDlg");
            if (dlg && dlg.open) await loadSlotsForPopup();
        });
    }

    // Sự kiện bộ lọc
    const filterEl = el("filter");
    if (filterEl) filterEl.addEventListener("change", loadTables);

    // Sự kiện Popup
    const closeBtn = el("dlgCloseX");
    if (closeBtn) closeBtn.addEventListener("click", closeDlg);

    const cancelBtn = el("dlgCancel");
    if (cancelBtn) cancelBtn.addEventListener("click", closeDlg);

    const submitBtn = el("dlgSubmitBtn");
    if (submitBtn) submitBtn.addEventListener("click", submitBooking);

    // ❌ ĐÃ BỎ: Không còn el("btnLogout") nữa nên xóa listener đi để tránh lỗi JS

    // Load dữ liệu lần đầu
    await loadTables();

    // Start SignalR
    try { await connection.start(); }
    catch { console.log("SignalR start failed (offline mode)"); }
});