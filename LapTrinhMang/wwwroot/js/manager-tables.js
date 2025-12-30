const cfg = window.MANAGER_CFG || {};
const baseUrl = cfg.baseUrl || location.origin;
const el = (id) => document.getElementById(id);

// --- STATE ---
const state = {
    tables: [],
    selectedTable: null
};

// --- INIT ---
document.addEventListener("DOMContentLoaded", async () => {
    // 1. Set ngày mặc định là hôm nay
    const d = new Date();
    const today = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
    if (!el("pageDate").value) {
        el("pageDate").value = today;
    }

    // 2. Gán sự kiện
    el("pageDate").addEventListener("change", loadTables);
    el("filter").addEventListener("change", renderGrid);
    el("btnResReload").addEventListener("click", loadReservations);
    el("resFilter").addEventListener("change", loadReservations);

    // Đóng dialog
    el("resCloseX").addEventListener("click", () => el("resDlg").close());
    el("btnResClose").addEventListener("click", () => el("resDlg").close());

    // Logout (Nếu bạn bỏ comment nút logout trong HTML)
    const btnLogout = el("btnLogout");
    if (btnLogout) {
        btnLogout.addEventListener("click", async () => {
            await fetch(`${baseUrl}/AuthView/Logout`, { method: "POST" }); // Sửa lại đường dẫn logout của bạn cho đúng
            location.reload();
        });
    }

    // 3. Load dữ liệu lần đầu
    await loadTables();

    // 4. Kết nối SignalR
    await startSignalR();
});

// --- API & LOGIC ---
async function loadTables() {
    const dateVal = el("pageDate").value;
    const grid = el("grid");

    // Hiệu ứng loading
    grid.innerHTML = '<div class="loading">Đang tải dữ liệu...</div>';

    try {
        const res = await fetch(`${baseUrl}/api/manager/tables?date=${dateVal}`);
        if (!res.ok) throw new Error("Lỗi tải dữ liệu");
        state.tables = await res.json();
        renderGrid();
    } catch (err) {
        grid.innerHTML = `<div class="empty"><div class="empty-text text-danger">Lỗi: ${err.message}</div></div>`;
    }
}

function renderGrid() {
    const filterVal = el("filter").value;
    const grid = el("grid");

    // Lọc Client-side
    let items = state.tables;
    if (filterVal) {
        items = items.filter(t => t.status === filterVal);
    }

    if (items.length === 0) {
        grid.innerHTML = `
            <div class="empty" style="grid-column: 1/-1;">
                <div class="empty-icon"><i class="fas fa-search"></i></div>
                <div class="empty-text">Không tìm thấy bàn nào theo bộ lọc này.</div>
            </div>`;
        return;
    }

    // Render HTML khớp với CSS mới (.tcard, .badge...)
    grid.innerHTML = items.map(t => {
        // Xác định class và text hiển thị
        let statusClass = t.status.toLowerCase(); // available, pending, reserved, full
        let badgeClass = statusClass;
        let statusLabel = "";
        let noteText = "";

        switch (t.status) {
            case "Pending":
                statusLabel = "CHỜ DUYỆT";
                noteText = `<span style="color:var(--pending)">Có ${t.pendingCount} đơn cần xử lý ngay!</span>`;
                break;
            case "Reserved":
                statusLabel = "ĐÃ ĐẶT";
                noteText = `Sắp tới có khách (${t.approvedCount} đơn)`;
                break;
            case "Full":
                statusLabel = "HẾT CHỖ";
                noteText = "Đã kín lịch hôm nay";
                break;
            default: // Available
                statusLabel = "CÒN TRỐNG";
                noteText = "Sẵn sàng đón khách";
                statusClass = "available";
                badgeClass = "available";
                break;
        }

        // Icon loại bàn
        let typeIcon = t.type === "VIP" ? '<i class="fas fa-crown text-warning"></i>' :
            t.type === "VVIP" ? '<i class="fas fa-gem text-danger"></i>' : '';

        return `
        <div class="tcard ${statusClass}" onclick="openResModal(${t.tableNumber})">
            <div class="tcHead">
                <div class="tcName">Bàn ${t.tableNumber} ${typeIcon}</div>
                <div class="tcSeat" title="Sức chứa">
                    <i class="fas fa-user-friends"></i> ${t.capacity}
                </div>
            </div>
            
            <div class="tcStat">
                <span class="badge ${badgeClass}">${statusLabel}</span>
            </div>

            <div class="tcAvail">
                Còn <strong>${t.slotCount}</strong> suất trống
            </div>

            <div class="tcNote">
                ${noteText}
            </div>
        </div>
        `;
    }).join("");
}

// --- MODAL CHI TIẾT ---
async function openResModal(tableNum) {
    state.selectedTable = tableNum;
    el("resTitle").textContent = `Bàn số ${tableNum}`;
    el("resHint").textContent = `Danh sách đơn ngày ${el("pageDate").value}`;
    el("resDlg").showModal();
    await loadReservations();
}

async function loadReservations() {
    const tableNum = state.selectedTable;
    const dateVal = el("pageDate").value;
    const statusVal = el("resFilter").value;
    const listEl = el("resList");

    listEl.innerHTML = '<div class="loading">Đang tải...</div>';

    try {
        let url = `${baseUrl}/api/manager/reservations?date=${dateVal}&tableNumber=${tableNum}`;
        if (statusVal) url += `&status=${statusVal}`;

        const res = await fetch(url);
        const data = await res.json();

        if (data.length === 0) {
            listEl.innerHTML = '<div class="empty"><div class="empty-text">Không có đơn đặt nào.</div></div>';
            return;
        }

        listEl.innerHTML = data.map(r => {
            const timeStr = new Date(r.startTime).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

            // Nút bấm hành động
            let actions = '';
            if (r.status === 'Pending') {
                actions = `
                <div class="resActions">
                    <button class="btnSmall approve" onclick="approve(${r.id})"><i class="fas fa-check"></i> Duyệt</button>
                    <button class="btnSmall cancel" onclick="cancel(${r.id})"><i class="fas fa-times"></i> Hủy</button>
                </div>`;
            }

            // Màu trạng thái text
            let statusColor = "var(--text-secondary)";
            if (r.status === 'Pending') statusColor = "var(--pending)";
            if (r.status === 'Approved') statusColor = "var(--success)";
            if (r.status === 'Canceled') statusColor = "var(--danger)";

            return `
            <div class="resItem">
                <div class="resHead">
                    <div class="resName">${r.customerName}</div>
                    <div style="font-weight:700; color:${statusColor}">${r.status.toUpperCase()}</div>
                </div>
                <div class="resInfo">
                    <div><i class="fas fa-clock"></i> ${timeStr}</div>
                    <div><i class="fas fa-phone"></i> ${r.phone}</div>
                </div>
                ${actions}
            </div>
            `;
        }).join("");

    } catch (e) {
        console.error(e);
        listEl.innerHTML = '<div class="empty-text text-danger">Lỗi tải chi tiết.</div>';
    }
}

// --- ACTIONS ---
async function approve(id) {
    if (!confirm("Duyệt đơn này?")) return;
    try {
        await fetch(`${baseUrl}/api/manager/reservations/${id}/approve`, { method: "PUT" });
        await loadReservations();
        await loadTables();
    } catch (e) { alert("Lỗi khi duyệt"); }
}

async function cancel(id) {
    if (!confirm("Hủy đơn này?")) return;
    try {
        await fetch(`${baseUrl}/api/manager/reservations/${id}/cancel`, { method: "PUT" });
        await loadReservations();
        await loadTables();
    } catch (e) { alert("Lỗi khi hủy"); }
}

// --- SIGNALR ---
async function startSignalR() {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(baseUrl + "/hubs/booking")
        .withAutomaticReconnect()
        .build();

    connection.on("TableUpdated", async () => {
        await loadTables();
    });

    connection.on("ReservationCreated", async () => {
        await loadTables(); 
    });

    connection.on("ReservationUpdated", async () => {
        if (el("resDlg").open) await loadReservations();
        await loadTables();
    });

    try {
        await connection.start();
        console.log("SignalR Connected");
    } catch (err) {
        console.error("SignalR Error", err);
    }
}

// Expose functions for HTML onclick
window.openResModal = openResModal;
window.approve = approve;
window.cancel = cancel;